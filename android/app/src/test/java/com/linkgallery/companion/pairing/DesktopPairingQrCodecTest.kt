package com.linkgallery.companion.pairing

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DesktopPairingQrCodecTest {
    @Test
    fun parsesVersionedDesktopPairingRequest() {
        val payload = DesktopPairingQrCodec.parse(
            "linkgallery://pair?v=1&code=281604&desktopId=pc-1&desktopName=Living%20Room",
        )

        assertEquals("281604", payload?.verificationCode)
        assertEquals("pc-1", payload?.desktopId)
        assertEquals("Living Room", payload?.desktopName)
    }

    @Test
    fun rejectsInvalidOrNonPairingQr() {
        assertNull(DesktopPairingQrCodec.parse("https://example.com"))
        assertNull(DesktopPairingQrCodec.parse("linkgallery://pair?v=1&code=123"))
    }
}
