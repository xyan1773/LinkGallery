package com.linkgallery.companion.server

import com.linkgallery.companion.media.MediaContent
import com.linkgallery.companion.media.MediaContentResult
import com.linkgallery.companion.media.MediaChangesResult
import com.linkgallery.companion.media.MediaManifestResult
import com.linkgallery.companion.media.MediaPageResult
import com.linkgallery.companion.media.MediaQuery
import com.linkgallery.companion.media.MediaRepository
import com.linkgallery.companion.media.MediaStoreCursor
import com.linkgallery.companion.media.MediaThumbnailResult
import com.linkgallery.companion.media.MediaSyncStateResult
import com.linkgallery.companion.media.MediaType
import com.linkgallery.companion.pairing.AccessTokenAuthenticator
import com.linkgallery.companion.pairing.AllowAllAccessTokenAuthenticator
import com.linkgallery.companion.pairing.DisabledPairingCoordinator
import com.linkgallery.companion.pairing.PairCancelRequest
import com.linkgallery.companion.pairing.PairConfirmRequest
import com.linkgallery.companion.pairing.PairStartRequest
import com.linkgallery.companion.pairing.PairingCoordinator
import com.linkgallery.companion.pairing.PairingResult
import com.linkgallery.companion.pairing.RejectingAccessTokenAuthenticator
import java.io.FilterInputStream
import java.io.InputStream
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

class ApiController(
    private val publicDeviceInfoProvider: PublicDeviceInfoProvider,
    private val deviceInfoProvider: DeviceInfoProvider,
    private val mediaRepository: MediaRepository,
    private val pairingCoordinator: PairingCoordinator = DisabledPairingCoordinator,
    private val accessTokenAuthenticator: AccessTokenAuthenticator = RejectingAccessTokenAuthenticator,
) {
    suspend fun handle(
        method: String,
        requestTarget: String,
        headers: Map<String, String> = emptyMap(),
        body: String = "",
    ): ApiResponse {
        val path = requestTarget.substringBefore('?')
        if (!ReadOnlyRoutePolicy.permits(method, path)) {
            return problem(404, "not_found", "The requested route does not exist.")
        }
        val bearerToken = bearerToken(headers)
        if (
            ReadOnlyRoutePolicy.requiresAuthentication(method, path) &&
            accessTokenAuthenticator != AllowAllAccessTokenAuthenticator
        ) {
            if (bearerToken == null) {
                return problem(401, "authentication_required", "Authorization: Bearer token is required.")
            }
            if (accessTokenAuthenticator.authenticate(bearerToken) == null) {
                return problem(403, "authentication_failed", "The access token is invalid or revoked.")
            }
        }

        return when (path) {
            PUBLIC_INFO_PATH -> getPublicInfo()
            PAIR_START_PATH -> postPairStart(body)
            PAIR_CONFIRM_PATH -> postPairConfirm(body)
            PAIR_CANCEL_PATH -> postPairCancel(body)
            PAIR_REVOKE_PATH -> postPairRevoke(bearerToken)
            DEVICE_PATH -> getDevice()
            MEDIA_SYNC_STATE_PATH -> getMediaSyncState()
            MEDIA_CHANGES_PATH -> {
                val parameters = try {
                    parseQuery(requestTarget.substringAfter('?', ""))
                } catch (_: IllegalArgumentException) {
                    return problem(400, "invalid_parameter", "The query string is invalid.")
                }
                getMediaChanges(parameters)
            }
            MEDIA_MANIFEST_PATH -> {
                val parameters = try {
                    parseQuery(requestTarget.substringAfter('?', ""))
                } catch (_: IllegalArgumentException) {
                    return problem(400, "invalid_parameter", "The query string is invalid.")
                }
                getMediaManifest(parameters)
            }
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
                getThumbnail(mediaId, parameters, header(headers, "If-None-Match"))
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
        ifNoneMatchHeader: String?,
    ): ApiResponse {
        val (width, height) = thumbnailDimensions(parameters)
            ?: return invalidParameter("size")
        return try {
            when (val result = mediaRepository.getThumbnail(mediaId, width, height)) {
                is MediaThumbnailResult.Found -> {
                    val headers = thumbnailHeaders(result.entityTag)
                    if (ifNoneMatchHeader == result.entityTag) {
                        ApiResponse(
                            status = 304,
                            body = "",
                            contentType = "image/jpeg",
                            contentLength = 0,
                            headers = headers,
                        )
                    } else {
                        ApiResponse(
                            status = 200,
                            body = "",
                            contentType = "image/jpeg",
                            binaryBody = result.jpeg,
                            headers = headers,
                        )
                    }
                }
                is MediaThumbnailResult.PermissionDenied ->
                    permissionDenied(result.requiredPermissions)
                MediaThumbnailResult.NotFound ->
                    problem(404, "not_found", "The requested media item does not exist.")
            }
        } catch (_: SecurityException) {
            permissionDenied(emptySet())
        }
    }

    private fun thumbnailDimensions(parameters: Map<String, List<String>>): Pair<Int, Int>? {
        val size = parameters["size"]?.singleOrNull()
            ?: if ("size" in parameters) return null else null
        if (size != null) {
            val value = size.toIntOrNull()?.takeIf { it in 1..2048 } ?: return null
            return value to value
        }

        val width = singleDimension(parameters, "width") ?: return null
        val height = singleDimension(parameters, "height") ?: return null
        return width to height
    }

    private fun thumbnailHeaders(entityTag: String): Map<String, String> = mapOf(
        "Cache-Control" to "public, max-age=86400",
        "ETag" to entityTag,
    )

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
        val albumId = parameters["albumId"]?.singleOrNull()
            ?.takeIf { it.isNotBlank() }
            ?: if ("albumId" in parameters) return invalidParameter("albumId") else null
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
                    MediaQuery(
                        cursor = cursor,
                        before = before,
                        limit = limit,
                        types = types,
                        albumId = albumId,
                    ),
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

    private suspend fun getMediaSyncState(): ApiResponse =
        when (val result = mediaRepository.getSyncState()) {
            is MediaSyncStateResult.Success -> ApiResponse(200, Json.mediaSyncState(result.state))
            is MediaSyncStateResult.PermissionDenied -> permissionDenied(result.requiredPermissions)
        }

    private suspend fun getMediaChanges(parameters: Map<String, List<String>>): ApiResponse {
        val after = parameters["after"]?.singleOrNull()
            ?: if ("after" in parameters) return invalidParameter("after") else null
        val limit = parameters["limit"]?.singleOrNull()?.toIntOrNull() ?: 200
        if (limit !in 1..500) return invalidParameter("limit")
        return when (val result = mediaRepository.getChanges(after, limit)) {
            is MediaChangesResult.Success -> ApiResponse(200, Json.mediaChanges(result.changes))
            is MediaChangesResult.PermissionDenied -> permissionDenied(result.requiredPermissions)
            MediaChangesResult.InvalidCursor -> invalidParameter("after")
        }
    }

    private suspend fun getMediaManifest(parameters: Map<String, List<String>>): ApiResponse {
        val cursor = parameters["cursor"]?.singleOrNull()
            ?: if ("cursor" in parameters) return invalidParameter("cursor") else null
        val limit = parameters["limit"]?.singleOrNull()?.toIntOrNull() ?: 500
        if (limit !in 1..500) return invalidParameter("limit")
        return when (val result = mediaRepository.getManifest(cursor, limit)) {
            is MediaManifestResult.Success -> ApiResponse(200, Json.mediaManifest(result.page))
            is MediaManifestResult.PermissionDenied ->
                permissionDenied(result.requiredPermissions)
            MediaManifestResult.InvalidCursor -> invalidParameter("cursor")
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

    private fun pairingFailure(result: PairingResult.Failure): ApiResponse =
        problem(result.status, result.code, result.message)

    private fun postPairStart(body: String): ApiResponse {
        val request = try {
            val fields = JsonFields.parse(body)
            PairStartRequest(
                desktopId = fields.required("desktopId"),
                desktopName = fields.required("desktopName"),
                desktopModel = fields.optional("desktopModel"),
                identityPublicKey = fields.required("identityPublicKey"),
                ephemeralPublicKey = fields.required("ephemeralPublicKey"),
                nonce = fields.required("nonce"),
            )
        } catch (_: IllegalArgumentException) {
            return problem(400, "invalid_pairing_request", "The pairing request JSON is invalid.")
        }
        return when (val result = pairingCoordinator.start(request)) {
            is PairingResult.Success -> ApiResponse(200, Json.pairStart(result.value))
            is PairingResult.Failure -> pairingFailure(result)
        }
    }

    private fun postPairConfirm(body: String): ApiResponse {
        val request = try {
            val fields = JsonFields.parse(body)
            PairConfirmRequest(
                pairingSessionId = fields.required("pairingSessionId"),
                verificationCode = fields.optional("verificationCode")
                    ?: fields.optional("code")
                    ?: fields.required("confirmationMac"),
            )
        } catch (_: IllegalArgumentException) {
            return problem(400, "invalid_pairing_request", "The pairing confirmation JSON is invalid.")
        }
        return when (val result = pairingCoordinator.confirm(request)) {
            is PairingResult.Success -> ApiResponse(200, Json.pairConfirm(result.value))
            is PairingResult.Failure -> pairingFailure(result)
        }
    }

    private fun postPairCancel(body: String): ApiResponse {
        val request = try {
            val fields = JsonFields.parse(body)
            PairCancelRequest(fields.required("pairingSessionId"))
        } catch (_: IllegalArgumentException) {
            return problem(400, "invalid_pairing_request", "The pairing cancel JSON is invalid.")
        }
        return when (val result = pairingCoordinator.cancel(request)) {
            is PairingResult.Success -> ApiResponse(200, Json.ok())
            is PairingResult.Failure -> pairingFailure(result)
        }
    }

    private fun postPairRevoke(bearerToken: String?): ApiResponse {
        val token = bearerToken ?: return problem(
            401,
            "authentication_required",
            "Authorization: Bearer token is required.",
        )
        accessTokenAuthenticator.revoke(token)
        return ApiResponse(200, Json.ok())
    }

    private fun bearerToken(headers: Map<String, String>): String? {
        val header = header(headers, "Authorization") ?: return null
        val prefix = "Bearer "
        if (!header.startsWith(prefix, ignoreCase = true)) return null
        return header.substring(prefix.length).trim().takeIf { it.isNotBlank() }
    }

    private companion object {
        const val PUBLIC_INFO_PATH = "/api/v1/public/info"
        const val PAIR_START_PATH = "/api/v1/pair/start"
        const val PAIR_CONFIRM_PATH = "/api/v1/pair/confirm"
        const val PAIR_CANCEL_PATH = "/api/v1/pair/cancel"
        const val PAIR_REVOKE_PATH = "/api/v1/pair/revoke"
        const val DEVICE_PATH = "/api/v1/device"
        const val MEDIA_PATH = "/api/v1/media"
        const val MEDIA_SYNC_STATE_PATH = "/api/v1/media/sync/state"
        const val MEDIA_CHANGES_PATH = "/api/v1/media/changes"
        const val MEDIA_MANIFEST_PATH = "/api/v1/media/manifest"
        val THUMBNAIL_PATH = Regex("^/api/v1/media/([^/]+)/thumbnail$")
        val CONTENT_PATH = Regex("^/api/v1/media/([^/]+)/content$")
        val RANGE_PATTERN = Regex("^bytes=(\\d*)-(\\d*)$")
    }

    private fun getPublicInfo(): ApiResponse =
        ApiResponse(200, Json.publicDeviceInfo(publicDeviceInfoProvider.get()))

    private class JsonFields(private val values: Map<String, String?>) {
        fun required(name: String): String =
            values[name]?.takeIf { it.isNotBlank() }
                ?: throw IllegalArgumentException("Missing $name")

        fun optional(name: String): String? = values[name]

        companion object {
            private val FIELD_PATTERN = Regex(
                """"([^"\\]*(?:\\.[^"\\]*)*)"\s*:\s*("([^"\\]*(?:\\.[^"\\]*)*)"|null)""",
            )

            fun parse(body: String): JsonFields {
                val trimmed = body.trim()
                if (!trimmed.startsWith("{") || !trimmed.endsWith("}")) {
                    throw IllegalArgumentException("Expected object")
                }
                val values = mutableMapOf<String, String?>()
                FIELD_PATTERN.findAll(trimmed).forEach { match ->
                    val key = unescape(match.groupValues[1])
                    val rawValue = match.groupValues[2]
                    values[key] = if (rawValue == "null") null else unescape(match.groupValues[3])
                }
                return JsonFields(values)
            }

            private fun unescape(value: String): String {
                val output = StringBuilder()
                var index = 0
                while (index < value.length) {
                    val character = value[index]
                    if (character != '\\') {
                        output.append(character)
                        index += 1
                    } else {
                        if (index + 1 >= value.length) throw IllegalArgumentException("Bad escape")
                        val escaped = value[index + 1]
                        if (escaped == 'u') {
                            if (index + 6 > value.length) {
                                throw IllegalArgumentException("Bad unicode escape")
                            }
                            val codePoint = value.substring(index + 2, index + 6)
                                .toIntOrNull(16)
                                ?: throw IllegalArgumentException("Bad unicode escape")
                            output.append(codePoint.toChar())
                            index += 6
                            continue
                        }
                        output.append(
                            when (escaped) {
                                '"' -> '"'
                                '\\' -> '\\'
                                '/' -> '/'
                                'b' -> '\b'
                                'f' -> '\u000C'
                                'n' -> '\n'
                                'r' -> '\r'
                                't' -> '\t'
                                else -> throw IllegalArgumentException("Unsupported escape")
                            },
                        )
                        index += 2
                    }
                }
                return output.toString()
            }
        }
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
