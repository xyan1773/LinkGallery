package com.linkgallery.companion.identity

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class DeviceIdentityFormatTest {
    @Test
    fun deviceIdUsesBase32Sha256WithoutPadding() {
        val id = DeviceIdentityFormat.deviceIdFromCertificate("certificate".toByteArray())

        assertEquals("APLG3UEIGXA4UPYSRTHKZUPTDLEUCYYJNMQPIRNOQQUFXQEDFVZA", id)
        assertFalse(id.contains("="))
    }

    @Test
    fun fingerprintUsesColonSeparatedUppercaseSha256() {
        val fingerprint = DeviceIdentityFormat.fingerprint("certificate".toByteArray())

        assertEquals(
            "03:D6:6D:D0:88:35:C1:CA:3F:12:8C:CE:AC:D1:F3:1A:C9:41:63:09:6B:20:F4:45:AE:84:28:5B:C0:83:2D:72",
            fingerprint,
        )
    }

    @Test
    fun shortCodeUsesStablePrefix() {
        assertEquals(
            "LKG-APLG-3UEI",
            DeviceIdentityFormat.shortCode("APLG3UEIGXA4UPYSRTHKZUPTDLEUCYYJNMQPIRNOQQUFXQEDFVZA"),
        )
    }
}
