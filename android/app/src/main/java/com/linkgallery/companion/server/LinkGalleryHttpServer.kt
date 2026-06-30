package com.linkgallery.companion.server

import android.util.Log
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

data class HttpServerConfig(
    val port: Int = 39570,
    val requestTimeoutMilliseconds: Long = 10_000,
)

fun interface RequestLogger {
    fun log(method: String, target: String, status: Int, elapsedMilliseconds: Long)
}

object AndroidRequestLogger : RequestLogger {
    override fun log(method: String, target: String, status: Int, elapsedMilliseconds: Long) {
        Log.i("LinkGalleryHttp", "$method $target $status ${elapsedMilliseconds}ms")
    }
}

class LinkGalleryHttpServer(
    private val controller: ApiController,
    private val config: HttpServerConfig = HttpServerConfig(),
    private val logger: RequestLogger = AndroidRequestLogger,
) {
    private val lock = Any()
    private var serverSocket: ServerSocket? = null
    private var acceptExecutor = Executors.newSingleThreadExecutor()
    private var requestExecutor = Executors.newCachedThreadPool()
    private var handlerExecutor = Executors.newCachedThreadPool()

    val isRunning: Boolean
        get() = synchronized(lock) { serverSocket?.isClosed == false }

    val localPort: Int?
        get() = synchronized(lock) { serverSocket?.localPort }

    fun start() {
        synchronized(lock) {
            if (serverSocket?.isClosed == false) return
            if (acceptExecutor.isShutdown) acceptExecutor = Executors.newSingleThreadExecutor()
            if (requestExecutor.isShutdown) requestExecutor = Executors.newCachedThreadPool()
            if (handlerExecutor.isShutdown) handlerExecutor = Executors.newCachedThreadPool()
            val socket = ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(config.port))
            }
            serverSocket = socket
            acceptExecutor.execute { acceptConnections(socket) }
        }
    }

    fun stop() {
        synchronized(lock) {
            serverSocket?.close()
            serverSocket = null
            acceptExecutor.shutdownNow()
            requestExecutor.shutdownNow()
            handlerExecutor.shutdownNow()
        }
    }

    private fun acceptConnections(socket: ServerSocket) {
        while (!socket.isClosed) {
            try {
                val client = socket.accept()
                requestExecutor.execute { handleClient(client) }
            } catch (_: Exception) {
                if (!socket.isClosed) Log.w("LinkGalleryHttp", "Accept failed")
            }
        }
    }

    private fun handleClient(client: Socket) {
        client.use { socket ->
            socket.soTimeout = config.requestTimeoutMilliseconds.coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
            val startedAt = System.nanoTime()
            var method = "UNKNOWN"
            var target = "/"
            var status = 500
            try {
                val reader = BufferedReader(
                    InputStreamReader(socket.getInputStream(), StandardCharsets.US_ASCII),
                )
                val requestLine = reader.readLine() ?: return
                val parts = requestLine.split(' ')
                if (parts.size != 3 || !parts[2].startsWith("HTTP/1.")) {
                    write(socket, ApiResponse(400, Json.problem("bad_request", "Malformed request line.")))
                    status = 400
                    return
                }
                method = parts[0]
                target = parts[1]
                var headerBytes = requestLine.length
                while (true) {
                    val line = reader.readLine() ?: break
                    headerBytes += line.length
                    if (headerBytes > MAX_HEADER_BYTES) {
                        write(socket, ApiResponse(400, Json.problem("bad_request", "Request headers are too large.")))
                        status = 400
                        return
                    }
                    if (line.isEmpty()) break
                }

                val response = executeController(method, target)
                status = response.status
                write(socket, response)
            } catch (_: TimeoutException) {
                status = 504
                runCatching {
                    write(socket, ApiResponse(504, Json.problem("request_timeout", "The request timed out.")))
                }
            } catch (_: Exception) {
                status = 500
                runCatching {
                    write(socket, ApiResponse(500, Json.problem("internal_error", "The request failed.")))
                }
            } finally {
                val elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - startedAt)
                logger.log(method, target, status, elapsed)
            }
        }
    }

    private fun executeController(method: String, target: String): ApiResponse {
        val task = handlerExecutor.submit<ApiResponse> {
            runSuspending { controller.handle(method, target) }
        }
        return try {
            task.get(config.requestTimeoutMilliseconds, TimeUnit.MILLISECONDS)
        } catch (exception: TimeoutException) {
            task.cancel(true)
            throw exception
        }
    }

    private fun write(socket: Socket, response: ApiResponse) {
        val body = response.body.toByteArray(StandardCharsets.UTF_8)
        val reason = when (response.status) {
            200 -> "OK"
            400 -> "Bad Request"
            403 -> "Forbidden"
            404 -> "Not Found"
            500 -> "Internal Server Error"
            504 -> "Gateway Timeout"
            else -> "Response"
        }
        socket.getOutputStream().buffered().use { output ->
            output.write("HTTP/1.1 ${response.status} $reason\r\n".toByteArray())
            output.write("Content-Type: application/json; charset=utf-8\r\n".toByteArray())
            output.write("Content-Length: ${body.size}\r\n".toByteArray())
            output.write("Connection: close\r\n\r\n".toByteArray())
            output.write(body)
            output.flush()
        }
    }

    private fun <T> runSuspending(block: suspend () -> T): T {
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

    private companion object {
        const val MAX_HEADER_BYTES = 16 * 1024
    }
}
