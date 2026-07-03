package com.linkgallery.companion.discovery

import com.linkgallery.companion.server.PublicDeviceInfo
import org.junit.Assert.assertEquals
import org.junit.Test

class LinkGalleryNsdAnnouncementFactoryTest {
    @Test
    fun announcementContainsStableIdentityTxtAttributes() {
        val announcement = LinkGalleryNsdAnnouncementFactory.create(
            PublicDeviceInfo(
                deviceId = "DEVICEID",
                deviceName = "Pixel 9",
                manufacturer = "Google",
                model = "Pixel 9",
                apiVersion = 1,
                serverVersion = "0.1.0",
                instanceId = "instance-1",
                pairingAvailable = true,
                certificateFingerprint = "AA:BB:CC:DD:EE:FF",
            ),
            port = 39570,
        )

        assertEquals("_linkgallery._tcp", announcement.serviceType)
        assertEquals("LinkGallery-Pixel9", announcement.serviceName)
        assertEquals(39570, announcement.port)
        assertEquals("DEVICEID", announcement.attributes["id"])
        assertEquals("Pixel 9", announcement.attributes["name"])
        assertEquals("Pixel 9", announcement.attributes["model"])
        assertEquals("1", announcement.attributes["api"])
        assertEquals("instance-1", announcement.attributes["instance"])
        assertEquals("available", announcement.attributes["pairing"])
        assertEquals("AA:BB:CC:DD:EE:FF", announcement.attributes["fp"])
    }
}
