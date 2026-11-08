package com.mesob.shop

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import androidx.annotation.RequiresApi
import com.mesob.core.Mesob
import com.mesob.core.SecretStore
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.KeyAgreement

/**
 * The shop's keys, held by Android Keystore so they can't be copied off the tablet.
 *
 *  - Signing key (licenses and the sales log): always in Keystore. Only this app can use it, and
 *    it can't be exported, so a copied tablet image can't mint licenses anywhere else.
 *  - Key-exchange key (title keys are sealed to it): in Keystore on Android 12+ (hardware ECDH).
 *    Older tablets fall back to a software key locked by a Keystore AES key; shop tablets should
 *    be Android 12+.
 *
 * The signing key is created with an attestation challenge; its certificate chain goes to the
 * studio in the setup request so it can be checked against Google's attestation root.
 */
class ShopKeys(private val store: SecretStore) {
    companion object {
        private const val SIGN = "mesob-shop-sign"
        private const val KEX = "mesob-shop-kex"
    }

    private val ks: KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    val hardwareKex: Boolean get() = Build.VERSION.SDK_INT >= 31

    fun ensure() {
        if (!ks.containsAlias(SIGN)) createSigningKey()
        if (hardwareKex) {
            if (!ks.containsAlias(KEX)) createHardwareKex()
        } else if (store.load("kex") == null) {
            val k = Mesob.call("studio.kexgen")
            store.saveB64("kex", k.getString("secret"))
            store.saveB64("kex-public", k.getString("public"))
        }
    }

    private fun createSigningKey() {
        val challenge = ByteArray(16).also { SecureRandom().nextBytes(it) }
        fun spec(attest: Boolean) = KeyGenParameterSpec.Builder(SIGN, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .apply { if (attest) setAttestationChallenge(challenge) }
            .build()
        val g = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        try {
            g.initialize(spec(true))
            g.generateKeyPair()
        } catch (e: Exception) {
            g.initialize(spec(false)) // tablets without key attestation
            g.generateKeyPair()
        }
    }

    @RequiresApi(31)
    private fun createHardwareKex() {
        val g = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        g.initialize(
            KeyGenParameterSpec.Builder(KEX, KeyProperties.PURPOSE_AGREE_KEY)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .build()
        )
        g.generateKeyPair()
    }

    private fun sec1(spki: ByteArray): String =
        Mesob.call("key.spki_to_sec1", Mesob.obj("spki" to Base64.encodeToString(spki, Base64.NO_WRAP))).getString("public")

    /** Public signing key (SEC1, base64) for the setup request. */
    fun signPublic(): String = sec1(ks.getCertificate(SIGN).publicKey.encoded)

    /** Public key-exchange key (SEC1, base64) for the setup request. */
    fun kexPublic(): String =
        if (hardwareKex) sec1(ks.getCertificate(KEX).publicKey.encoded)
        else store.loadB64("kex-public") ?: throw IllegalStateException("shop keys not created")

    /** Attestation chain of the signing key (DER certificates, leaf first). */
    fun attestation(): List<String> =
        ks.getCertificateChain(SIGN)?.map { Base64.encodeToString(it.encoded, Base64.NO_WRAP) } ?: emptyList()

    /** Sign the `to_sign` bytes the core prepared. Returns a DER signature (base64). */
    fun sign(toSignB64: String): String {
        val s = Signature.getInstance("SHA256withECDSA")
        s.initSign(ks.getKey(SIGN, null) as PrivateKey)
        s.update(Base64.decode(toSignB64, Base64.NO_WRAP))
        return Base64.encodeToString(s.sign(), Base64.NO_WRAP)
    }

    /**
     * Fields that let the core open something sealed to this shop: the hardware ECDH result for
     * that sealed blob (Android 12+), or the software key (older tablets).
     */
    fun openFields(sealedB64: String): Array<Pair<String, Any?>> {
        if (!hardwareKex) return arrayOf("kex_secret" to store.loadB64("kex"))
        val ephSpki = Base64.decode(Mesob.call("shop.sealed_eph", Mesob.obj("sealed" to sealedB64)).getString("eph_spki"), Base64.NO_WRAP)
        val eph = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(ephSpki))
        val ka = KeyAgreement.getInstance("ECDH", "AndroidKeyStore")
        ka.init(ks.getKey(KEX, null) as PrivateKey)
        ka.doPhase(eph, true)
        return arrayOf("kex_shared" to Base64.encodeToString(ka.generateSecret(), Base64.NO_WRAP), "kex_public" to kexPublic())
    }
}
