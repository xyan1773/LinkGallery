package com.linkgallery.companion.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ConnectionGuideTest {
    @Test
    fun emulatorUsesAdbLoopbackAndDoesNotAdvertiseNatAddress() {
        val guide = createConnectionGuide(
            isEmulator = true,
            lanAddresses = listOf("10.0.2.15"),
        )

        assertTrue(guide.address.contains("127.0.0.1:39570"))
        assertTrue(guide.detail.contains("ADB forward"))
        assertFalse(guide.address.contains("10.0.2.15"))
    }

    @Test
    fun physicalDeviceAdvertisesItsLanAddress() {
        val guide = createConnectionGuide(
            isEmulator = false,
            lanAddresses = listOf("192.168.1.42"),
        )

        assertEquals("Windows 连接地址：192.168.1.42:39570", guide.address)
    }
}
