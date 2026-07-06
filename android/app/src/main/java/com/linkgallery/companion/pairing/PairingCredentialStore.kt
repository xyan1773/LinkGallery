package com.linkgallery.companion.pairing

data class PairedCredential(
    val desktopId: String,
    val desktopName: String,
    val tokenHash: String,
    val createdAtEpochMillis: Long,
)

interface PairingCredentialStore {
    fun save(credential: PairedCredential)
    fun findByTokenHash(tokenHash: String): PairedCredential?
    fun revokeByTokenHash(tokenHash: String): Boolean
    fun list(): List<PairedCredential>
}

class InMemoryPairingCredentialStore : PairingCredentialStore {
    private val credentials = linkedMapOf<String, PairedCredential>()

    override fun save(credential: PairedCredential) {
        credentials[credential.tokenHash] = credential
    }

    override fun findByTokenHash(tokenHash: String): PairedCredential? = credentials[tokenHash]

    override fun revokeByTokenHash(tokenHash: String): Boolean =
        credentials.remove(tokenHash) != null

    override fun list(): List<PairedCredential> = credentials.values.toList()

    fun containsTokenHash(tokenHash: String): Boolean = credentials.containsKey(tokenHash)
}

object TokenHashing {
    fun sha256Base64Url(token: String): String {
        val digest = java.security.MessageDigest.getInstance("SHA-256")
            .digest(token.toByteArray(Charsets.UTF_8))
        return java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(digest)
    }
}
