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

        val codeBeforeDesktopConnects = manager.activeVerificationCode(nowMillis = 1_001)

        val result = manager.start(startRequest(), nowMillis = 2_000)
        val second = manager.start(startRequest(desktopId = "desktop-2"), nowMillis = 2_001)

        assertTrue(result is PairingResult.Success)
        val response = (result as PairingResult.Success).value
        assertEquals(5, response.attemptsRemaining)
        assertEquals(6, response.codeLength)
        assertNotNull(codeBeforeDesktopConnects)
        assertEquals(codeBeforeDesktopConnects, manager.activeVerificationCode(nowMillis = 2_002))
        assertNotNull(manager.activeVerificationCode(nowMillis = 2_002))
        assertTrue(manager.activeVerificationCode(nowMillis = 2_002)!!.matches(Regex("\\d{6}")))
        assertTrue(second is PairingResult.Failure)
        assertEquals("pairing_session_active", (second as PairingResult.Failure).code)
    }

    @Test
    fun qrCodeCanOpenWindowWithDesktopGeneratedVerificationCode() {
        val manager = PairingManager()

        manager.openPairingWindow(nowMillis = 1_000, verificationCode = "281604")
        val start = manager.start(startRequest(), nowMillis = 2_000) as PairingResult.Success

        assertEquals("281604", manager.activeVerificationCode(nowMillis = 2_001))
        val confirmed = manager.confirm(
            PairConfirmRequest(start.value.pairingSessionId, "281604"),
            nowMillis = 2_002,
        )
        assertTrue(confirmed is PairingResult.Success)
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
        val store = InMemoryPairingCredentialStore()
        val manager = PairingManager(credentialStore = store)
        manager.openPairingWindow(nowMillis = 1_000)
        val start = manager.start(startRequest(), nowMillis = 2_000) as PairingResult.Success
        val code = manager.activeVerificationCode(nowMillis = 2_001)

        val result = manager.confirm(
            PairConfirmRequest(start.value.pairingSessionId, checkNotNull(code)),
            nowMillis = 2_002,
        )

        assertTrue(result is PairingResult.Success)
        val response = (result as PairingResult.Success).value
        assertEquals(true, response.paired)
        assertEquals("Bearer", response.tokenType)
        assertTrue(response.accessToken.length >= 32)
        assertTrue(store.containsTokenHash(TokenHashing.sha256Base64Url(response.accessToken)))
        assertEquals("desktop-1", manager.authenticate(response.accessToken)?.desktopId)
        assertNull(manager.activeVerificationCode(nowMillis = 2_003))
    }

    @Test
    fun revokeRemovesStoredTokenHash() {
        val store = InMemoryPairingCredentialStore()
        val manager = PairingManager(credentialStore = store)
        manager.openPairingWindow(nowMillis = 1_000)
        val start = manager.start(startRequest(), nowMillis = 2_000) as PairingResult.Success
        val code = checkNotNull(manager.activeVerificationCode(nowMillis = 2_001))
        val confirm = manager.confirm(
            PairConfirmRequest(start.value.pairingSessionId, code),
            nowMillis = 2_002,
        ) as PairingResult.Success

        assertTrue(manager.revoke(confirm.value.accessToken))

        assertNull(manager.authenticate(confirm.value.accessToken))
        assertEquals(false, store.containsTokenHash(TokenHashing.sha256Base64Url(confirm.value.accessToken)))
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
