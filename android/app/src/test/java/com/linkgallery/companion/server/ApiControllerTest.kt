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
        assertTrue(response.body.contains(""""nextCursor":"next-page""""))
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
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null)),
                MediaThumbnailResult.Found(jpeg),
            ),
        )

        val response = runSuspend {
            controller.handle("GET", "/api/v1/media/media-1/thumbnail?width=320&height=240")
        }
        val invalid = runSuspend {
            controller.handle("GET", "/api/v1/media/media-1/thumbnail?width=4096&height=240")
        }

        assertEquals(200, response.status)
        assertEquals("image/jpeg", response.contentType)
        assertTrue(jpeg.contentEquals(response.binaryBody))
        assertEquals(400, invalid.status)
    }

    @Test
    fun contentResponseSupportsFullAndRangeReads() {
        val bytes = "0123456789".toByteArray()
        val offsets = mutableListOf<Long>()
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null)),
                contentResult = MediaContentResult.Found(
                    MediaContent(bytes.size.toLong(), "video/mp4") { offset ->
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
                mapOf("Range" to "bytes=3-6"),
            )
        }

        assertEquals(200, full.status)
        assertEquals("video/mp4", full.contentType)
        assertEquals(10L, full.contentLength)
        assertEquals("bytes", full.headers["Accept-Ranges"])
        assertEquals("0123456789", full.binaryStream!!.use { String(it.readBytes()) })
        assertEquals(206, partial.status)
        assertEquals(4L, partial.contentLength)
        assertEquals("bytes 3-6/10", partial.headers["Content-Range"])
        assertEquals("3456", partial.binaryStream!!.use { String(it.readBytes()) })
        assertEquals(listOf(0L, 3L), offsets)
    }

    @Test
    fun invalidOrUnsatisfiableRangesReturn416WithoutOpeningContent() {
        var opened = false
        val controller = controller(
            repository = FakeMediaRepository(
                MediaPageResult.Success(MediaPage(emptyList(), null)),
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
        assertEquals("bytes */10", malformed.headers["Content-Range"])
        assertEquals(416, pastEnd.status)
        assertFalse(opened)
    }

    @Test
    fun contentPermissionAndMissingItemsUseNormalizedProblems() {
        val denied = runSuspend {
            controller(
                repository = FakeMediaRepository(
                    MediaPageResult.Success(MediaPage(emptyList(), null)),
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
        repository: MediaRepository = FakeMediaRepository(
            MediaPageResult.Success(MediaPage(listOf(MEDIA), "next-page")),
        ),
    ): ApiController = ApiController(
        deviceInfoProvider = DeviceInfoProvider {
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
        mediaRepository = repository,
    )

    private class FakeMediaRepository(
        private val pageResult: MediaPageResult,
        private val thumbnailResult: MediaThumbnailResult = MediaThumbnailResult.NotFound,
        private val contentResult: MediaContentResult = MediaContentResult.NotFound,
    ) : MediaRepository {
        override suspend fun getPage(query: MediaQuery): MediaPageResult = pageResult

        override suspend fun getById(id: String): MediaItemResult = MediaItemResult.NotFound

        override suspend fun getThumbnail(
            id: String,
            width: Int,
            height: Int,
        ): MediaThumbnailResult = thumbnailResult

        override suspend fun getContent(id: String): MediaContentResult = contentResult
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
