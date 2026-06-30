package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaItemResult
import com.linkgallery.companion.media.MediaContent
import com.linkgallery.companion.media.MediaContentResult
import com.linkgallery.companion.media.MediaPage
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRepository
import java.io.ByteArrayInputStream
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.util.concurrent.CountDownLatch
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LinkGalleryHttpServerTest {
    @Test
    fun servesJsonOverHttpAndCanBeStopped() {
        val server = LinkGalleryHttpServer(
            controller = ApiController(
                deviceInfoProvider = DeviceInfoProvider {
                    DeviceInfoResult.Success(
                        DeviceInfo("device-1", "Test phone", "Test model", 50, 0),
                    )
                },
                mediaRepository = EmptyMediaRepository,
            ),
            config = HttpServerConfig(port = 0, requestTimeoutMilliseconds = 2_000),
            logger = RequestLogger { _, _, _, _ -> },
        )

        try {
            server.start()
            val port = checkNotNull(server.localPort)
            val response = Socket("127.0.0.1", port).use { socket ->
                socket.getOutputStream().apply {
                    write(
                        "GET /api/v1/device HTTP/1.1\r\nHost: localhost\r\n\r\n"
                            .toByteArray(StandardCharsets.US_ASCII),
                    )
                    flush()
                }
                socket.getInputStream().bufferedReader().readText()
            }

            assertTrue(response.startsWith("HTTP/1.1 200 OK"))
            assertTrue(response.contains("Content-Type: application/json"))
            assertTrue(response.endsWith(""""mediaCount":0}"""))
        } finally {
            server.stop()
        }

        assertFalse(server.isRunning)
    }

    @Test
    fun returnsNormalizedTimeoutResponse() {
        val neverCompletes = CountDownLatch(1)
        val server = LinkGalleryHttpServer(
            controller = ApiController(
                deviceInfoProvider = DeviceInfoProvider {
                    neverCompletes.await()
                    DeviceInfoResult.Success(DeviceInfo("id", "name", null, null, 0))
                },
                mediaRepository = EmptyMediaRepository,
            ),
            config = HttpServerConfig(port = 0, requestTimeoutMilliseconds = 100),
            logger = RequestLogger { _, _, _, _ -> },
        )

        try {
            server.start()
            val response = Socket("127.0.0.1", checkNotNull(server.localPort)).use { socket ->
                socket.soTimeout = 2_000
                socket.getOutputStream().apply {
                    write(
                        "GET /api/v1/device HTTP/1.1\r\nHost: localhost\r\n\r\n"
                            .toByteArray(StandardCharsets.US_ASCII),
                    )
                    flush()
                }
                socket.getInputStream().bufferedReader().readText()
            }

            assertTrue(response.startsWith("HTTP/1.1 504 Gateway Timeout"))
            assertTrue(response.contains(""""code":"request_timeout""""))
        } finally {
            neverCompletes.countDown()
            server.stop()
        }
    }

    @Test
    fun streamsRequestedByteRangeWithHttpHeaders() {
        val repository = object : MediaRepository {
            override suspend fun getPage(query: MediaQuery): MediaPageResult =
                MediaPageResult.Success(MediaPage(emptyList(), null))

            override suspend fun getById(id: String): MediaItemResult =
                MediaItemResult.NotFound

            override suspend fun getContent(id: String): MediaContentResult =
                MediaContentResult.Found(
                    MediaContent(10, "video/mp4") { offset ->
                        val bytes = "0123456789".toByteArray()
                        ByteArrayInputStream(bytes, offset.toInt(), bytes.size - offset.toInt())
                    },
                )
        }
        val server = LinkGalleryHttpServer(
            controller = ApiController(
                deviceInfoProvider = DeviceInfoProvider {
                    DeviceInfoResult.Success(DeviceInfo("id", "name", null, null, 1))
                },
                mediaRepository = repository,
            ),
            config = HttpServerConfig(port = 0, requestTimeoutMilliseconds = 2_000),
            logger = RequestLogger { _, _, _, _ -> },
        )

        try {
            server.start()
            val response = Socket("127.0.0.1", checkNotNull(server.localPort)).use { socket ->
                socket.getOutputStream().apply {
                    write(
                        (
                            "GET /api/v1/media/video/content HTTP/1.1\r\n" +
                                "Host: localhost\r\nRange: bytes=4-7\r\n\r\n"
                            ).toByteArray(StandardCharsets.US_ASCII),
                    )
                    flush()
                }
                socket.getInputStream().readBytes()
            }
            val text = String(response, StandardCharsets.US_ASCII)

            assertTrue(text.startsWith("HTTP/1.1 206 Partial Content"))
            assertTrue(text.contains("Content-Length: 4\r\n"))
            assertTrue(text.contains("Content-Range: bytes 4-7/10\r\n"))
            assertTrue(text.endsWith("4567"))
        } finally {
            server.stop()
        }
    }

    private object EmptyMediaRepository : MediaRepository {
        override suspend fun getPage(query: MediaQuery): MediaPageResult =
            MediaPageResult.Success(MediaPage(emptyList(), null))

        override suspend fun getById(id: String): MediaItemResult = MediaItemResult.NotFound
    }
}
