package com.linkgallery.companion.media

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat

class AndroidMediaPermissionGateway(
    private val context: Context,
    private val sdkInt: Int = Build.VERSION.SDK_INT,
) : MediaPermissionGateway {
    override fun missingPermissions(types: Set<MediaType>): Set<String> =
        requiredPermissions(types, sdkInt).filterTo(linkedSetOf()) { permission ->
            ContextCompat.checkSelfPermission(context, permission) != PackageManager.PERMISSION_GRANTED
        }

    companion object {
        fun requiredPermissions(types: Set<MediaType>, sdkInt: Int): Set<String> =
            if (sdkInt >= Build.VERSION_CODES.TIRAMISU) {
                buildSet {
                    if (MediaType.IMAGE in types) add(Manifest.permission.READ_MEDIA_IMAGES)
                    if (MediaType.VIDEO in types) add(Manifest.permission.READ_MEDIA_VIDEO)
                    if (MediaType.IMAGE in types) add(Manifest.permission.ACCESS_MEDIA_LOCATION)
                }
            } else if (sdkInt >= Build.VERSION_CODES.Q) {
                buildSet {
                    add(Manifest.permission.READ_EXTERNAL_STORAGE)
                    if (MediaType.IMAGE in types) add(Manifest.permission.ACCESS_MEDIA_LOCATION)
                }
            } else {
                setOf(Manifest.permission.READ_EXTERNAL_STORAGE)
            }
    }
}
