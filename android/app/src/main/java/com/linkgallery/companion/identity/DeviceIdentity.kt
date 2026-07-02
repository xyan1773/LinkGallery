package com.linkgallery.companion.identity

data class DeviceIdentity(
    val deviceId: String,
    val certificateFingerprint: String,
    val certificateDer: ByteArray,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is DeviceIdentity) return false
        return deviceId == other.deviceId &&
            certificateFingerprint == other.certificateFingerprint &&
            certificateDer.contentEquals(other.certificateDer)
    }

    override fun hashCode(): Int {
        var result = deviceId.hashCode()
        result = 31 * result + certificateFingerprint.hashCode()
        result = 31 * result + certificateDer.contentHashCode()
        return result
    }
}

interface DeviceIdentityProvider {
    fun getOrCreate(): DeviceIdentity
}
