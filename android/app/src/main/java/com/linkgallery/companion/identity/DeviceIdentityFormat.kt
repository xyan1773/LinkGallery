package com.linkgallery.companion.identity

import java.security.MessageDigest
import java.util.Locale

object DeviceIdentityFormat {
    private val alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".toCharArray()

    fun deviceIdFromCertificate(certificateDer: ByteArray): String =
        base32NoPadding(sha256(certificateDer))

    fun fingerprint(certificateDer: ByteArray): String =
        sha256(certificateDer).joinToString(separator = ":") { byte ->
            "%02X".format(Locale.US, byte)
        }

    fun shortCode(deviceId: String): String {
        val compact = deviceId.filter { it.isLetterOrDigit() }.uppercase(Locale.US)
        return "LKG-" + compact.take(8).chunked(4).joinToString("-")
    }

    private fun sha256(bytes: ByteArray): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(bytes)

    private fun base32NoPadding(bytes: ByteArray): String {
        val output = StringBuilder((bytes.size * 8 + 4) / 5)
        var buffer = 0
        var bitsLeft = 0
        for (byte in bytes) {
            buffer = (buffer shl 8) or (byte.toInt() and 0xFF)
            bitsLeft += 8
            while (bitsLeft >= 5) {
                output.append(alphabet[(buffer shr (bitsLeft - 5)) and 0x1F])
                bitsLeft -= 5
            }
        }
        if (bitsLeft > 0) {
            output.append(alphabet[(buffer shl (5 - bitsLeft)) and 0x1F])
        }
        return output.toString()
    }
}
