package com.linkgallery.companion.server

import android.content.ContentResolver
import android.content.Context
import android.os.BatteryManager
import android.os.Build
import android.provider.MediaStore
import android.provider.Settings
import com.linkgallery.companion.media.MediaPermissionGateway
import com.linkgallery.companion.media.MediaType

class AndroidDeviceInfoProvider(
    private val context: Context,
    private val permissionGateway: MediaPermissionGateway,
) : DeviceInfoProvider {
    override fun get(): DeviceInfoResult {
        val missingPermissions = permissionGateway.missingPermissions(MediaType.entries.toSet())
        if (missingPermissions.isNotEmpty()) {
            return DeviceInfoResult.PermissionDenied(missingPermissions)
        }

        val mediaCount = context.contentResolver.query(
            MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL),
            arrayOf(MediaStore.MediaColumns._ID),
            "${MediaStore.Files.FileColumns.MEDIA_TYPE} IN (?, ?)",
            arrayOf(
                MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE.toString(),
                MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO.toString(),
            ),
            null,
        )?.use { it.count } ?: 0
        val batteryManager = context.getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        val battery = batteryManager
            .getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
            .takeIf { it in 0..100 }

        return DeviceInfoResult.Success(
            DeviceInfo(
                id = Settings.Secure.getString(
                    context.contentResolver,
                    Settings.Secure.ANDROID_ID,
                ) ?: "unknown",
                name = Build.MODEL.ifBlank { "Android device" },
                model = Build.MODEL.takeIf(String::isNotBlank),
                battery = battery,
                mediaCount = mediaCount,
            ),
        )
    }
}
