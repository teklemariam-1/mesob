package com.mesob.player

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.util.Base64
import androidx.compose.runtime.mutableStateListOf
import com.mesob.core.Mesob
import com.mesob.core.SecretStore
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.InputStream

/** A title in the library: where the encrypted file is, and (once bought) its license bundle. */
data class Item(
    val uri: String,
    val titleId: String,
    val titleEn: String,
    val titleTi: String,
    val artist: String,
    val kind: String,
    val mime: String,
    val size: Long,
    val bundle: String? = null,
    val serial: Long = 0,
    val poster: String? = null,
) {
    val locked get() = bundle == null
    val name get() = if (titleTi.isBlank()) titleEn else "$titleEn · $titleTi"

    fun toJson(): JSONObject = Mesob.obj(
        "uri" to uri, "title_id" to titleId, "title_en" to titleEn, "title_ti" to titleTi, "artist" to artist,
        "kind" to kind, "mime" to mime, "size" to size, "bundle" to bundle, "serial" to serial, "poster" to poster,
    )

    companion object {
        fun fromJson(o: JSONObject) = Item(
            o.getString("uri"), o.getString("title_id"), o.getString("title_en"), o.optString("title_ti"), o.optString("artist"),
            o.getString("kind"), o.getString("mime"), o.getLong("size"),
            o.optString("bundle").ifEmpty { null }, o.optLong("serial"), o.optString("poster").ifEmpty { null },
        )
    }
}

/** Device key, library and imports. */
object PlayerState {
    lateinit var app: Context
    lateinit var store: SecretStore
    var deviceSecret = ""; private set
    var devicePublic = ""; private set
    var fingerprint = ""; private set
    val library = mutableStateListOf<Item>()
    private val pending = mutableListOf<String>()

    private val libraryFile get() = File(app.filesDir, "library.json")
    private val pendingFile get() = File(app.filesDir, "pending-licenses.json")

    fun init(context: Context) {
        if (::app.isInitialized) return
        app = context.applicationContext
        store = SecretStore(app, "mesob-player")
        deviceSecret = store.loadB64("device") ?: Mesob.call("device.create").getString("secret").also { store.saveB64("device", it) }
        val info = Mesob.call("device.info", Mesob.obj("secret" to deviceSecret))
        devicePublic = info.getString("public")
        fingerprint = info.getString("fingerprint")
        if (libraryFile.exists()) {
            val a = JSONArray(libraryFile.readText())
            for (i in 0 until a.length()) library += Item.fromJson(a.getJSONObject(i))
        }
        if (pendingFile.exists()) {
            val a = JSONArray(pendingFile.readText())
            for (i in 0 until a.length()) pending += a.getString(i)
        }
    }

    private fun save() {
        libraryFile.writeText(JSONArray().apply { library.forEach { put(it.toJson()) } }.toString())
        pendingFile.writeText(JSONArray(pending).toString())
    }

    /** A file descriptor the core takes ownership of. */
    fun fd(uri: String): Int = app.contentResolver.openFileDescriptor(Uri.parse(uri), "r")!!.detachFd()

    private fun magic(uri: Uri): String = app.contentResolver.openInputStream(uri)!!.use { s ->
        val b = ByteArray(4)
        if (s.read(b) == 4) String(b, Charsets.US_ASCII) else ""
    }

    /** Open a shared file: a title (.msb) goes into the library, a license (.msl/.msls) is checked and attached. */
    fun importUri(uri: Uri): String = when (magic(uri)) {
        "MSB1" -> importTitle(uri).let { "Added ${it.name}." }
        "MSL1" -> {
            val b = app.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
            importLicense(Base64.encodeToString(b, Base64.NO_WRAP)); "License added."
        }
        else -> {
            val b = app.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
            val n = readLicenseList(b).count { runCatching { importLicense(it) }.isSuccess }
            if (n == 0) throw IllegalArgumentException("That isn't a Mesob title or license file.") else "$n license(s) added."
        }
    }

    fun importTitle(source: Uri): Item {
        var uri = source
        try {
            app.contentResolver.takePersistableUriPermission(source, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        } catch (e: SecurityException) {
            // Not a permanent link (e.g. shared by another app): keep our own copy in Downloads/Mesob.
            uri = app.contentResolver.openInputStream(source)!!.use { saveToDownloads("mesob-${System.currentTimeMillis()}.msb", it) }
        }
        val h = Mesob.call("title.inspect", Mesob.obj("fd" to fd(uri.toString()), "check" to true))
        val id = h.getString("title_id")
        val posterB64 = h.optString("poster")
        val poster = if (posterB64.isNotEmpty()) File(app.filesDir, "poster-$id").also { it.writeBytes(Base64.decode(posterB64, Base64.NO_WRAP)) }.path else null
        val old = library.firstOrNull { it.titleId == id }
        val item = Item(
            uri.toString(), id, h.getString("title_en"), h.optString("title_ti"), h.optString("artist"),
            h.getString("kind"), h.getString("mime"), h.getLong("size"), old?.bundle, old?.serial ?: 0, poster,
        )
        if (old != null) library[library.indexOf(old)] = item else library += item
        pending.toList().forEach { if (attach(it)) pending.remove(it) }
        save()
        return library.first { it.titleId == id }
    }

    /** Check a license for this device; attach it to its title or keep it until the title arrives. */
    fun importLicense(bundle: String): String {
        val v = Mesob.call("license.verify", Mesob.obj("bundle" to bundle, "device_secret" to deviceSecret))
        if (!attach(bundle) && bundle !in pending) pending += bundle
        save()
        return v.getString("title_id")
    }

    private fun attach(bundle: String): Boolean {
        val v = Mesob.call("license.verify", Mesob.obj("bundle" to bundle, "device_secret" to deviceSecret))
        val i = library.indexOfFirst { it.titleId == v.getString("title_id") }
        if (i < 0) return false
        library[i] = library[i].copy(bundle = bundle, serial = v.getLong("serial"))
        return true
    }

    fun readLicenseList(b: ByteArray): List<String> {
        val out = mutableListOf<String>()
        var i = 0
        while (i + 2 <= b.size) {
            val n = ((b[i].toInt() and 0xff) shl 8) or (b[i + 1].toInt() and 0xff)
            if (i + 2 + n > b.size) break
            out += Base64.encodeToString(b, i + 2, n, Base64.NO_WRAP)
            i += 2 + n
        }
        return out
    }

    fun requestFor(titleIds: List<String>): String =
        Mesob.call("request.make", Mesob.obj("device_secret" to deviceSecret, "title_ids" to JSONArray(titleIds))).getString("request")

    /**
     * Titles live in Downloads/Mesob where file managers and Xender can see them, so customers can
     * pass them on. They're useless without a license for the receiving phone.
     */
    fun saveToDownloads(name: String, data: InputStream): Uri {
        return if (Build.VERSION.SDK_INT >= 29) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, name)
                put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream")
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Mesob")
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val r = app.contentResolver
            val uri = r.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)!!
            r.openOutputStream(uri)!!.use { data.copyTo(it, 1 shl 16) }
            values.clear()
            values.put(MediaStore.Downloads.IS_PENDING, 0)
            r.update(uri, values, null, null)
            uri
        } else {
            @Suppress("DEPRECATION")
            val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Mesob").apply { mkdirs() }
            val f = File(dir, name)
            f.outputStream().use { data.copyTo(it, 1 shl 16) }
            Uri.fromFile(f)
        }
    }
}
