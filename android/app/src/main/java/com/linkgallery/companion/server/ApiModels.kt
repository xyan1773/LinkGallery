package com.linkgallery.companion.server

import java.io.InputStream

data class DeviceInfo(
    val id: String,
    val name: String,
    val model: String?,
    val battery: Int?,
    val mediaCount: Int,
)

sealed interface DeviceInfoResult {
    data class Success(val device: DeviceInfo) : DeviceInfoResult

    data class PermissionDenied(val requiredPermissions: Set<String>) : DeviceInfoResult
}

fun interface DeviceInfoProvider {
    fun get(): DeviceInfoResult
}

data class PublicDeviceInfo(
    val deviceId: String,
    val deviceName: String,
    val manufacturer: String,
    val model: String,
    val apiVersion: Int,
    val serverVersion: String,
    val instanceId: String,
    val pairingAvailable: Boolean,
    val certificateFingerprint: String,
)

fun interface PublicDeviceInfoProvider {
    fun get(): PublicDeviceInfo
}

data class ApiResponse(
    val status: Int,
    val body: String,
    val contentType: String = "application/json; charset=utf-8",
    val binaryBody: ByteArray? = null,
    val binaryStream: InputStream? = null,
    val contentLength: Long? = null,
    val headers: Map<String, String> = emptyMap(),
)
