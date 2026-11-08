package com.mesob.core

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.os.Build
import android.os.Debug
import java.io.File
import java.security.MessageDigest

/**
 * Checks run before protected content is shown. None of these is unbreakable on a rooted phone;
 * together they stop casual tools and make extraction slow and specialised work.
 */
object Protection {
    data class Report(val problems: List<String>) {
        val ok get() = problems.isEmpty()
    }

    fun check(context: Context): Report {
        val p = mutableListOf<String>()
        if (Debug.isDebuggerConnected() || Debug.waitingForDebugger()) p += "A debugger is attached."
        val debuggable = context.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0
        if (debuggable && !Mesob.isLab) p += "This copy of the app has been modified (debuggable)."
        if (!Mesob.isLab && looksRooted()) p += "This phone has been rooted."
        if (!Mesob.isLab && looksLikeEmulator()) p += "Protected titles don't play on emulators."
        val expected = BuildConfig.SIGNING_CERT_SHA256
        if (expected.isNotEmpty() && !expected.equals(signingCertSha256(context), ignoreCase = true)) {
            p += "This copy of the app was not published by Mesob. Install it from a Mesob shop."
        }
        return Report(p)
    }

    private fun looksRooted(): Boolean {
        val paths = listOf(
            "/system/bin/su", "/system/xbin/su", "/sbin/su", "/su/bin/su", "/data/local/su",
            "/data/local/xbin/su", "/data/local/bin/su", "/system/app/Superuser.apk", "/data/adb/magisk",
        )
        if (paths.any { File(it).exists() }) return true
        return Build.TAGS?.contains("test-keys") == true
    }

    private fun looksLikeEmulator(): Boolean {
        val f = Build.FINGERPRINT.lowercase()
        return f.startsWith("generic") || f.contains("emulator") || f.contains("sdk_gphone") ||
            Build.HARDWARE.contains("goldfish") || Build.HARDWARE.contains("ranchu") ||
            Build.PRODUCT.contains("sdk") || Build.MODEL.contains("Android SDK built for")
    }

    @Suppress("DEPRECATION")
    fun signingCertSha256(context: Context): String {
        val pm = context.packageManager
        val sigs = if (Build.VERSION.SDK_INT >= 28) {
            pm.getPackageInfo(context.packageName, PackageManager.GET_SIGNING_CERTIFICATES).signingInfo?.apkContentsSigners
        } else {
            pm.getPackageInfo(context.packageName, PackageManager.GET_SIGNATURES).signatures
        }
        val cert = sigs?.firstOrNull()?.toByteArray() ?: return ""
        return MessageDigest.getInstance("SHA-256").digest(cert).joinToString("") { "%02x".format(it) }
    }

    /** Short watermark: license serial plus the start of the device code. */
    fun watermark(serial: Long, deviceHex: String) = "$serial · ${deviceHex.take(8).uppercase()}"
}
