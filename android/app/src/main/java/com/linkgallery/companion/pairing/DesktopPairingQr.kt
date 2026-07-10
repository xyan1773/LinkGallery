package com.linkgallery.companion.pairing

import java.net.URI
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

data class DesktopPairingQr(
    val verificationCode: String,
    val desktopId: String,
    val desktopName: String,
)

object DesktopPairingQrCodec {
    fun parse(raw: String): DesktopPairingQr? {
        val uri = runCatching { URI(raw.trim()) }.getOrNull() ?: return null
        if (uri.scheme != "linkgallery" || uri.host != "pair") return null
        val fields = uri.rawQuery.orEmpty()
            .split('&')
            .mapNotNull { part ->
                val split = part.indexOf('=')
                if (split <= 0) null else decode(part.take(split)) to decode(part.drop(split + 1))
            }
            .toMap()
        if (fields["v"] != "1") return null
        val code = fields["code"]
            ?.takeIf { it.length == 6 && it.all(Char::isDigit) }
            ?: return null
        val desktopId = fields["desktopId"]?.takeIf(String::isNotBlank) ?: return null
        val desktopName = fields["desktopName"]?.takeIf(String::isNotBlank) ?: "Windows PC"
        return DesktopPairingQr(code, desktopId, desktopName)
    }

    private fun decode(value: String): String =
        URLDecoder.decode(value, StandardCharsets.UTF_8.name())
}
