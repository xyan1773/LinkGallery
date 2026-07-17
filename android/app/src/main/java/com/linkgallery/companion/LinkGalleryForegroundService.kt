package com.linkgallery.companion

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.os.IBinder
import android.util.Log
import androidx.core.content.ContextCompat
import com.linkgallery.companion.discovery.AndroidNsdServiceRegistrar
import com.linkgallery.companion.discovery.AndroidUdpDiscoveryResponder
import com.linkgallery.companion.identity.AndroidKeystoreDeviceIdentityProvider
import com.linkgallery.companion.media.AndroidMediaPermissionGateway
import com.linkgallery.companion.media.AndroidMediaStoreDataSource
import com.linkgallery.companion.media.DefaultMediaRepository
import com.linkgallery.companion.pairing.AndroidPairingCredentialStore
import com.linkgallery.companion.pairing.PairingManager
import com.linkgallery.companion.server.AndroidDeviceInfoProvider
import com.linkgallery.companion.server.AndroidFriendlyDeviceNameProvider
import com.linkgallery.companion.server.AndroidPublicDeviceInfoProvider
import com.linkgallery.companion.server.ApiController
import com.linkgallery.companion.server.LinkGalleryHttpServer
import com.linkgallery.companion.server.RequestLogger
import com.linkgallery.companion.ui.AndroidConnectionEnvironment
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

data class LinkGalleryServiceState(
    val running: Boolean = false,
    val port: Int? = null,
    val addresses: List<String> = emptyList(),
    val pairingCode: String? = null,
    val pairedDesktopNames: List<String> = emptyList(),
)

object LinkGalleryServiceRuntime {
    private const val PREFERENCES = "linkgallery_service"
    private const val ENABLED = "enabled"
    private val mutableState = MutableStateFlow(LinkGalleryServiceState())
    private var service: LinkGalleryForegroundService? = null

    val state: StateFlow<LinkGalleryServiceState> = mutableState

    fun startIfEnabled(context: Context) {
        val enabled = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .getBoolean(ENABLED, true)
        if (enabled) start(context)
    }

    fun setEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(ENABLED, enabled)
            .apply()
        if (enabled) {
            start(context)
        } else {
            context.stopService(Intent(context, LinkGalleryForegroundService::class.java))
        }
    }

    fun openPairingWindow(verificationCode: String? = null): Long =
        service?.openPairingWindow(verificationCode) ?: 0L

    fun activePairingCode(): String? = service?.activePairingCode()

    private fun start(context: Context) {
        ContextCompat.startForegroundService(
            context,
            Intent(context, LinkGalleryForegroundService::class.java),
        )
    }

    internal fun attach(next: LinkGalleryForegroundService) {
        service = next
    }

    internal fun detach(current: LinkGalleryForegroundService) {
        if (service === current) service = null
        mutableState.value = LinkGalleryServiceState()
    }

    internal fun publish(next: LinkGalleryServiceState) {
        mutableState.value = next
    }
}

class LinkGalleryForegroundService : Service() {
    private lateinit var pairingManager: PairingManager
    private lateinit var publicDeviceInfoProvider: AndroidPublicDeviceInfoProvider
    private lateinit var nsdRegistrar: AndroidNsdServiceRegistrar
    private lateinit var udpDiscoveryResponder: AndroidUdpDiscoveryResponder
    private lateinit var httpServer: LinkGalleryHttpServer
    private lateinit var connectivityManager: ConnectivityManager
    private var advertisedPort: Int? = null
    private var cachedAddresses: List<String> = emptyList()
    private var lastPublishedState: LinkGalleryServiceState? = null

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = restartAdvertising(refreshAddresses = true)
        override fun onLost(network: Network) = restartAdvertising(refreshAddresses = true)
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        startForeground(NOTIFICATION_ID, notification(LinkGalleryServiceState()))
        LinkGalleryServiceRuntime.attach(this)

        val permissionGateway = AndroidMediaPermissionGateway(applicationContext)
        val credentialStore = AndroidPairingCredentialStore(applicationContext)
        val friendlyDeviceNameProvider = AndroidFriendlyDeviceNameProvider(applicationContext)
        pairingManager = PairingManager(credentialStore = credentialStore)
        publicDeviceInfoProvider = AndroidPublicDeviceInfoProvider(
            applicationContext,
            AndroidKeystoreDeviceIdentityProvider(),
            friendlyDeviceNameProvider,
            pairingAvailableProvider = pairingManager::isPairingAvailable,
        )
        nsdRegistrar = AndroidNsdServiceRegistrar(applicationContext, publicDeviceInfoProvider)
        udpDiscoveryResponder = AndroidUdpDiscoveryResponder(
            publicDeviceInfoProvider,
            pairingManager::activeVerificationCode,
        )
        val repository = DefaultMediaRepository(
            AndroidMediaStoreDataSource(applicationContext, contentResolver),
            permissionGateway,
        )
        httpServer = LinkGalleryHttpServer(
            ApiController(
                publicDeviceInfoProvider,
                AndroidDeviceInfoProvider(
                    applicationContext,
                    permissionGateway,
                    friendlyDeviceNameProvider,
                ),
                repository,
                pairingManager,
                pairingManager,
            ),
            logger = RequestLogger { method, target, status, elapsedMilliseconds ->
                Log.i("LinkGalleryHttp", "$method $target $status ${elapsedMilliseconds}ms")
                publishState()
            },
        )

        publicDeviceInfoProvider.rotateInstanceId()
        cachedAddresses = AndroidConnectionEnvironment.lanIpv4Addresses()
        httpServer.start()
        restartAdvertising(refreshAddresses = false)
        connectivityManager = getSystemService(ConnectivityManager::class.java)
        connectivityManager.registerDefaultNetworkCallback(networkCallback)
        publishState()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            LinkGalleryServiceRuntime.setEnabled(applicationContext, false)
            stopSelf()
            return START_NOT_STICKY
        }
        publishState()
        return START_STICKY
    }

    override fun onDestroy() {
        runCatching { connectivityManager.unregisterNetworkCallback(networkCallback) }
        udpDiscoveryResponder.stop()
        nsdRegistrar.unregister()
        httpServer.stop()
        advertisedPort = null
        LinkGalleryServiceRuntime.detach(this)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    fun openPairingWindow(verificationCode: String? = null): Long {
        val expiresAt = pairingManager.openPairingWindow(
            verificationCode = verificationCode,
        ).expiresAtEpochMillis
        publishState()
        return expiresAt
    }

    fun activePairingCode(): String? = pairingManager.activeVerificationCode()

    private fun restartAdvertising(refreshAddresses: Boolean = false) {
        val port = httpServer.localPort ?: return
        if (advertisedPort != null) {
            udpDiscoveryResponder.stop()
            nsdRegistrar.unregister()
        }
        if (refreshAddresses) {
            cachedAddresses = AndroidConnectionEnvironment.lanIpv4Addresses()
        }
        publicDeviceInfoProvider.rotateInstanceId()
        nsdRegistrar.register(port)
        udpDiscoveryResponder.start(port)
        advertisedPort = port
        publishState()
    }

    private fun publishState() {
        if (!::pairingManager.isInitialized) return
        val next = LinkGalleryServiceState(
            running = ::httpServer.isInitialized && httpServer.isRunning,
            port = if (::httpServer.isInitialized) httpServer.localPort else null,
            addresses = cachedAddresses,
            pairingCode = pairingManager.activeVerificationCode(),
            pairedDesktopNames = pairingManager.pairedCredentials()
                .map { it.desktopName }
                .distinct()
                .sorted(),
        )
        if (next == lastPublishedState) return
        lastPublishedState = next
        LinkGalleryServiceRuntime.publish(next)
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification(next))
    }

    private fun createNotificationChannel() {
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                getString(R.string.app_name),
                NotificationManager.IMPORTANCE_LOW,
            ),
        )
    }

    private fun notification(state: LinkGalleryServiceState): Notification {
        val openIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val stopIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, LinkGalleryForegroundService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val detail = when {
            !state.running -> "Starting read-only media sharing"
            state.pairedDesktopNames.isNotEmpty() ->
                "Available to ${state.pairedDesktopNames.joinToString()}"
            else -> "Read-only media sharing is active"
        }
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .setContentTitle("LinkGallery")
            .setContentText(detail)
            .setContentIntent(openIntent)
            .setOngoing(true)
            .addAction(
                Notification.Action.Builder(
                    null,
                    "Stop sharing",
                    stopIntent,
                ).build(),
            )
            .build()
    }

    private companion object {
        const val CHANNEL_ID = "linkgallery-media-service"
        const val NOTIFICATION_ID = 39570
        const val ACTION_STOP = "com.linkgallery.companion.STOP_SERVICE"
    }
}
