package com.linkgallery.companion.server

import android.util.Log
import java.io.BufferedReader
import java.io.IOException
import java.io.InputStreamReader
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.util.Locale
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

data class HttpServerConfig(
    val port: Int = 39570,
    val requestTimeoutMilliseconds: Long = 10_000,
    val maxConcurrentRequests: Int = 4,
) {
    init {
        require(port in 0..65535) { "Port must be between 0 and 65535." }
        require(requestTimeoutMilliseconds > 0) { "Request timeout must be positive." }
        require(maxConcurrentRequests > 0) { "Concurrent request limit must be positive." }
    }
}

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
    private var requestExecutor = newRequestExecutor()
    private var handlerExecutor = Executors.newFixedThreadPool(config.maxConcurrentRequests)

    val isRunning: Boolean
        get() = synchronized(lock) { serverSocket?.isClosed == false }

    val localPort: Int?
        get() = synchronized(lock) { serverSocket?.localPort }

    fun start() {
        synchronized(lock) {
            if (serverSocket?.isClosed == false) return
            if (acceptExecutor.isShutdown) acceptExecutor = Executors.newSingleThreadExecutor()
            if (requestExecutor.isShutdown) {
                requestExecutor = newRequestExecutor()
            }
            if (handlerExecutor.isShutdown) {
                handlerExecutor = Executors.newFixedThreadPool(config.maxConcurrentRequests)
            }
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
                try {
                    requestExecutor.execute { handleClient(client) }
                } catch (_: RejectedExecutionException) {
                    runCatching { client.close() }
                }
            } catch (_: Exception) {
                if (!socket.isClosed) Log.w("LinkGalleryHttp", "Accept failed")
            }
        }
    }

    private fun newRequestExecutor() = ThreadPoolExecutor(
        config.maxConcurrentRequests,
        config.maxConcurrentRequests,
        0L,
        TimeUnit.MILLISECONDS,
        ArrayBlockingQueue(config.maxConcurrentRequests),
    )

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
                val headers = mutableMapOf<String, String>()
                while (true) {
                    val line = reader.readLine() ?: break
                    headerBytes += line.length
                    if (headerBytes > MAX_HEADER_BYTES) {
                        write(socket, ApiResponse(400, Json.problem("bad_request", "Request headers are too large.")))
                        status = 400
                        return
                    }
                    if (line.isEmpty()) break
                    val separator = line.indexOf(':')
                    if (separator <= 0) {
                        write(socket, ApiResponse(400, Json.problem("bad_request", "Malformed request header.")))
                        status = 400
                        return
                    }
                    headers[line.substring(0, separator).trim().lowercase(Locale.ROOT)] =
                        line.substring(separator + 1).trim()
                }

                val body = readBody(reader, headers)
                val response = executeController(method, target, headers, body)
                status = response.status
                write(socket, response)
            } catch (_: TimeoutException) {
                status = 504
                runCatching {
                    write(socket, ApiResponse(504, Json.problem("request_timeout", "The request timed out.")))
                }
            } catch (exception: BadHttpRequest) {
                status = 400
                runCatching {
                    write(socket, ApiResponse(400, Json.problem("bad_request", exception.message ?: "Bad request.")))
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

    private fun executeController(
        method: String,
        target: String,
        headers: Map<String, String>,
        body: String,
    ): ApiResponse {
        val task = handlerExecutor.submit<ApiResponse> {
            runSuspending { controller.handle(method, target, headers, body) }
        }
        return try {
            task.get(config.requestTimeoutMilliseconds, TimeUnit.MILLISECONDS)
        } catch (exception: TimeoutException) {
            task.cancel(true)
            throw exception
        }
    }

    private fun readBody(reader: BufferedReader, headers: Map<String, String>): String {
        val length = headers["content-length"]?.toIntOrNull() ?: return ""
        if (length < 0 || length > MAX_BODY_BYTES) {
            throw BadHttpRequest("Request body is too large.")
        }
        if (length == 0) return ""
        val buffer = CharArray(length)
        var offset = 0
        while (offset < length) {
            val count = reader.read(buffer, offset, length - offset)
            if (count < 0) throw BadHttpRequest("Request body ended early.")
            offset += count
        }
        return String(buffer)
    }

    private fun write(socket: Socket, response: ApiResponse) {
        val body = response.binaryBody ?: response.body.toByteArray(StandardCharsets.UTF_8)
        val contentLength = response.contentLength ?: body.size.toLong()
        val input = response.binaryStream
        val reason = when (response.status) {
            200 -> "OK"
            206 -> "Partial Content"
            304 -> "Not Modified"
            400 -> "Bad Request"
            401 -> "Unauthorized"
            403 -> "Forbidden"
            404 -> "Not Found"
            409 -> "Conflict"
            410 -> "Gone"
            416 -> "Range Not Satisfiable"
            429 -> "Too Many Requests"
            500 -> "Internal Server Error"
            504 -> "Gateway Timeout"
            else -> "Response"
        }
        var committed = false
        try {
            socket.getOutputStream().buffered().use { output ->
                output.write("HTTP/1.1 ${response.status} $reason\r\n".toByteArray())
                output.write("Content-Type: ${response.contentType}\r\n".toByteArray())
                output.write("Content-Length: $contentLength\r\n".toByteArray())
                response.headers.forEach { (name, value) ->
                    output.write("$name: $value\r\n".toByteArray())
                }
                output.write("Connection: close\r\n\r\n".toByteArray())
                output.flush()
                committed = true
                if (input != null) {
                    input.copyTo(output)
                } else {
                    output.write(body)
                }
                output.flush()
            }
        } catch (exception: IOException) {
            if (!committed) throw exception
        } finally {
            runCatching { input?.close() }
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
        const val MAX_BODY_BYTES = 32 * 1024
    }

    private class BadHttpRequest(message: String) : IllegalArgumentException(message)
}
