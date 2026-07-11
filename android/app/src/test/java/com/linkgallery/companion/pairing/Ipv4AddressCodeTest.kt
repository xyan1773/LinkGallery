package com.linkgallery.companion.pairing

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class Ipv4AddressCodeTest {
    @Test
    fun encodesCompleteIpv4AddressWithoutLosingTheSubnet() {
        assertEquals("AC172D6C", Ipv4AddressCode.encode("172.23.45.108"))
        assertEquals("AC17-2D6C", Ipv4AddressCode.format("ac172d6c"))
    }

    @Test
    fun rejectsInvalidIpv4Addresses() {
        assertNull(Ipv4AddressCode.encode("172.23.999.1"))
        assertNull(Ipv4AddressCode.encode("not-an-address"))
    }
}
