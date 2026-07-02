package com.linkgallery.companion.discovery

import android.util.Log
import com.linkgallery.companion.server.PublicDeviceInfoProvider
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.util.concurrent.Executors

class AndroidUdpDiscoveryResponder(
    private val publicDeviceInfoProvider: PublicDeviceInfoProvider,
) {
    private val executor = Executors.newSingleThreadExecutor()
    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var serverPort: Int = 39570

    fun start(httpPort: Int) {
        stop()
        serverPort = httpPort
        executor.execute { listen() }
    }

    fun stop() {
        socket?.close()
        socket = null
    }

    private fun listen() {
        val datagramSocket = runCatching {
            DatagramSocket(UdpDiscoveryProtocol.PORT).apply {
                broadcast = true
                reuseAddress = true
            }
        }.onFailure {
            Log.w("LinkGalleryUdp", "Unable to bind UDP discovery", it)
        }.getOrNull() ?: return
        socket = datagramSocket
        Log.i("LinkGalleryUdp", "Listening on UDP ${UdpDiscoveryProtocol.PORT}")
        val buffer = ByteArray(4096)
        while (!datagramSocket.isClosed) {
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                datagramSocket.receive(packet)
                val payload = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                val discover = UdpDiscoveryProtocol.parseDiscover(payload) ?: continue
                val response = UdpDiscoveryProtocol.announceJson(
                    discover = discover,
                    info = publicDeviceInfoProvider.get(),
                    host = lanHostAddress() ?: packet.address.hostAddress.orEmpty(),
                    port = serverPort,
                    timestamp = System.currentTimeMillis(),
                ).toByteArray(Charsets.UTF_8)
                datagramSocket.send(DatagramPacket(response, response.size, packet.address, packet.port))
            } catch (_: Exception) {
                if (!datagramSocket.isClosed) {
                    Log.w("LinkGalleryUdp", "UDP discovery receive failed")
                }
            }
        }
    }

    private fun lanHostAddress(): String? =
        NetworkInterface.getNetworkInterfaces().asSequence()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.asSequence() }
            .firstOrNull { address ->
                address is java.net.Inet4Address &&
                    !address.isLoopbackAddress &&
                    !address.isLinkLocalAddress
            }
            ?.hostAddress
}
