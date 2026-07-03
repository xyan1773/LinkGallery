package com.linkgallery.companion.discovery

import com.linkgallery.companion.server.PublicDeviceInfo

object UdpDiscoveryProtocol {
    const val MAGIC = "LINKGALLERY_DISCOVERY_V1"
    const val PORT = 39571

    fun parseDiscover(payload: String): DiscoverMessage? {
        if (field(payload, "magic") != MAGIC || field(payload, "type") != "discover") {
            return null
        }
        val nonce = field(payload, "nonce")?.takeIf(String::isNotBlank) ?: return null
        return DiscoverMessage(
            nonce = nonce,
            desktopId = field(payload, "desktopId").orEmpty(),
            timestamp = numberField(payload, "timestamp") ?: 0,
        )
    }

    fun announceJson(
        discover: DiscoverMessage,
        info: PublicDeviceInfo,
        host: String,
        port: Int,
        timestamp: Long,
        signature: String = "",
    ): String = buildString {
        append('{')
        appendString("magic", MAGIC)
        append(',')
        appendString("type", "announce")
        append(',')
        appendString("nonce", discover.nonce)
        append(',')
        appendString("deviceId", info.deviceId)
        append(',')
        appendString("name", info.deviceName)
        append(',')
        appendString("model", info.model)
        append(',')
        appendString("host", host)
        append(',')
        appendNumber("port", port)
        append(',')
        appendNumber("apiVersion", info.apiVersion)
        append(',')
        appendString("instanceId", info.instanceId)
        append(',')
        appendBoolean("pairingAvailable", info.pairingAvailable)
        append(',')
        appendString("certificateFingerprint", info.certificateFingerprint)
        append(',')
        appendNumber("timestamp", timestamp)
        append(',')
        appendString("signature", signature)
        append('}')
    }

    private fun field(payload: String, name: String): String? {
        val pattern = Regex("\"${Regex.escape(name)}\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"")
        return pattern.find(payload)?.groupValues?.get(1)?.replace("\\\"", "\"")
    }

    private fun numberField(payload: String, name: String): Long? {
        val pattern = Regex("\"${Regex.escape(name)}\"\\s*:\\s*(\\d+)")
        return pattern.find(payload)?.groupValues?.get(1)?.toLongOrNull()
    }

    private fun StringBuilder.appendString(name: String, value: String?) {
        append('"').append(name).append("\":")
        if (value == null) {
            append("null")
        } else {
            append('"').append(escape(value)).append('"')
        }
    }

    private fun StringBuilder.appendNumber(name: String, value: Number) {
        append('"').append(name).append("\":").append(value)
    }

    private fun StringBuilder.appendBoolean(name: String, value: Boolean) {
        append('"').append(name).append("\":").append(value)
    }

    private fun escape(value: String): String =
        value.replace("\\", "\\\\").replace("\"", "\\\"")
}

data class DiscoverMessage(
    val nonce: String,
    val desktopId: String,
    val timestamp: Long,
)
