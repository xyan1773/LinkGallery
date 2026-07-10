package com.linkgallery.companion.discovery

import com.linkgallery.companion.server.PublicDeviceInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class UdpDiscoveryProtocolTest {
    @Test
    fun parsesDiscoverAndBuildsAnnounce() {
        val discover = UdpDiscoveryProtocol.parseDiscover(
            """{"magic":"LINKGALLERY_DISCOVERY_V1","type":"discover","nonce":"n1","desktopId":"desktop","timestamp":1}""",
        )

        assertNotNull(discover)
        val announce = UdpDiscoveryProtocol.announceJson(
            discover = discover!!,
            info = PublicDeviceInfo(
                deviceId = "phone-1",
                deviceName = "Pixel",
                manufacturer = "Google",
                model = "Pixel 9",
                apiVersion = 1,
                serverVersion = "0.1.0",
                instanceId = "instance-1",
                pairingAvailable = true,
                certificateFingerprint = "AA:BB",
            ),
            host = "192.168.1.42",
            port = 39570,
            timestamp = 2,
        )

        assertTrue(announce.contains(""""type":"announce""""))
        assertTrue(announce.contains(""""nonce":"n1""""))
        assertTrue(announce.contains(""""deviceId":"phone-1""""))
        assertTrue(announce.contains(""""host":"192.168.1.42""""))
        assertTrue(announce.contains(""""port":39570"""))
        assertTrue(announce.contains(""""certificateFingerprint":"AA:BB""""))
        assertTrue(announce.contains("\"signature\":\"\""))
    }

    @Test
    fun ignoresUnknownDiscoveryPayloads() {
        assertNull(UdpDiscoveryProtocol.parseDiscover("""{"magic":"other","type":"discover"}"""))
        assertNull(UdpDiscoveryProtocol.parseDiscover("""{"magic":"LINKGALLERY_DISCOVERY_V1","type":"announce"}"""))
    }

    @Test
    fun parsesPairingCodeResolutionWithoutTreatingCodeAsAddress() {
        val request = UdpDiscoveryProtocol.parseDiscover(
            """{"magic":"LINKGALLERY_DISCOVERY_V1","type":"resolve_pairing_code","nonce":"n2","desktopId":"desktop","pairingCode":"281604","timestamp":3}""",
        )

        assertNotNull(request)
        assertEquals("281604", request?.pairingCode)
    }
}
