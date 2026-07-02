package com.linkgallery.companion.identity

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.util.Date
import javax.security.auth.x500.X500Principal

class AndroidKeystoreDeviceIdentityProvider(
    private val alias: String = DEFAULT_ALIAS,
) : DeviceIdentityProvider {
    private val keyStore: KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply {
        load(null)
    }

    override fun getOrCreate(): DeviceIdentity {
        if (!keyStore.containsAlias(alias)) {
            generateIdentityKey()
        }
        val certificate = checkNotNull(keyStore.getCertificate(alias)) {
            "Device identity certificate was not created."
        }
        val der = certificate.encoded
        return DeviceIdentity(
            deviceId = DeviceIdentityFormat.deviceIdFromCertificate(der),
            certificateFingerprint = DeviceIdentityFormat.fingerprint(der),
            certificateDer = der,
        )
    }

    private fun generateIdentityKey() {
        val now = System.currentTimeMillis()
        val notBefore = Date(now - CLOCK_SKEW_MILLISECONDS)
        val notAfter = Date(now + CERTIFICATE_VALIDITY_MILLISECONDS)
        val generator = KeyPairGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_RSA,
            ANDROID_KEYSTORE,
        )
        generator.initialize(
            KeyGenParameterSpec.Builder(
                alias,
                KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY,
            )
                .setDigests(KeyProperties.DIGEST_SHA256)
                .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1)
                .setCertificateSubject(X500Principal("CN=LinkGallery Device Identity"))
                .setCertificateSerialNumber(BigInteger.valueOf(now))
                .setCertificateNotBefore(notBefore)
                .setCertificateNotAfter(notAfter)
                .build(),
        )
        generator.generateKeyPair()
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val DEFAULT_ALIAS = "linkgallery_device_identity_v1"
        const val CLOCK_SKEW_MILLISECONDS = 60_000L
        const val CERTIFICATE_VALIDITY_MILLISECONDS = 20L * 365 * 24 * 60 * 60 * 1000
    }
}
