package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaItemResult
import com.linkgallery.companion.media.MediaPage
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRecord
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaThumbnailResult
import com.linkgallery.companion.media.MediaType
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
    fun writesAndUnimplementedMediaRoutesDoNotExist() {
        val controller = controller()
        val delete = runSuspend { controller.handle("DELETE", "/api/v1/media") }
        val upload = runSuspend { controller.handle("POST", "/api/v1/media/upload") }
        val content = runSuspend { controller.handle("GET", "/api/v1/media/abc/content") }

        assertEquals(404, delete.status)
        assertEquals(404, upload.status)
        assertEquals(404, content.status)
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
    ) : MediaRepository {
        override suspend fun getPage(query: MediaQuery): MediaPageResult = pageResult

        override suspend fun getById(id: String): MediaItemResult = MediaItemResult.NotFound

        override suspend fun getThumbnail(
            id: String,
            width: Int,
            height: Int,
        ): MediaThumbnailResult = thumbnailResult
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
