package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaThumbnailResult
import com.linkgallery.companion.media.MediaType
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

class ApiController(
    private val deviceInfoProvider: DeviceInfoProvider,
    private val mediaRepository: MediaRepository,
) {
    suspend fun handle(method: String, requestTarget: String): ApiResponse {
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
            } else {
                problem(404, "not_found", "The requested route does not exist.")
            }
        }
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
                    MediaQuery(cursor = cursor, limit = limit, types = types),
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
    }
}
