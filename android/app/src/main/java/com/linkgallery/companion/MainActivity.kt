package com.linkgallery.companion

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.AndroidMediaStoreDataSource
import com.linkgallery.companion.media.DefaultMediaRepository
import com.linkgallery.companion.ui.AndroidConnectionEnvironment
import com.linkgallery.companion.ui.LinkGalleryTheme
import com.linkgallery.companion.ui.PermissionScreen
import com.linkgallery.companion.ui.createConnectionGuide

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val permissionGateway = AndroidMediaPermissionGateway(applicationContext)
        val mediaRepository = DefaultMediaRepository(
            AndroidMediaStoreDataSource(applicationContext, contentResolver),
            permissionGateway,
        )
        val connectionGuide = createConnectionGuide(
            AndroidConnectionEnvironment.isEmulator(),
            AndroidConnectionEnvironment.lanIpv4Addresses(),
        )
        LinkGalleryServiceRuntime.startIfEnabled(applicationContext)
        setContent {
            val serviceState by LinkGalleryServiceRuntime.state.collectAsState()
            LinkGalleryTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    PermissionScreen(
                        connectionGuide = connectionGuide,
                        mediaRepository = mediaRepository,
                        serviceState = serviceState,
                        onServiceRunningChange = { enabled ->
                            LinkGalleryServiceRuntime.setEnabled(applicationContext, enabled)
                        },
                        onOpenPairingWindow = LinkGalleryServiceRuntime::openPairingWindow,
                    )
                }
            }
        }
    }
}
