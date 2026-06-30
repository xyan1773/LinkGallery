package com.linkgallery.companion.discovery

data class ServiceDescriptor(
    val serviceType: String = "_linkgallery._tcp.",
    val port: Int = 39570,
    val protocolVersion: Int = 1,
)

