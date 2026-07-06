package com.linkgallery.companion.media

import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.Base64

internal data class MediaLocator(
    val type: MediaType,
    val mediaStoreId: Long,
)

class OpaqueMediaTokenCodec {
    internal fun encodeId(row: MediaStoreRow): String =
        encodeId(row.type, row.mediaStoreId)

    internal fun encodeId(type: MediaType, mediaStoreId: Long): String =
        ID_PREFIX + encode("${type.name}:$mediaStoreId")

    internal fun decodeId(value: String): MediaLocator? {
        if (!value.startsWith(ID_PREFIX)) return null
        val parts = decode(value.removePrefix(ID_PREFIX))?.split(':') ?: return null
        if (parts.size != 2) return null
        val type = runCatching { MediaType.valueOf(parts[0]) }.getOrNull() ?: return null
        val id = parts[1].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        return MediaLocator(type, id)
    }

    internal fun encodeCursor(row: MediaStoreRow): String =
        CURSOR_PREFIX + encode("${row.sortTimestampEpochMillis}:${row.mediaStoreId}")

    internal fun decodeCursor(value: String): MediaStoreCursor? {
        if (!value.startsWith(CURSOR_PREFIX)) return null
        val parts = decode(value.removePrefix(CURSOR_PREFIX))?.split(':') ?: return null
        if (parts.size != 2) return null
        val sortTimestamp = parts[0].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        val id = parts[1].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        return MediaStoreCursor(sortTimestamp, id)
    }

    internal fun encodeCursor(row: MediaStoreRow, scope: String): String =
        SCOPED_CURSOR_PREFIX + encode(
            "${row.sortTimestampEpochMillis}:${row.mediaStoreId}:${scopeFingerprint(scope)}",
        )

    internal fun decodeCursor(value: String, expectedScope: String): MediaStoreCursor? {
        if (value.startsWith(CURSOR_PREFIX)) {
            return if (expectedScope.startsWith("album=;")) decodeCursor(value) else null
        }
        if (!value.startsWith(SCOPED_CURSOR_PREFIX)) return null
        val parts = decode(value.removePrefix(SCOPED_CURSOR_PREFIX))?.split(':') ?: return null
        if (parts.size != 3 || parts[2] != scopeFingerprint(expectedScope)) return null
        val sortTimestamp = parts[0].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        val id = parts[1].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        return MediaStoreCursor(sortTimestamp, id)
    }

    internal fun encodeSyncCursor(value: MediaSyncCursor): String =
        SYNC_CURSOR_PREFIX + encode("${value.value}:${value.mediaStoreId}")

    internal fun decodeSyncCursor(value: String): MediaSyncCursor? {
        if (!value.startsWith(SYNC_CURSOR_PREFIX)) return null
        val parts = decode(value.removePrefix(SYNC_CURSOR_PREFIX))?.split(':') ?: return null
        if (parts.size != 2) return null
        val generation = parts[0].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        val id = parts[1].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        return MediaSyncCursor(generation, id)
    }

    internal fun encodeManifestCursor(mediaStoreId: Long): String =
        MANIFEST_CURSOR_PREFIX + encode(mediaStoreId.toString())

    internal fun decodeManifestCursor(value: String): Long? {
        if (!value.startsWith(MANIFEST_CURSOR_PREFIX)) return null
        return decode(value.removePrefix(MANIFEST_CURSOR_PREFIX))
            ?.toLongOrNull()
            ?.takeIf { it >= 0 }
    }

    private fun encode(value: String): String =
        Base64.getUrlEncoder()
            .withoutPadding()
            .encodeToString(value.toByteArray(StandardCharsets.UTF_8))

    private fun decode(value: String): String? =
        runCatching {
            String(Base64.getUrlDecoder().decode(value), StandardCharsets.UTF_8)
        }.getOrNull()

    private fun scopeFingerprint(scope: String): String =
        MessageDigest.getInstance("SHA-256")
            .digest(scope.toByteArray(StandardCharsets.UTF_8))
            .take(12)
            .joinToString("") { "%02x".format(it) }

    companion object {
        internal fun cursorScope(albumId: String?, types: Set<MediaType>): String =
            "album=${albumId.orEmpty()};types=${types.map(MediaType::name).sorted().joinToString(",")}"

        private const val ID_PREFIX = "lgm1_"
        private const val CURSOR_PREFIX = "lgc2_"
        private const val SCOPED_CURSOR_PREFIX = "lgc3_"
        private const val SYNC_CURSOR_PREFIX = "lgs1_"
        private const val MANIFEST_CURSOR_PREFIX = "lgmfc1_"
    }
}
