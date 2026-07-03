package com.linkgallery.companion.server

import java.time.Instant
import java.util.UUID
import kotlin.random.Random

class InMemoryPairingSessionManager(
    private val deviceId: String,
    private val random: Random = Random.Default,
) : PairingSessionManager {
    private val lock = Any()
    private var pending: PendingPairing? = null

    override fun requestPairing(): PairingChallenge = synchronized(lock) {
        val code = random.nextInt(0, 1_000_000).toString().padStart(6, '0')
        val session = PendingPairing(
            sessionId = UUID.randomUUID().toString(),
            confirmationCode = code,
            expiresAt = Instant.now().plusSeconds(300),
        )
        pending = session
        PairingChallenge(
            sessionId = session.sessionId,
            confirmationCode = session.confirmationCode,
            expiresAt = session.expiresAt.toString(),
        )
    }

    override fun confirmPairing(
        sessionId: String,
        confirmationCode: String,
    ): PairingResult? = synchronized(lock) {
        val session = pending ?: return null
        if (session.expiresAt.isBefore(Instant.now()) ||
            session.sessionId != sessionId ||
            session.confirmationCode != confirmationCode
        ) {
            return null
        }

        pending = null
        PairingResult(
            deviceId = deviceId,
            devicePublicKey = "local-preview-public-key",
            accessToken = UUID.randomUUID().toString(),
            expiresAt = Instant.now().plusSeconds(86_400).toString(),
        )
    }

    private data class PendingPairing(
        val sessionId: String,
        val confirmationCode: String,
        val expiresAt: Instant,
    )
}
