package com.linkgallery.companion.media

import java.nio.charset.StandardCharsets
import java.util.Base64

internal data class MediaLocator(
    val type: MediaType,
    val mediaStoreId: Long,
)

class OpaqueMediaTokenCodec {
    internal fun encodeId(row: MediaStoreRow): String =
        ID_PREFIX + encode("${row.type.name}:${row.mediaStoreId}")

    internal fun decodeId(value: String): MediaLocator? {
        if (!value.startsWith(ID_PREFIX)) return null
        val parts = decode(value.removePrefix(ID_PREFIX))?.split(':') ?: return null
        if (parts.size != 2) return null
        val type = runCatching { MediaType.valueOf(parts[0]) }.getOrNull() ?: return null
        val id = parts[1].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        return MediaLocator(type, id)
    }

    internal fun encodeCursor(row: MediaStoreRow): String =
        CURSOR_PREFIX + encode("${row.dateModifiedEpochSeconds}:${row.mediaStoreId}")

    internal fun decodeCursor(value: String): MediaStoreCursor? {
        if (!value.startsWith(CURSOR_PREFIX)) return null
        val parts = decode(value.removePrefix(CURSOR_PREFIX))?.split(':') ?: return null
        if (parts.size != 2) return null
        val modifiedAt = parts[0].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        val id = parts[1].toLongOrNull()?.takeIf { it >= 0 } ?: return null
        return MediaStoreCursor(modifiedAt, id)
    }

    private fun encode(value: String): String =
        Base64.getUrlEncoder()
            .withoutPadding()
            .encodeToString(value.toByteArray(StandardCharsets.UTF_8))

    private fun decode(value: String): String? =
        runCatching {
            String(Base64.getUrlDecoder().decode(value), StandardCharsets.UTF_8)
        }.getOrNull()

    private companion object {
        const val ID_PREFIX = "lgm1_"
        const val CURSOR_PREFIX = "lgc1_"
    }
}
