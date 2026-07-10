package com.linkgallery.companion.discovery

import android.util.Log
import com.linkgallery.companion.server.PublicDeviceInfoProvider
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.util.concurrent.Executors

class AndroidUdpDiscoveryResponder(
    private val publicDeviceInfoProvider: PublicDeviceInfoProvider,
    private val activePairingCodeProvider: () -> String? = { null },
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
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(UdpDiscoveryProtocol.PORT))
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
                if (discover.pairingCode != null &&
                    discover.pairingCode != activePairingCodeProvider()
                ) {
                    continue
                }
                val response = UdpDiscoveryProtocol.announceJson(
                    discover = discover,
                    info = publicDeviceInfoProvider.get(),
                    // The packet source is authoritative. Leaving host empty avoids
                    // advertising an address from the wrong Wi-Fi, hotspot or USB interface.
                    host = "",
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

}
