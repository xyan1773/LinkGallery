package com.linkgallery.companion.pairing

interface PairingStore {
    suspend fun find(deviceId: String): PairedDevice?

    suspend fun save(device: PairedDevice)

    suspend fun revoke(deviceId: String)
}

data class PairedDevice(
    val deviceId: String,
    val displayName: String,
    val publicKeyFingerprint: String,
)

