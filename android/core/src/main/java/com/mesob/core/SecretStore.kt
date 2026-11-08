package com.mesob.core

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Small secrets (device key, shop keys) stored on disk, locked by an AES key that lives in the
 * Android Keystore (hardware-backed on most phones) and can't be exported. Only this app can use
 * the Keystore key, and backups are disabled in the manifest, so the secrets can't leave the phone.
 */
class SecretStore(context: Context, private val alias: String = "mesob-secret-store") {
    private val dir = File(context.noBackupFilesDir, "mesob-keys").apply { mkdirs() }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(alias, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    /** True when the Keystore key is inside secure hardware (TEE or StrongBox). */
    val hardwareBacked: Boolean
        get() = try {
            val k = key()
            val f = javax.crypto.SecretKeyFactory.getInstance(k.algorithm, "AndroidKeyStore")
            val info = f.getKeySpec(k, android.security.keystore.KeyInfo::class.java) as android.security.keystore.KeyInfo
            @Suppress("DEPRECATION")
            info.isInsideSecureHardware
        } catch (e: Exception) {
            false
        }

    fun save(name: String, secret: ByteArray) {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        val blob = c.iv + c.doFinal(secret)
        File(dir, "$name.locked").writeBytes(blob)
    }

    fun load(name: String): ByteArray? {
        val f = File(dir, "$name.locked")
        if (!f.exists()) return null
        val blob = f.readBytes()
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, blob.copyOfRange(0, 12)))
        return c.doFinal(blob, 12, blob.size - 12)
    }

    fun saveB64(name: String, b64: String) = save(name, Base64.decode(b64, Base64.NO_WRAP))
    fun loadB64(name: String): String? = load(name)?.let { Base64.encodeToString(it, Base64.NO_WRAP) }
}
