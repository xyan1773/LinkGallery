package com.linkgallery.companion

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import com.linkgallery.companion.discovery.AndroidNsdServiceRegistrar
import com.linkgallery.companion.discovery.AndroidUdpDiscoveryResponder
import com.linkgallery.companion.identity.AndroidKeystoreDeviceIdentityProvider
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.AndroidMediaStoreDataSource
import com.linkgallery.companion.media.DefaultMediaRepository
import com.linkgallery.companion.pairing.AndroidPairingCredentialStore
import com.linkgallery.companion.pairing.PairingManager
import com.linkgallery.companion.server.AndroidDeviceInfoProvider
import com.linkgallery.companion.server.AndroidPublicDeviceInfoProvider
import com.linkgallery.companion.server.ApiController
import com.linkgallery.companion.server.LinkGalleryHttpServer
import com.linkgallery.companion.ui.AndroidConnectionEnvironment
import com.linkgallery.companion.ui.PermissionScreen
import com.linkgallery.companion.ui.createConnectionGuide

class MainActivity : ComponentActivity() {
    private lateinit var httpServer: LinkGalleryHttpServer
    private lateinit var publicDeviceInfoProvider: AndroidPublicDeviceInfoProvider
    private lateinit var nsdRegistrar: AndroidNsdServiceRegistrar
    private lateinit var udpDiscoveryResponder: AndroidUdpDiscoveryResponder
    private lateinit var pairingManager: PairingManager

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val permissionGateway = AndroidMediaPermissionGateway(applicationContext)
        pairingManager = PairingManager(credentialStore = AndroidPairingCredentialStore(applicationContext))
        publicDeviceInfoProvider = AndroidPublicDeviceInfoProvider(
            applicationContext,
            AndroidKeystoreDeviceIdentityProvider(),
            pairingAvailableProvider = { pairingManager.isPairingAvailable() },
        )
        nsdRegistrar = AndroidNsdServiceRegistrar(applicationContext, publicDeviceInfoProvider)
        udpDiscoveryResponder = AndroidUdpDiscoveryResponder(publicDeviceInfoProvider)
        val mediaRepository = DefaultMediaRepository(
            AndroidMediaStoreDataSource(contentResolver),
            permissionGateway,
        )
        httpServer = LinkGalleryHttpServer(
            ApiController(
                publicDeviceInfoProvider,
                AndroidDeviceInfoProvider(applicationContext, permissionGateway),
                mediaRepository,
                pairingManager,
                pairingManager,
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
                        onOpenPairingWindow = {
                            pairingManager.openPairingWindow().expiresAtEpochMillis
                        },
                        activePairingCodeProvider = {
                            pairingManager.activeVerificationCode()
                        },
                    )
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        publicDeviceInfoProvider.rotateInstanceId()
        httpServer.start()
        httpServer.localPort?.let {
            nsdRegistrar.register(it)
            udpDiscoveryResponder.start(it)
        }
    }

    override fun onStop() {
        udpDiscoveryResponder.stop()
        nsdRegistrar.unregister()
        httpServer.stop()
        super.onStop()
    }
}
