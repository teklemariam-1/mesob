package com.mesob.player

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import com.mesob.core.ShopProtocol
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * Talks to a shop over its own Wi-Fi. Android prefers networks with internet and may route around
 * a shop's Wi-Fi, so the app binds its connections to the Wi-Fi network first.
 */
class ShopClient(private val context: Context, address: String) {
    private val base = ShopProtocol.baseUrl(address)

    data class Title(val id: String, val en: String, val ti: String, val artist: String, val kind: String, val size: Long, val price: Long)
    data class Catalog(val shopName: String, val titles: List<Title>)

    companion object {
        @Suppress("DEPRECATION")
        fun bindToWifi(context: Context) {
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val wifi = cm.allNetworks.firstOrNull { cm.getNetworkCapabilities(it)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true }
            cm.bindProcessToNetwork(wifi)
        }
    }

    private fun open(path: String): HttpURLConnection =
        (URL(base + path).openConnection() as HttpURLConnection).apply { connectTimeout = 8000; readTimeout = 20000 }

    private fun get(path: String): JSONObject = open(path).run {
        try {
            if (responseCode != 200) throw IOException("shop answered $responseCode")
            JSONObject(inputStream.bufferedReader().readText())
        } finally { disconnect() }
    }

    fun catalog(): Catalog {
        val o = get("api/catalog")
        val a = o.getJSONArray("titles")
        return Catalog(o.optString("shop_name", "Shop ${o.optInt("shop_id")}"), (0 until a.length()).map { i ->
            val t = a.getJSONObject(i)
            Title(t.getString("title_id"), t.getString("title_en"), t.optString("title_ti"), t.optString("artist"), t.getString("kind"), t.getLong("size"), t.getLong("price"))
        })
    }

    fun order(titleIds: List<String>, replaces: String? = null): JSONObject = open("api/orders").run {
        try {
            requestMethod = "POST"
            doOutput = true
            setRequestProperty("Content-Type", "application/json")
            outputStream.use { it.write(JSONObject().put("device_public", PlayerState.devicePublic).put("title_ids", JSONArray(titleIds)).put("replaces", replaces).toString().toByteArray()) }
            if (responseCode != 200) throw IOException("shop answered $responseCode")
            JSONObject(inputStream.bufferedReader().readText())
        } finally { disconnect() }
    }

    /** Poll with the private token the shop returned; the short code is only for the counter. */
    fun orderState(token: String): JSONObject = get("api/orders/$token")

    fun poster(titleId: String): ByteArray? = open("api/poster/$titleId").run {
        try { if (responseCode == 200) inputStream.readBytes() else null } finally { disconnect() }
    }

    /** Download with resume into app storage; the caller moves the finished file to Downloads/Mesob. */
    fun download(titleId: String, progress: (Float) -> Unit): File {
        val part = File(context.getExternalFilesDir(null) ?: context.filesDir, "$titleId.part")
        repeat(5) { attempt ->
            try {
                var have = if (part.exists()) part.length() else 0L
                val c = open("api/titles/$titleId")
                if (have > 0) c.setRequestProperty("Range", "bytes=$have-")
                try {
                    val code = c.responseCode
                    if (code != 200 && code != 206) throw IOException("shop answered $code")
                    if (code == 200) have = 0
                    val total = have + c.contentLengthLong.coerceAtLeast(0)
                    c.inputStream.use { inp ->
                        java.io.FileOutputStream(part, have > 0).use { out ->
                            val buf = ByteArray(1 shl 16)
                            while (true) {
                                val n = inp.read(buf)
                                if (n < 0) break
                                out.write(buf, 0, n)
                                have += n
                                if (total > 0) progress(have.toFloat() / total)
                            }
                        }
                    }
                    return part
                } finally { c.disconnect() }
            } catch (e: IOException) {
                if (attempt == 4) throw e
                Thread.sleep(1500)
            }
        }
        throw IOException("download failed")
    }
}
