package com.linkgallery.companion

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.core.content.ContextCompat
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.AndroidMediaStoreDataSource
import com.linkgallery.companion.media.DefaultMediaRepository
import com.linkgallery.companion.ui.AndroidConnectionEnvironment
import com.linkgallery.companion.ui.LinkGalleryTheme
import com.linkgallery.companion.ui.PermissionScreen
import com.linkgallery.companion.ui.createConnectionGuide

class MainActivity : ComponentActivity() {
    private var notificationPermissionGranted by mutableStateOf(true)
    private val notificationPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted -> notificationPermissionGranted = granted }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        refreshNotificationPermission()
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
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && !notificationPermissionGranted) {
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
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
                        notificationPermissionGranted = notificationPermissionGranted,
                        onNotificationPermissionRequest = {
                            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                            }
                        },
                        onOpenNotificationSettings = {
                            startActivity(
                                Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                                    .putExtra(Settings.EXTRA_APP_PACKAGE, packageName),
                            )
                        },
                    )
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        refreshNotificationPermission()
    }

    private fun refreshNotificationPermission() {
        notificationPermissionGranted = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            ContextCompat.checkSelfPermission(
                this,
                Manifest.permission.POST_NOTIFICATIONS,
            ) == PackageManager.PERMISSION_GRANTED
    }
}
