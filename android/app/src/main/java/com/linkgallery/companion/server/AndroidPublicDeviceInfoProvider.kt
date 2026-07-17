package com.linkgallery.companion.server

import android.content.Context
import android.os.Build
import com.linkgallery.companion.identity.DeviceIdentityProvider
import java.util.UUID

class AndroidPublicDeviceInfoProvider(
    private val context: Context,
    private val identityProvider: DeviceIdentityProvider,
    private val friendlyDeviceNameProvider: AndroidFriendlyDeviceNameProvider,
    private val apiVersion: Int = 1,
    private val pairingAvailableProvider: () -> Boolean = { false },
) : PublicDeviceInfoProvider {
    private var instanceId: String = newInstanceId()

    fun rotateInstanceId() {
        instanceId = newInstanceId()
    }

    override fun get(): PublicDeviceInfo {
        val identity = identityProvider.getOrCreate()
        return PublicDeviceInfo(
            deviceId = identity.deviceId,
            deviceName = friendlyDeviceNameProvider.get(),
            manufacturer = Build.MANUFACTURER.ifBlank { "Android" },
            model = Build.MODEL.ifBlank { "Android device" },
            apiVersion = apiVersion,
            serverVersion = serverVersion(),
            instanceId = instanceId,
            pairingAvailable = pairingAvailableProvider(),
            certificateFingerprint = identity.certificateFingerprint,
        )
    }

    private fun serverVersion(): String =
        runCatching {
            val packageInfo = context.packageManager.getPackageInfo(context.packageName, 0)
            packageInfo.versionName ?: "0.0.0"
        }.getOrDefault("0.0.0")

    private fun newInstanceId(): String = UUID.randomUUID().toString()
}
