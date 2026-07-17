package com.linkgallery.companion.server

import android.content.Context
import android.os.Build
import android.provider.Settings
import com.jaredrummler.android.device.DeviceName

/**
 * Resolves the consumer-facing device name without blocking service startup.
 * Android exposes hardware codes through Build.MODEL on many vendors, so the
 * maintained AndroidDeviceNames database is warmed on a background thread.
 */
class AndroidFriendlyDeviceNameProvider(
    private val context: Context,
) {
    @Volatile
    private var cachedName: String = resolveFastName()

    init {
        DeviceName.init(context)
        Thread(
            {
                val databaseName = runCatching {
                    DeviceName.getDeviceInfo(context).name
                }.getOrNull()
                if (databaseName.isConsumerFriendly()) {
                    cachedName = databaseName!!.trim()
                }
            },
            "LinkGallery-device-name",
        ).apply {
            isDaemon = true
            start()
        }
    }

    fun get(): String = cachedName

    private fun resolveFastName(): String {
        knownMarketingName(Build.MODEL)?.let { return it }

        val configuredName = sequenceOf(
            runCatching {
                Settings.Global.getString(context.contentResolver, "device_name")
            }.getOrNull(),
            runCatching {
                Settings.System.getString(context.contentResolver, "device_name")
            }.getOrNull(),
        ).firstOrNull { it.isConsumerFriendly() }
        if (configuredName != null) {
            return configuredName.trim()
        }

        val manufacturer = Build.MANUFACTURER.trim()
        val model = Build.MODEL.trim()
        return when {
            model.isBlank() -> manufacturer.ifBlank { "Android device" }
            manufacturer.isBlank() || model.startsWith(manufacturer, ignoreCase = true) -> model
            else -> "$manufacturer $model"
        }
    }

    private fun String?.isConsumerFriendly(): Boolean {
        val candidate = this?.trim().orEmpty()
        return candidate.isNotEmpty() &&
            !candidate.equals(Build.MODEL, ignoreCase = true) &&
            !candidate.equals(Build.DEVICE, ignoreCase = true)
    }

    private fun knownMarketingName(model: String): String? = when (model.uppercase()) {
        // Chinese, global and Indian variants of Xiaomi 15.
        "24129PN74C", "24129PN74G", "24129PN74I" -> "Xiaomi 15"
        else -> null
    }
}
