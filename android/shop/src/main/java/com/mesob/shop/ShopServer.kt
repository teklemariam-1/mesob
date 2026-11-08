package com.mesob.shop

import android.util.Base64
import com.mesob.core.Mesob
import com.mesob.core.ShopProtocol
import fi.iki.elonen.NanoHTTPD
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.net.Inet4Address
import java.net.NetworkInterface

/** The shop's local Wi-Fi API (see com.mesob.core.ShopProtocol). */
class ShopServer : NanoHTTPD(ShopProtocol.PORT) {

    private fun json(o: Any, status: Response.Status = Response.Status.OK) =
        newFixedLengthResponse(status, "application/json", o.toString())

    private fun error(status: Response.Status, msg: String) = json(JSONObject().put("error", msg), status)

    override fun serve(session: IHTTPSession): Response = try {
        val path = session.uri.trimEnd('/')
        when {
            path == "/api/catalog" && session.method == Method.GET -> json(JSONObject()
                .put("shop_id", ShopState.shopId)
                .put("shop_name", ShopState.shopName)
                .put("titles", JSONArray(ShopState.catalog())))

            path.startsWith("/api/poster/") -> {
                val id = path.removePrefix("/api/poster/")
                val f = File(ShopState.titlesDir, "$id.msb")
                val poster = Base64.decode(Mesob.call("title.inspect", Mesob.obj("path" to f.path)).getString("poster"), Base64.NO_WRAP)
                if (poster.isEmpty()) error(Response.Status.NOT_FOUND, "no poster")
                else newFixedLengthResponse(Response.Status.OK, "image/jpeg", poster.inputStream(), poster.size.toLong())
            }

            path == "/api/orders" && session.method == Method.POST -> {
                val body = HashMap<String, String>()
                session.parseBody(body)
                val req = JSONObject(body["postData"] ?: "{}")
                val ids = req.getJSONArray("title_ids").let { a -> (0 until a.length()).map { a.getString(it) } }
                val o = ShopState.newOrder(req.getString("device_public"), ids, req.optString("replaces").takeIf { it.matches(Regex("[0-9a-f]{48}")) })
                json(JSONObject().put("code", o.code).put("token", o.token).put("total", o.total).put("items", JSONArray(o.titleIds.map { id ->
                    val t = ShopState.title(id)!!
                    JSONObject().put("title_id", id).put("title_en", t.getString("title_en")).put("price", t.getLong("price"))
                })))
            }

            path.startsWith("/api/orders/") && session.method == Method.GET -> {
                // Only the private token finds an order; short codes are for the counter.
                val token = path.removePrefix("/api/orders/")
                val o = if (token.matches(Regex("[0-9a-f]{48}"))) ShopState.byToken(token) else null
                if (o == null) error(Response.Status.NOT_FOUND, "no such order")
                else json(JSONObject().put("code", o.code).put("status", o.status).put("total", o.total)
                    .put("licenses", JSONArray(o.licenses.map { JSONObject().put("title_id", it.titleId).put("bundle", it.bundle) })))
            }

            path.startsWith("/api/titles/") -> serveFile(session, path.removePrefix("/api/titles/"))

            else -> error(Response.Status.NOT_FOUND, "not found")
        }
    } catch (e: Exception) {
        error(Response.Status.BAD_REQUEST, e.message ?: "bad request")
    }

    /** Title files with Range support so phones can resume broken downloads. */
    private fun serveFile(session: IHTTPSession, id: String): Response {
        if (!id.matches(Regex("[0-9a-f]{32}"))) return error(Response.Status.BAD_REQUEST, "bad title id")
        val f = File(ShopState.titlesDir, "$id.msb")
        if (!f.exists()) return error(Response.Status.NOT_FOUND, "not in this shop")
        val len = f.length()
        val range = session.headers["range"]?.removePrefix("bytes=")?.substringBefore('-')?.toLongOrNull()
        return if (range != null && range in 1 until len) {
            val inp = FileInputStream(f).apply { skip(range) }
            newFixedLengthResponse(Response.Status.PARTIAL_CONTENT, "application/octet-stream", inp, len - range).apply {
                addHeader("Content-Range", "bytes $range-${len - 1}/$len")
                addHeader("Accept-Ranges", "bytes")
            }
        } else {
            newFixedLengthResponse(Response.Status.OK, "application/octet-stream", FileInputStream(f), len).apply { addHeader("Accept-Ranges", "bytes") }
        }
    }

    companion object {
        /** The tablet's address on the shop Wi-Fi, for the QR code customers scan. */
        fun localAddress(): String? = NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .firstOrNull { it is Inet4Address && it.isSiteLocalAddress }
            ?.hostAddress
    }
}
