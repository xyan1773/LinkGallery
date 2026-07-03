package com.linkgallery.companion.discovery

import com.linkgallery.companion.server.PublicDeviceInfo

data class LinkGalleryNsdAnnouncement(
    val serviceName: String,
    val serviceType: String,
    val port: Int,
    val attributes: Map<String, String>,
)

object LinkGalleryNsdAnnouncementFactory {
    const val SERVICE_TYPE = "_linkgallery._tcp"

    fun create(info: PublicDeviceInfo, port: Int): LinkGalleryNsdAnnouncement =
        LinkGalleryNsdAnnouncement(
            serviceName = "LinkGallery-${sanitize(info.deviceName)}",
            serviceType = SERVICE_TYPE,
            port = port,
            attributes = buildMap {
                put("id", info.deviceId)
                put("name", info.deviceName)
                put("model", info.model)
                put("api", info.apiVersion.toString())
                put("instance", info.instanceId)
                put("pairing", if (info.pairingAvailable) "available" else "unavailable")
                put("fp", info.certificateFingerprint.split(':').take(6).joinToString(":"))
            },
        )

    private fun sanitize(value: String): String =
        value.filter { it.isLetterOrDigit() || it == '-' || it == '_' }
            .ifBlank { "Android" }
}
