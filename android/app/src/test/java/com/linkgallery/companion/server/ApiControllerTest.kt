package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaItemResult
import com.linkgallery.companion.media.MediaContent
import com.linkgallery.companion.media.MediaContentResult
import com.linkgallery.companion.media.MediaPage
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRecord
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaThumbnailResult
import com.linkgallery.companion.media.MediaType
import java.io.ByteArrayInputStream
import java.time.Instant
import java.util.concurrent.CountDownLatch
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ApiControllerTest {
    @Test
    fun publicInfoResponseIsMinimalAndDoesNotRequireMediaPermission() {
        val response = runSuspend {
            controller(
                deviceInfoProvider = DeviceInfoProvider {
                    DeviceInfoResult.PermissionDenied(setOf("android.permission.READ_MEDIA_IMAGES"))
                },
            ).handle("GET", "/api/v1/public/info")
        }

        assertEquals(200, response.status)
        assertEquals(
            """{"deviceId":"DEVICEID","deviceName":"Pixel","manufacturer":"Google","model":"Pixel 9","apiVersion":1,"serverVersion":"0.1.0","instanceId":"instance-1","pairingAvailable":false,"certificateFingerprint":"AA:BB"}""",
            response.body,
        )
        assertFalse(response.body.contains("mediaCount"))
        assertFalse(response.body.contains("token", ignoreCase = true))
    }

    @Test
    fun deviceResponseMatchesContract() {
        val response = runSuspend {
            controller().handle("GET", "/api/v1/device")
        }

        assertEquals(200, response.status)
        assertEquals(
            """{"id":"device-1","name":"Pixel","platform":"android","model":"Pixel 9","battery":73,"mediaCount":1}""",
            response.body,
        )
    }

    @Test
    fun mediaResponseIsPagedAndUsesContractNames() {
        val response = runSuspend {
            controller().handle("GET", "/api/v1/media?limit=1&type=image")
        }

        assertEquals(200, response.status)
        assertTrue(response.body.contains(""""fileName":"photo \"one\".jpg""""))
        assertTrue(response.body.contains(""""type":"image""""))
        assertTrue(response.body.contains(""""takenAt":"2026-06-30T00:00:00Z""""))
        assertTrue(response.body.contains(""""thumbnailUrl":"/api/v1/media/media-1/thumbnail?size=256""""))
        assertTrue(response.body.contains(""""nextCursor":"next-page""""))
        assertTrue(response.body.contains(""""hasMore":true"""))
        assertTrue(response.body.contains(""""total":2"""))
    }

    @Test
    fun mediaResponseAcceptsExplicitBeforeCursor() {
        val repository = RecordingMediaRepository(
            MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
        )

        val response = runSuspend {
            controller(repository = repository)
                .handle("GET", "/api/v1/media?beforeSortTime=1234&beforeId=42")
        }

        assertEquals(200, response.status)
        assertEquals(1234L, repository.lastQuery?.before?.sortTimestampEpochMillis)
        assertEquals(42L, repository.lastQuery?.before?.mediaStoreId)
    }

    @Test
    fun invalidParametersReturnNormalizedProblem() {
        val response = runSuspend {
            controller().handle("GET", "/api/v1/media?limit=201")
        }

        assertEquals(400, response.status)
        assertEquals(
            """{"code":"invalid_parameter","message":"The 'limit' query parameter is invalid."}""",
            response.body,
        )
    }

    @Test
    fun malformedQueryEncodingReturnsNormalizedProblem() {
        val response = runSuspend {
            controller().handle("GET", "/api/v1/media?cursor=%ZZ")
        }

        assertEquals(400, response.status)
        assertEquals(
            """{"code":"invalid_parameter","message":"The query string is invalid."}""",
            response.body,
        )
    }

    @Test
    fun mediaPermissionFailureReturnsNormalizedProblem() {
        val response = runSuspend {
            controller(
                repository = FakeMediaRepository(
                    MediaPageResult.PermissionDenied(setOf("android.permission.READ_MEDIA_IMAGES")),
                ),
            ).handle("GET", "/api/v1/media")
        }

        assertEquals(403, response.status)
        assertTrue(response.body.contains(""""code":"media_permission_denied""""))
    }

    @Test
    fun thumbnailResponseReturnsJpegBytesAndValidatesDimensions() {
        val jpeg = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 1, 2)
        val repository = FakeMediaRepository(
            MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
            MediaThumbnailResult.Found(jpeg, "\"thumb-v1\""),
        )
        val controller = controller(
            repository = repository,
        )

        val response = runSuspend {
            controller.handle("GET", "/api/v1/media/media-1/thumbnail?size=256")
        }
        val notModified = runSuspend {
            controller.handle(
                "GET",
                "/api/v1/media/media-1/thumbnail?size=256",
                mapOf("If-None-Match" to "\"thumb-v1\""),
            )
        }
        val invalid = runSuspend {
            controller.handle("GET", "/api/v1/media/media-1/thumbnail?width=4096&height=240")
        }

        assertEquals(200, response.status)
        assertEquals("image/jpeg", response.contentType)
        assertEquals(256 to 256, repository.lastThumbnailSize)
        assertTrue(jpeg.contentEquals(response.binaryBody))
        assertEquals("\"thumb-v1\"", response.headers["ETag"])
        assertEquals("public, max-age=86400", response.headers["Cache-Control"])
        assertEquals(304, notModified.status)
        assertEquals(0L, notModified.contentLength)
        assertEquals(400, invalid.status)
    }

    @Test
    fun contentResponseSupportsFullAndRangeReads() {
        val bytes = "0123456789".toByteArray()
        val offsets = mutableListOf<Long>()
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
                contentResult = MediaContentResult.Found(
                    MediaContent(bytes.size.toLong(), "video/mp4", "\"entity-v1\"") { offset ->
                        offsets += offset
                        ByteArrayInputStream(bytes, offset.toInt(), bytes.size - offset.toInt())
                    },
                ),
            ),
        )

        val full = runSuspend {
            controller.handle("GET", "/api/v1/media/media-1/content")
        }
        val partial = runSuspend {
            controller.handle(
                "GET",
                "/api/v1/media/media-1/content",
                mapOf("Range" to "bytes=3-6", "If-Range" to "\"entity-v1\""),
            )
        }
        val changed = runSuspend {
            controller.handle(
                "GET",
                "/api/v1/media/media-1/content",
                mapOf("Range" to "bytes=3-6", "If-Range" to "\"stale\""),
            )
        }

        assertEquals(200, full.status)
        assertEquals("video/mp4", full.contentType)
        assertEquals(10L, full.contentLength)
        assertEquals("bytes", full.headers["Accept-Ranges"])
        assertEquals("\"entity-v1\"", full.headers["ETag"])
        assertEquals("0123456789", full.binaryStream!!.use { String(it.readBytes()) })
        assertEquals(206, partial.status)
        assertEquals(4L, partial.contentLength)
        assertEquals("bytes 3-6/10", partial.headers["Content-Range"])
        assertEquals("3456", partial.binaryStream!!.use { String(it.readBytes()) })
        assertEquals(200, changed.status)
        assertEquals("0123456789", changed.binaryStream!!.use { String(it.readBytes()) })
        assertEquals(listOf(0L, 3L, 0L), offsets)
    }

    @Test
    fun invalidOrUnsatisfiableRangesReturn416WithoutOpeningContent() {
        var opened = false
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
                contentResult = MediaContentResult.Found(
                    MediaContent(10, "video/mp4") {
                        opened = true
                        ByteArrayInputStream(ByteArray(10))
                    },
                ),
            ),
        )

        val malformed = runSuspend {
            controller.handle(
                "GET",
                "/api/v1/media/media-1/content",
                mapOf("range" to "bytes=4-2"),
            )
        }
        val pastEnd = runSuspend {
            controller.handle(
                "GET",
                "/api/v1/media/media-1/content",
                mapOf("Range" to "bytes=10-"),
            )
        }

        assertEquals(416, malformed.status)
        assertEquals("bytes", malformed.headers["Accept-Ranges"])
        assertEquals("bytes */10", malformed.headers["Content-Range"])
        assertEquals(416, pastEnd.status)
        assertFalse(opened)
    }

    @Test
    fun rangeBeyondTwoGigabytesIsOpenedAtOffsetWithoutBufferingWholeContent() {
        val contentLength = 3L * 1024 * 1024 * 1024
        val requestedOffset = contentLength - 4
        var openedAt: Long? = null
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
                contentResult = MediaContentResult.Found(
                    MediaContent(contentLength, "video/mp4") { offset ->
                        openedAt = offset
                        ByteArrayInputStream("tail".toByteArray())
                    },
                ),
            ),
        )

        val response = runSuspend {
            controller.handle(
                "GET",
                "/api/v1/media/media-1/content",
                mapOf("Range" to "bytes=$requestedOffset-"),
            )
        }

        assertEquals(206, response.status)
        assertEquals(requestedOffset, openedAt)
        assertEquals(4L, response.contentLength)
        assertEquals(
            "bytes $requestedOffset-${contentLength - 1}/$contentLength",
            response.headers["Content-Range"],
        )
        assertEquals("tail", response.binaryStream!!.use { String(it.readBytes()) })
    }

    @Test
    fun contentRemovedBeforeStreamOpenReturns404() {
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
                contentResult = MediaContentResult.Found(
                    MediaContent(10, "video/mp4") { null },
                ),
            ),
        )

        val response = runSuspend {
            controller.handle("GET", "/api/v1/media/media-1/content")
        }

        assertEquals(404, response.status)
        assertEquals(null, response.binaryStream)
    }

    @Test
    fun suffixRangeUsesOnlyTheRequestedTail() {
        val bytes = "0123456789".toByteArray()
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
                contentResult = MediaContentResult.Found(
                    MediaContent(bytes.size.toLong(), "video/mp4") { offset ->
                        ByteArrayInputStream(bytes, offset.toInt(), bytes.size - offset.toInt())
                    },
                ),
            ),
        )

        val response = runSuspend {
            controller.handle(
                "GET",
                "/api/v1/media/media-1/content",
                mapOf("Range" to "bytes=-3"),
            )
        }

        assertEquals(206, response.status)
        assertEquals("bytes 7-9/10", response.headers["Content-Range"])
        assertEquals("789", response.binaryStream!!.use { String(it.readBytes()) })
    }

    @Test
    fun contentPermissionAndMissingItemsUseNormalizedProblems() {
        val denied = runSuspend {
            controller(
                repository = FakeMediaRepository(
                    MediaPageResult.Success(MediaPage(emptyList(), null, false, 0)),
                    contentResult = MediaContentResult.PermissionDenied(setOf("READ_MEDIA_VIDEO")),
                ),
            ).handle("GET", "/api/v1/media/media-1/content")
        }
        val missing = runSuspend {
            controller().handle("GET", "/api/v1/media/missing/content")
        }

        assertEquals(403, denied.status)
        assertTrue(denied.body.contains("media_permission_denied"))
        assertEquals(404, missing.status)
    }

    @Test
    fun writesAndUnknownMediaRoutesDoNotExist() {
        val controller = controller()
        val delete = runSuspend { controller.handle("DELETE", "/api/v1/media") }
        val upload = runSuspend { controller.handle("POST", "/api/v1/media/upload") }

        assertEquals(404, delete.status)
        assertEquals(404, upload.status)
        assertFalse(ReadOnlyRoutePolicy.permits("PUT", "/api/v1/media"))
    }

    private fun controller(
        deviceInfoProvider: DeviceInfoProvider = DeviceInfoProvider {
            DeviceInfoResult.Success(
                DeviceInfo(
                    id = "device-1",
                    name = "Pixel",
                    model = "Pixel 9",
                    battery = 73,
                    mediaCount = 1,
                ),
            )
        },
        repository: MediaRepository = FakeMediaRepository(
            MediaPageResult.Success(MediaPage(listOf(MEDIA), "next-page", true, 2)),
        ),
    ): ApiController = ApiController(
        publicDeviceInfoProvider = PublicDeviceInfoProvider {
            PublicDeviceInfo(
                deviceId = "DEVICEID",
                deviceName = "Pixel",
                manufacturer = "Google",
                model = "Pixel 9",
                apiVersion = 1,
                serverVersion = "0.1.0",
                instanceId = "instance-1",
                pairingAvailable = false,
                certificateFingerprint = "AA:BB",
            )
        },
        deviceInfoProvider = deviceInfoProvider,
        mediaRepository = repository,
    )

    private class FakeMediaRepository(
        private val pageResult: MediaPageResult,
        private val thumbnailResult: MediaThumbnailResult = MediaThumbnailResult.NotFound,
        private val contentResult: MediaContentResult = MediaContentResult.NotFound,
    ) : MediaRepository {
        var lastThumbnailSize: Pair<Int, Int>? = null

        override suspend fun getPage(query: MediaQuery): MediaPageResult = pageResult

        override suspend fun getById(id: String): MediaItemResult = MediaItemResult.NotFound

        override suspend fun getThumbnail(
            id: String,
            width: Int,
            height: Int,
        ): MediaThumbnailResult {
            lastThumbnailSize = width to height
            return thumbnailResult
        }

        override suspend fun getContent(id: String): MediaContentResult = contentResult
    }

    private class RecordingMediaRepository(
        private val pageResult: MediaPageResult,
    ) : MediaRepository {
        var lastQuery: MediaQuery? = null

        override suspend fun getPage(query: MediaQuery): MediaPageResult {
            lastQuery = query
            return pageResult
        }

        override suspend fun getById(id: String): MediaItemResult = MediaItemResult.NotFound
    }

    private companion object {
        val MEDIA = MediaRecord(
            id = "media-1",
            fileName = "photo \"one\".jpg",
            type = MediaType.IMAGE,
            fileSize = 1234,
            width = 800,
            height = 600,
            takenAt = Instant.parse("2026-06-30T00:00:00Z"),
            modifiedAt = Instant.parse("2026-06-30T01:00:00Z"),
            thumbnailUrl = "/api/v1/media/media-1/thumbnail?size=256",
        )

        fun <T> runSuspend(block: suspend () -> T): T {
            val completed = CountDownLatch(1)
            var outcome: Result<T>? = null
            block.startCoroutine(
                object : Continuation<T> {
                    override val context = EmptyCoroutineContext

                    override fun resumeWith(result: Result<T>) {
                        outcome = result
                        completed.countDown()
                    }
                },
            )
            completed.await()
            return checkNotNull(outcome).getOrThrow()
        }
    }
}
