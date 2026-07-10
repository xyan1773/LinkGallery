package com.linkgallery.companion.pairing

import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import java.util.UUID

data class PairStartRequest(
    val desktopId: String,
    val desktopName: String,
    val desktopModel: String?,
    val identityPublicKey: String,
    val ephemeralPublicKey: String,
    val nonce: String,
)

data class PairConfirmRequest(
    val pairingSessionId: String,
    val verificationCode: String,
)

data class PairCancelRequest(
    val pairingSessionId: String,
)

data class PairStartResponse(
    val pairingSessionId: String,
    val phoneNonce: String,
    val expiresAtEpochMillis: Long,
    val attemptsRemaining: Int,
    val codeLength: Int,
)

data class PairConfirmResponse(
    val paired: Boolean,
    val accessToken: String,
    val tokenType: String = "Bearer",
)

sealed interface PairingResult<out T> {
    data class Success<T>(val value: T) : PairingResult<T>
    data class Failure(val status: Int, val code: String, val message: String) : PairingResult<Nothing>
}

interface PairingCoordinator {
    fun openPairingWindow(
        nowMillis: Long = System.currentTimeMillis(),
        verificationCode: String? = null,
    ): PairingWindow
    fun isPairingAvailable(nowMillis: Long = System.currentTimeMillis()): Boolean
    fun activeVerificationCode(nowMillis: Long = System.currentTimeMillis()): String?
    fun start(request: PairStartRequest, nowMillis: Long = System.currentTimeMillis()): PairingResult<PairStartResponse>
    fun confirm(request: PairConfirmRequest, nowMillis: Long = System.currentTimeMillis()): PairingResult<PairConfirmResponse>
    fun cancel(request: PairCancelRequest): PairingResult<Unit>
}

data class AuthenticatedPairing(
    val desktopId: String,
    val desktopName: String,
)

interface AccessTokenAuthenticator {
    fun authenticate(accessToken: String): AuthenticatedPairing?
    fun revoke(accessToken: String): Boolean
}

object AllowAllAccessTokenAuthenticator : AccessTokenAuthenticator {
    override fun authenticate(accessToken: String): AuthenticatedPairing =
        AuthenticatedPairing("test-desktop", "Test desktop")

    override fun revoke(accessToken: String): Boolean = true
}

object RejectingAccessTokenAuthenticator : AccessTokenAuthenticator {
    override fun authenticate(accessToken: String): AuthenticatedPairing? = null
    override fun revoke(accessToken: String): Boolean = false
}

object DisabledPairingCoordinator : PairingCoordinator {
    override fun openPairingWindow(nowMillis: Long, verificationCode: String?): PairingWindow =
        PairingWindow(nowMillis, nowMillis, "")
    override fun isPairingAvailable(nowMillis: Long): Boolean = false
    override fun activeVerificationCode(nowMillis: Long): String? = null
    override fun start(
        request: PairStartRequest,
        nowMillis: Long,
    ): PairingResult<PairStartResponse> = PairingResult.Failure(
        403,
        "pairing_unavailable",
        "Pairing must be opened on the Android device first.",
    )

    override fun confirm(
        request: PairConfirmRequest,
        nowMillis: Long,
    ): PairingResult<PairConfirmResponse> = PairingResult.Failure(
        404,
        "pairing_session_not_found",
        "The pairing session does not exist.",
    )

    override fun cancel(request: PairCancelRequest): PairingResult<Unit> = PairingResult.Success(Unit)
}

data class PairingWindow(
    val openedAtEpochMillis: Long,
    val expiresAtEpochMillis: Long,
    val verificationCode: String,
)

class PairingManager(
    private val random: SecureRandom = SecureRandom(),
    private val windowDurationMillis: Long = WINDOW_DURATION_MILLIS,
    private val credentialStore: PairingCredentialStore = InMemoryPairingCredentialStore(),
) : PairingCoordinator, AccessTokenAuthenticator {
    private val lock = Any()
    private var window: PairingWindow? = null
    private var activeSession: PairingSession? = null
    private var lockedUntilEpochMillis: Long = 0

    fun pairedCredentials(): List<PairedCredential> = credentialStore.list()

    override fun openPairingWindow(nowMillis: Long, verificationCode: String?): PairingWindow = synchronized(lock) {
        val requestedCode = verificationCode
            ?.takeIf { it.length == CODE_LENGTH && it.all(Char::isDigit) }
        val next = PairingWindow(
            openedAtEpochMillis = nowMillis,
            expiresAtEpochMillis = nowMillis + windowDurationMillis,
            verificationCode = requestedCode ?: randomVerificationCode(),
        )
        window = next
        activeSession = null
        next
    }

    override fun isPairingAvailable(nowMillis: Long): Boolean = synchronized(lock) {
        val current = window
        current != null && nowMillis < current.expiresAtEpochMillis && nowMillis >= lockedUntilEpochMillis
    }

    override fun activeVerificationCode(nowMillis: Long): String? = synchronized(lock) {
        activeSession?.takeIf { nowMillis < it.expiresAtEpochMillis }?.verificationCode
            ?: window?.takeIf { nowMillis < it.expiresAtEpochMillis }?.verificationCode
    }

    override fun start(
        request: PairStartRequest,
        nowMillis: Long,
    ): PairingResult<PairStartResponse> = synchronized(lock) {
        cleanExpired(nowMillis)
        validateStartRequest(request)?.let { return@synchronized it }
        val currentWindow = window
        if (currentWindow == null || nowMillis >= currentWindow.expiresAtEpochMillis) {
            return@synchronized failureUnavailable()
        }
        if (nowMillis < lockedUntilEpochMillis) {
            return@synchronized PairingResult.Failure(
                429,
                "pairing_rate_limited",
                "Too many incorrect pairing attempts. Try again later.",
            )
        }
        if (activeSession != null) {
            return@synchronized PairingResult.Failure(
                409,
                "pairing_session_active",
                "A pairing session is already active.",
            )
        }

        val phoneNonce = randomBase64(24)
        val expiresAt = minOf(currentWindow.expiresAtEpochMillis, nowMillis + SESSION_DURATION_MILLIS)
        val code = currentWindow.verificationCode
        val session = PairingSession(
            id = UUID.randomUUID().toString(),
            desktopId = request.desktopId,
            desktopName = request.desktopName,
            phoneNonce = phoneNonce,
            verificationCode = code,
            expiresAtEpochMillis = expiresAt,
            attemptsRemaining = MAX_ATTEMPTS,
        )
        activeSession = session
        PairingResult.Success(
            PairStartResponse(
                pairingSessionId = session.id,
                phoneNonce = phoneNonce,
                expiresAtEpochMillis = expiresAt,
                attemptsRemaining = session.attemptsRemaining,
                codeLength = CODE_LENGTH,
            ),
        )
    }

    override fun confirm(
        request: PairConfirmRequest,
        nowMillis: Long,
    ): PairingResult<PairConfirmResponse> = synchronized(lock) {
        cleanExpired(nowMillis)
        val session = activeSession ?: return@synchronized PairingResult.Failure(
            404,
            "pairing_session_not_found",
            "The pairing session does not exist.",
        )
        if (session.id != request.pairingSessionId) {
            return@synchronized PairingResult.Failure(
                404,
                "pairing_session_not_found",
                "The pairing session does not exist.",
            )
        }
        if (nowMillis >= session.expiresAtEpochMillis) {
            activeSession = null
            return@synchronized PairingResult.Failure(
                410,
                "pairing_expired",
                "The pairing session has expired.",
            )
        }
        if (!MessageDigest.isEqual(session.verificationCode.toByteArray(), request.verificationCode.toByteArray())) {
            session.attemptsRemaining -= 1
            if (session.attemptsRemaining <= 0) {
                activeSession = null
                lockedUntilEpochMillis = nowMillis + LOCKOUT_DURATION_MILLIS
                return@synchronized PairingResult.Failure(
                    429,
                    "pairing_attempts_exceeded",
                    "Too many incorrect pairing attempts. Start pairing again later.",
                )
            }
            return@synchronized PairingResult.Failure(
                403,
                "pairing_code_mismatch",
                "The pairing verification code is incorrect.",
            )
        }

        val accessToken = randomBase64(32)
        credentialStore.save(
            PairedCredential(
                desktopId = session.desktopId,
                desktopName = session.desktopName,
                tokenHash = TokenHashing.sha256Base64Url(accessToken),
                createdAtEpochMillis = nowMillis,
            ),
        )
        activeSession = null
        window = null
        PairingResult.Success(PairConfirmResponse(paired = true, accessToken = accessToken))
    }

    override fun cancel(request: PairCancelRequest): PairingResult<Unit> = synchronized(lock) {
        if (activeSession?.id == request.pairingSessionId) {
            activeSession = null
        }
        PairingResult.Success(Unit)
    }

    override fun authenticate(accessToken: String): AuthenticatedPairing? {
        if (accessToken.isBlank()) return null
        val credential = credentialStore.findByTokenHash(TokenHashing.sha256Base64Url(accessToken)) ?: return null
        return AuthenticatedPairing(credential.desktopId, credential.desktopName)
    }

    override fun revoke(accessToken: String): Boolean {
        if (accessToken.isBlank()) return false
        return credentialStore.revokeByTokenHash(TokenHashing.sha256Base64Url(accessToken))
    }

    private fun cleanExpired(nowMillis: Long) {
        if (window?.let { nowMillis >= it.expiresAtEpochMillis } == true) {
            window = null
            activeSession = null
        }
        if (activeSession?.let { nowMillis >= it.expiresAtEpochMillis } == true) {
            activeSession = null
        }
    }

    private fun validateStartRequest(request: PairStartRequest): PairingResult.Failure? {
        val required = listOf(
            request.desktopId,
            request.desktopName,
            request.identityPublicKey,
            request.ephemeralPublicKey,
            request.nonce,
        )
        return if (required.any { it.isBlank() }) {
            PairingResult.Failure(400, "invalid_pairing_request", "The pairing request is incomplete.")
        } else {
            null
        }
    }

    private fun failureUnavailable(): PairingResult.Failure = PairingResult.Failure(
        403,
        "pairing_unavailable",
        "Pairing must be opened on the Android device first.",
    )

    private fun randomVerificationCode(): String =
        random.nextInt(1_000_000).toString().padStart(CODE_LENGTH, '0')

    private fun randomBase64(size: Int): String {
        val bytes = ByteArray(size)
        random.nextBytes(bytes)
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    }

    private data class PairingSession(
        val id: String,
        val desktopId: String,
        val desktopName: String,
        val phoneNonce: String,
        val verificationCode: String,
        val expiresAtEpochMillis: Long,
        var attemptsRemaining: Int,
    )

    private companion object {
        const val WINDOW_DURATION_MILLIS = 120_000L
        const val SESSION_DURATION_MILLIS = 120_000L
        const val LOCKOUT_DURATION_MILLIS = 300_000L
        const val MAX_ATTEMPTS = 5
        const val CODE_LENGTH = 6
    }
}
