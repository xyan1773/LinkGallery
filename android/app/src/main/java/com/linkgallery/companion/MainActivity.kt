package com.linkgallery.companion

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.AndroidMediaStoreDataSource
import com.linkgallery.companion.media.DefaultMediaRepository
import com.linkgallery.companion.server.AndroidDeviceInfoProvider
import com.linkgallery.companion.server.ApiController
import com.linkgallery.companion.server.LinkGalleryHttpServer
import com.linkgallery.companion.ui.AndroidConnectionEnvironment
import com.linkgallery.companion.ui.PermissionScreen
import com.linkgallery.companion.ui.createConnectionGuide

class MainActivity : ComponentActivity() {
    private lateinit var httpServer: LinkGalleryHttpServer

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val permissionGateway = AndroidMediaPermissionGateway(applicationContext)
        val mediaRepository = DefaultMediaRepository(
            AndroidMediaStoreDataSource(contentResolver),
            permissionGateway,
        )
        httpServer = LinkGalleryHttpServer(
            ApiController(
                AndroidDeviceInfoProvider(applicationContext, permissionGateway),
                mediaRepository,
            ),
        )
        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    PermissionScreen(
                        connectionGuide = createConnectionGuide(
                            AndroidConnectionEnvironment.isEmulator(),
                            AndroidConnectionEnvironment.lanIpv4Addresses(),
                        ),
                    )
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        httpServer.start()
    }

    override fun onStop() {
        httpServer.stop()
        super.onStop()
    }
}
