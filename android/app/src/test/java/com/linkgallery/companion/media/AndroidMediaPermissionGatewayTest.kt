package com.linkgallery.companion.media

import android.Manifest
import android.os.Build
import org.junit.Assert.assertEquals
import org.junit.Test

class AndroidMediaPermissionGatewayTest {
    @Test
    fun `Android 13 requests granular permissions for selected media types`() {
        assertEquals(
            setOf(
                Manifest.permission.READ_MEDIA_IMAGES,
                Manifest.permission.ACCESS_MEDIA_LOCATION,
            ),
            AndroidMediaPermissionGateway.requiredPermissions(
                setOf(MediaType.IMAGE),
                Build.VERSION_CODES.TIRAMISU,
            ),
        )
        assertEquals(
            setOf(
                Manifest.permission.READ_MEDIA_IMAGES,
                Manifest.permission.READ_MEDIA_VIDEO,
                Manifest.permission.ACCESS_MEDIA_LOCATION,
            ),
            AndroidMediaPermissionGateway.requiredPermissions(
                setOf(MediaType.IMAGE, MediaType.VIDEO),
                Build.VERSION_CODES.TIRAMISU,
            ),
        )
    }

    @Test
    fun `Android 12 requests storage and image location permissions`() {
        assertEquals(
            setOf(
                Manifest.permission.READ_EXTERNAL_STORAGE,
                Manifest.permission.ACCESS_MEDIA_LOCATION,
            ),
            AndroidMediaPermissionGateway.requiredPermissions(
                setOf(MediaType.IMAGE, MediaType.VIDEO),
                Build.VERSION_CODES.S_V2,
            ),
        )
    }

    @Test
    fun `Android 9 only requests external storage read permission`() {
        assertEquals(
            setOf(Manifest.permission.READ_EXTERNAL_STORAGE),
            AndroidMediaPermissionGateway.requiredPermissions(
                setOf(MediaType.IMAGE, MediaType.VIDEO),
                Build.VERSION_CODES.P,
            ),
        )
    }
}
