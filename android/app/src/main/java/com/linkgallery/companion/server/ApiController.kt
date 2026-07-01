package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaContent
import com.linkgallery.companion.media.MediaContentResult
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaStoreCursor
import com.linkgallery.companion.media.MediaThumbnailResult
import com.linkgallery.companion.media.MediaType
import java.io.FilterInputStream
import java.io.InputStream
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

class ApiController(
    private val deviceInfoProvider: DeviceInfoProvider,
    private val mediaRepository: MediaRepository,
) {
    suspend fun handle(
        method: String,
        requestTarget: String,
        headers: Map<String, String> = emptyMap(),
    ): ApiResponse {
        val path = requestTarget.substringBefore('?')
        if (!ReadOnlyRoutePolicy.permits(method, path)) {
            return problem(404, "not_found", "The requested route does not exist.")
        }

        return when (path) {
            DEVICE_PATH -> getDevice()
            MEDIA_PATH -> {
                val parameters = try {
                    parseQuery(requestTarget.substringAfter('?', ""))
                } catch (_: IllegalArgumentException) {
                    return problem(400, "invalid_parameter", "The query string is invalid.")
                }
                getMedia(parameters)
            }
            else -> if (THUMBNAIL_PATH.matches(path)) {
                val (mediaId, parameters) = try {
                    val encodedId = checkNotNull(THUMBNAIL_PATH.matchEntire(path)).groupValues[1]
                    decode(encodedId) to parseQuery(requestTarget.substringAfter('?', ""))
                } catch (_: IllegalArgumentException) {
                    return problem(400, "invalid_parameter", "The query string is invalid.")
                }
                getThumbnail(mediaId, parameters)
            } else if (CONTENT_PATH.matches(path)) {
                val mediaId = try {
                    val encodedId = checkNotNull(CONTENT_PATH.matchEntire(path)).groupValues[1]
                    decode(encodedId)
                } catch (_: IllegalArgumentException) {
                    return problem(400, "invalid_parameter", "The media ID is invalid.")
                }
                getContent(
                    mediaId,
                    rangeHeader = header(headers, "Range"),
                    ifRangeHeader = header(headers, "If-Range"),
                )
            } else {
                problem(404, "not_found", "The requested route does not exist.")
            }
        }
    }

    private suspend fun getContent(
        mediaId: String,
        rangeHeader: String?,
        ifRangeHeader: String?,
    ): ApiResponse {
        return try {
            when (val result = mediaRepository.getContent(mediaId)) {
                is MediaContentResult.Found ->
                    contentResponse(result.content, rangeHeader, ifRangeHeader)
                is MediaContentResult.PermissionDenied ->
                    permissionDenied(result.requiredPermissions)
                MediaContentResult.NotFound ->
                    problem(404, "not_found", "The requested media item does not exist.")
            }
        } catch (_: SecurityException) {
            permissionDenied(emptySet())
        }
    }

    private fun contentResponse(
        content: MediaContent,
        rangeHeader: String?,
        ifRangeHeader: String?,
    ): ApiResponse {
        val effectiveRange = if (
            ifRangeHeader == null ||
            content.entityTag != null && ifRangeHeader == content.entityTag
        ) {
            rangeHeader
        } else {
            null
        }
        val range = parseRange(effectiveRange, content.length)
            ?: return ApiResponse(
                status = 416,
                body = "",
                contentType = "application/octet-stream",
                headers = mapOf(
                    "Accept-Ranges" to "bytes",
                    "Content-Range" to "bytes */${content.length}",
                ) + listOfNotNull(content.entityTag?.let { "ETag" to it }),
            )
        val stream = content.open(range.start)
            ?: return problem(404, "not_found", "The requested media item does not exist.")
        return ApiResponse(
            status = if (range.isPartial) 206 else 200,
            body = "",
            contentType = content.contentType,
            binaryStream = LimitedInputStream(stream, range.length),
            contentLength = range.length,
            headers = buildMap {
                put("Accept-Ranges", "bytes")
                content.entityTag?.let { put("ETag", it) }
                if (range.isPartial) {
                    put(
                        "Content-Range",
                        "bytes ${range.start}-${range.start + range.length - 1}/${content.length}",
                    )
                }
            },
        )
    }

    private fun header(headers: Map<String, String>, name: String): String? =
        headers.entries.firstOrNull { it.key.equals(name, ignoreCase = true) }?.value

    private fun parseRange(header: String?, length: Long): ContentRange? {
        if (header == null) return ContentRange(0, length, isPartial = false)
        val match = RANGE_PATTERN.matchEntire(header.trim()) ?: return null
        val startText = match.groupValues[1]
        val endText = match.groupValues[2]
        if (startText.isEmpty() && endText.isEmpty()) return null

        if (startText.isEmpty()) {
            val suffixLength = endText.toLongOrNull()?.takeIf { it > 0 } ?: return null
            if (length == 0L) return null
            val selectedLength = minOf(suffixLength, length)
            return ContentRange(
                start = length - selectedLength,
                length = selectedLength,
                isPartial = true,
            )
        }

        val start = startText.toLongOrNull()?.takeIf { it >= 0 } ?: return null
        if (start >= length) return null
        val requestedEnd = if (endText.isEmpty()) {
            length - 1
        } else {
            endText.toLongOrNull() ?: return null
        }
        if (requestedEnd < start) return null
        val endInclusive = minOf(requestedEnd, length - 1)
        return ContentRange(
            start = start,
            length = endInclusive - start + 1,
            isPartial = true,
        )
    }

    private suspend fun getThumbnail(
        mediaId: String,
        parameters: Map<String, List<String>>,
    ): ApiResponse {
        val width = singleDimension(parameters, "width") ?: return invalidParameter("width")
        val height = singleDimension(parameters, "height") ?: return invalidParameter("height")
        return try {
            when (val result = mediaRepository.getThumbnail(mediaId, width, height)) {
                is MediaThumbnailResult.Found -> ApiResponse(
                    status = 200,
                    body = "",
                    contentType = "image/jpeg",
                    binaryBody = result.jpeg,
                )
                is MediaThumbnailResult.PermissionDenied ->
                    permissionDenied(result.requiredPermissions)
                MediaThumbnailResult.NotFound ->
                    problem(404, "not_found", "The requested media item does not exist.")
            }
        } catch (_: SecurityException) {
            permissionDenied(emptySet())
        }
    }

    private fun singleDimension(parameters: Map<String, List<String>>, name: String): Int? {
        val raw = parameters[name]?.singleOrNull() ?: return null
        return raw.toIntOrNull()?.takeIf { it in 1..2048 }
    }

    private fun getDevice(): ApiResponse = try {
        when (val result = deviceInfoProvider.get()) {
            is DeviceInfoResult.Success -> ApiResponse(200, Json.device(result.device))
            is DeviceInfoResult.PermissionDenied -> permissionDenied(result.requiredPermissions)
        }
    } catch (_: SecurityException) {
        permissionDenied(emptySet())
    }

    private suspend fun getMedia(parameters: Map<String, List<String>>): ApiResponse {
        val limitText = parameters["limit"]?.singleOrNull()
            ?: if ("limit" in parameters) {
                return invalidParameter("limit")
            } else {
                null
            }
        val limit = limitText?.toIntOrNull() ?: if (limitText == null) {
            100
        } else {
            return invalidParameter("limit")
        }
        if (limit !in 1..200) return invalidParameter("limit")

        val cursor = parameters["cursor"]?.singleOrNull()
            ?: if ("cursor" in parameters) return invalidParameter("cursor") else null
        val beforeSortTime = parameters["beforeSortTime"]?.singleOrNull()
            ?: if ("beforeSortTime" in parameters) return invalidParameter("beforeSortTime") else null
        val beforeId = parameters["beforeId"]?.singleOrNull()
            ?: if ("beforeId" in parameters) return invalidParameter("beforeId") else null
        if (cursor != null && (beforeSortTime != null || beforeId != null)) {
            return invalidParameter("cursor")
        }

        val before = if (beforeSortTime != null || beforeId != null) {
            val sortTime = beforeSortTime?.toLongOrNull()?.takeIf { it >= 0 }
                ?: return invalidParameter("beforeSortTime")
            val id = beforeId?.toLongOrNull()?.takeIf { it >= 0 }
                ?: return invalidParameter("beforeId")
            MediaStoreCursor(sortTime, id)
        } else {
            null
        }

        val typeValues = parameters["type"].orEmpty()
            .flatMap { it.split(',') }
            .filter(String::isNotBlank)
        if ("type" in parameters && typeValues.isEmpty()) return invalidParameter("type")
        val types = if (typeValues.isEmpty()) {
            MediaType.entries.toSet()
        } else {
            typeValues.mapTo(linkedSetOf()) { value ->
                when (value.lowercase()) {
                    "image" -> MediaType.IMAGE
                    "video" -> MediaType.VIDEO
                    else -> return invalidParameter("type")
                }
            }
        }

        return try {
            when (
                val result = mediaRepository.getPage(
                    MediaQuery(cursor = cursor, before = before, limit = limit, types = types),
                )
            ) {
                is MediaPageResult.Success -> ApiResponse(200, Json.mediaPage(result.page))
                is MediaPageResult.PermissionDenied -> permissionDenied(result.requiredPermissions)
                MediaPageResult.InvalidCursor -> invalidParameter("cursor")
            }
        } catch (_: SecurityException) {
            permissionDenied(emptySet())
        }
    }

    private fun parseQuery(query: String): Map<String, List<String>> {
        if (query.isBlank()) return emptyMap()
        return query.split('&').groupBy(
            keySelector = { decode(it.substringBefore('=')) },
            valueTransform = { decode(it.substringAfter('=', "")) },
        )
    }

    private fun decode(value: String): String =
        URLDecoder.decode(value, StandardCharsets.UTF_8.name())

    private fun permissionDenied(permissions: Set<String>): ApiResponse = problem(
        403,
        "media_permission_denied",
        if (permissions.isEmpty()) {
            "Android media permission is required."
        } else {
            "Media access requires: ${permissions.sorted().joinToString()}."
        },
    )

    private fun invalidParameter(name: String): ApiResponse =
        problem(400, "invalid_parameter", "The '$name' query parameter is invalid.")

    private fun problem(status: Int, code: String, message: String): ApiResponse =
        ApiResponse(status, Json.problem(code, message))

    private companion object {
        const val DEVICE_PATH = "/api/v1/device"
        const val MEDIA_PATH = "/api/v1/media"
        val THUMBNAIL_PATH = Regex("^/api/v1/media/([^/]+)/thumbnail$")
        val CONTENT_PATH = Regex("^/api/v1/media/([^/]+)/content$")
        val RANGE_PATTERN = Regex("^bytes=(\\d*)-(\\d*)$")
    }

    private data class ContentRange(
        val start: Long,
        val length: Long,
        val isPartial: Boolean,
    )

    private class LimitedInputStream(
        source: InputStream,
        private var remaining: Long,
    ) : FilterInputStream(source) {
        override fun read(): Int {
            if (remaining == 0L) return -1
            val value = super.read()
            if (value >= 0) remaining--
            return value
        }

        override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
            if (remaining == 0L) return -1
            val count = super.read(buffer, offset, minOf(length.toLong(), remaining).toInt())
            if (count > 0) remaining -= count
            return count
        }
    }
}
