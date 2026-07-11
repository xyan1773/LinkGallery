package com.linkgallery.companion.pairing

object Ipv4AddressCode {
    private val ipv4Pattern = Regex("^(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})$")

    fun encode(address: String): String? {
        val match = ipv4Pattern.matchEntire(address.trim()) ?: return null
        val octets = match.groupValues.drop(1).map { it.toIntOrNull() ?: return null }
        if (octets.any { it !in 0..255 }) return null
        return octets.joinToString(separator = "") { it.toString(16).padStart(2, '0') }.uppercase()
    }

    fun format(code: String): String {
        val normalized = code.filter(Char::isLetterOrDigit).uppercase()
        return if (normalized.length == 8) {
            "${normalized.take(4)}-${normalized.drop(4)}"
        } else {
            normalized
        }
    }
}
