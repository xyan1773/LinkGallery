package com.linkgallery.companion.pairing

import android.content.Context
import java.util.Base64

class AndroidPairingCredentialStore(context: Context) : PairingCredentialStore {
    private val preferences = context.getSharedPreferences("linkgallery_pairing_credentials", Context.MODE_PRIVATE)

    override fun save(credential: PairedCredential) {
        val updated = entries().filterNot { it.tokenHash == credential.tokenHash } + credential
        preferences.edit().putStringSet(KEY_CREDENTIALS, updated.mapTo(linkedSetOf()) { encode(it) }).apply()
    }

    override fun findByTokenHash(tokenHash: String): PairedCredential? =
        entries().firstOrNull { it.tokenHash == tokenHash }

    override fun revokeByTokenHash(tokenHash: String): Boolean {
        val existing = entries()
        val updated = existing.filterNot { it.tokenHash == tokenHash }
        if (updated.size == existing.size) return false
        preferences.edit().putStringSet(KEY_CREDENTIALS, updated.mapTo(linkedSetOf()) { encode(it) }).apply()
        return true
    }

    private fun entries(): List<PairedCredential> =
        preferences.getStringSet(KEY_CREDENTIALS, emptySet()).orEmpty().mapNotNull(::decode)

    private fun encode(credential: PairedCredential): String = listOf(
        credential.desktopId,
        credential.desktopName,
        credential.tokenHash,
        credential.createdAtEpochMillis.toString(),
    ).joinToString(separator = "|") { Base64.getUrlEncoder().withoutPadding().encodeToString(it.toByteArray()) }

    private fun decode(value: String): PairedCredential? = runCatching {
        val parts = value.split('|')
        if (parts.size != 4) return@runCatching null
        PairedCredential(
            desktopId = decodePart(parts[0]),
            desktopName = decodePart(parts[1]),
            tokenHash = decodePart(parts[2]),
            createdAtEpochMillis = decodePart(parts[3]).toLong(),
        )
    }.getOrNull()

    private fun decodePart(value: String): String =
        String(Base64.getUrlDecoder().decode(value), Charsets.UTF_8)

    private companion object {
        const val KEY_CREDENTIALS = "credentials"
    }
}
