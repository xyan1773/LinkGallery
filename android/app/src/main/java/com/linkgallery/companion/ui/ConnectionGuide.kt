package com.linkgallery.companion.ui

import android.os.Build
import java.net.Inet4Address
import java.net.NetworkInterface

data class ConnectionGuide(
    val title: String,
    val address: String,
    val detail: String,
)

fun createConnectionGuide(
    isEmulator: Boolean,
    lanAddresses: List<String>,
    port: Int = 39570,
): ConnectionGuide = if (isEmulator) {
    ConnectionGuide(
        title = "Android 模拟器",
        address = "Windows 连接地址：127.0.0.1:$port",
        detail = "先运行 ADB forward tcp:$port tcp:$port。模拟器的 10.0.2.x 是 NAT 地址，Windows 不能直接连接。",
    )
} else {
    val address = lanAddresses.firstOrNull()
    ConnectionGuide(
        title = "真实手机 · 同一 Wi-Fi",
        address = address?.let { "Windows 连接地址：$it:$port" }
            ?: "暂未找到 Wi-Fi 局域网地址",
        detail = "保持本页面在前台；若地址未显示，请确认手机已连接 Wi-Fi。",
    )
}

object AndroidConnectionEnvironment {
    fun isEmulator(): Boolean {
        val fingerprint = Build.FINGERPRINT.lowercase()
        val model = Build.MODEL.lowercase()
        val product = Build.PRODUCT.lowercase()
        return fingerprint.startsWith("generic") ||
            fingerprint.contains("emulator") ||
            model.contains("emulator") ||
            model.contains("android sdk built for") ||
            product.contains("sdk")
    }

    fun lanIpv4Addresses(): List<String> = runCatching {
        NetworkInterface.getNetworkInterfaces()
            .toList()
            .filter {
                it.isUp &&
                    !it.isLoopback &&
                    (it.name.startsWith("wlan") || it.displayName.contains("wlan", ignoreCase = true))
            }
            .flatMap { network -> network.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .filter { it.isSiteLocalAddress }
            .map { it.hostAddress.orEmpty() }
            .filter { it.isNotBlank() }
            .sorted()
    }.getOrDefault(emptyList())
}
