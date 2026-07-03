package com.linkgallery.companion.discovery

data class ServiceDescriptor(
    val serviceType: String = LinkGalleryNsdAnnouncementFactory.SERVICE_TYPE,
    val port: Int = 39570,
    val protocolVersion: Int = 1,
)
