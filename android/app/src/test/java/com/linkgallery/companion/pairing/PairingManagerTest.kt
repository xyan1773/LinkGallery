package com.linkgallery.companion.pairing

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingManagerTest {
    @Test
    fun startRequiresUserOpenedWindow() {
        val manager = PairingManager()

        val result = manager.start(startRequest(), nowMillis = 1_000)

        assertTrue(result is PairingResult.Failure)
        assertEquals("pairing_unavailable", (result as PairingResult.Failure).code)
    }

    @Test
    fun openWindowStartsSingleSessionAndExposesSixDigitCode() {
        val manager = PairingManager()
        manager.openPairingWindow(nowMillis = 1_000)

        val result = manager.start(startRequest(), nowMillis = 2_000)
        val second = manager.start(startRequest(desktopId = "desktop-2"), nowMillis = 2_001)

        assertTrue(result is PairingResult.Success)
        val response = (result as PairingResult.Success).value
        assertEquals(5, response.attemptsRemaining)
        assertEquals(6, response.codeLength)
        assertNotNull(manager.activeVerificationCode(nowMillis = 2_002))
        assertTrue(manager.activeVerificationCode(nowMillis = 2_002)!!.matches(Regex("\\d{6}")))
        assertTrue(second is PairingResult.Failure)
        assertEquals("pairing_session_active", (second as PairingResult.Failure).code)
    }

    @Test
    fun confirmRejectsWrongCodeAndLocksAfterFiveAttempts() {
        val manager = PairingManager()
        manager.openPairingWindow(nowMillis = 1_000)
        val start = manager.start(startRequest(), nowMillis = 2_000) as PairingResult.Success
        val request = PairConfirmRequest(start.value.pairingSessionId, "000000")

        repeat(4) {
            val result = manager.confirm(request, nowMillis = 3_000L + it)
            assertTrue(result is PairingResult.Failure)
            assertEquals("pairing_code_mismatch", (result as PairingResult.Failure).code)
        }
        val fifth = manager.confirm(request, nowMillis = 4_000)
        val restart = manager.start(startRequest(desktopId = "desktop-2"), nowMillis = 4_001)

        assertTrue(fifth is PairingResult.Failure)
        assertEquals("pairing_attempts_exceeded", (fifth as PairingResult.Failure).code)
        assertTrue(restart is PairingResult.Failure)
        assertEquals("pairing_rate_limited", (restart as PairingResult.Failure).code)
    }

    @Test
    fun confirmWithDisplayedCodePairsAndClearsSession() {
        val manager = PairingManager()
        manager.openPairingWindow(nowMillis = 1_000)
        val start = manager.start(startRequest(), nowMillis = 2_000) as PairingResult.Success
        val code = manager.activeVerificationCode(nowMillis = 2_001)

        val result = manager.confirm(
            PairConfirmRequest(start.value.pairingSessionId, checkNotNull(code)),
            nowMillis = 2_002,
        )

        assertTrue(result is PairingResult.Success)
        assertEquals(true, (result as PairingResult.Success).value.paired)
        assertNull(manager.activeVerificationCode(nowMillis = 2_003))
    }

    @Test
    fun cancelClearsActiveSession() {
        val manager = PairingManager()
        manager.openPairingWindow(nowMillis = 1_000)
        val start = manager.start(startRequest(), nowMillis = 2_000) as PairingResult.Success

        val cancel = manager.cancel(PairCancelRequest(start.value.pairingSessionId))
        val confirm = manager.confirm(
            PairConfirmRequest(start.value.pairingSessionId, "000000"),
            nowMillis = 2_001,
        )

        assertTrue(cancel is PairingResult.Success)
        assertTrue(confirm is PairingResult.Failure)
        assertEquals("pairing_session_not_found", (confirm as PairingResult.Failure).code)
    }

    @Test
    fun expiredWindowStopsPairing() {
        val manager = PairingManager()
        manager.openPairingWindow(nowMillis = 1_000)

        val result = manager.start(startRequest(), nowMillis = 122_000)

        assertTrue(result is PairingResult.Failure)
        assertEquals("pairing_unavailable", (result as PairingResult.Failure).code)
    }

    private fun startRequest(desktopId: String = "desktop-1") = PairStartRequest(
        desktopId = desktopId,
        desktopName = "Windows PC",
        desktopModel = "Windows",
        identityPublicKey = "identity-key",
        ephemeralPublicKey = "ephemeral-key",
        nonce = "desktop-nonce",
    )
}
