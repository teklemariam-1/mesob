package com.mesob.player

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import org.json.JSONArray
import org.json.JSONObject

/**
 * The customer's cart and the order waiting at the counter. Both survive closing the app, so a
 * customer can fill the cart, walk to the counter, and still show the code if the phone restarts.
 */
object Cart {
    data class Line(val id: String, val en: String, val ti: String, val artist: String, val kind: String, val price: Long) {
        fun toJson(): JSONObject = JSONObject().put("id", id).put("en", en).put("ti", ti).put("artist", artist).put("kind", kind).put("price", price)
        companion object {
            fun of(t: ShopClient.Title) = Line(t.id, t.en, t.ti, t.artist, t.kind, t.price)
            fun fromJson(o: JSONObject) = Line(o.getString("id"), o.getString("en"), o.optString("ti"), o.optString("artist"), o.getString("kind"), o.getLong("price"))
        }
    }

    /** An order placed at a shop, waiting for the cashier. [token] is private; [code] is said at the counter. */
    data class Pending(val address: String, val shopName: String, val code: String, val token: String, val total: Long, val lines: List<Line>) {
        fun toJson(): JSONObject = JSONObject().put("address", address).put("shop", shopName).put("code", code).put("token", token)
            .put("total", total).put("lines", JSONArray(lines.map { it.toJson() }))
        companion object {
            fun fromJson(o: JSONObject) = Pending(
                o.getString("address"), o.optString("shop"), o.getString("code"), o.getString("token"), o.getLong("total"),
                o.getJSONArray("lines").let { a -> (0 until a.length()).map { Line.fromJson(a.getJSONObject(it)) } },
            )
        }
    }

    val lines = mutableStateListOf<Line>()
    var pending by mutableStateOf<Pending?>(null); private set
    var lastAddress by mutableStateOf(""); private set
    var lastShopName by mutableStateOf(""); private set
    /** Token of an order the customer went back to change; the next order replaces it at the shop. */
    var replaces: String? = null; private set
    private var loaded = false

    private val prefs get() = PlayerState.app.getSharedPreferences("cart", Context.MODE_PRIVATE)

    fun load() {
        if (loaded) return
        loaded = true
        runCatching {
            val a = JSONArray(prefs.getString("lines", "[]"))
            lines.clear()
            for (i in 0 until a.length()) lines.add(Line.fromJson(a.getJSONObject(i)))
        }
        pending = prefs.getString("pending", null)?.let { runCatching { Pending.fromJson(JSONObject(it)) }.getOrNull() }
        lastAddress = prefs.getString("address", "") ?: ""
        lastShopName = prefs.getString("shop", "") ?: ""
        replaces = prefs.getString("replaces", null)
    }

    private fun save() {
        prefs.edit()
            .putString("lines", JSONArray(lines.map { it.toJson() }).toString())
            .putString("pending", pending?.toJson()?.toString())
            .putString("address", lastAddress)
            .putString("shop", lastShopName)
            .putString("replaces", replaces)
            .apply()
    }

    val total get() = lines.sumOf { it.price }
    operator fun contains(id: String) = lines.any { it.id == id }

    fun toggle(t: ShopClient.Title) {
        if (t.id in this) lines.removeAll { it.id == t.id } else lines.add(Line.of(t))
        save()
    }

    fun remove(id: String) { lines.removeAll { it.id == id }; save() }

    /** Connected to a shop: remember it, and keep only cart lines in [sells] (sold here, not already owned). Returns how many were dropped. */
    fun atShop(address: String, name: String, sells: Set<String>): Int {
        lastAddress = address
        lastShopName = name
        val before = lines.size
        lines.removeAll { it.id !in sells }
        save()
        return before - lines.size
    }

    fun placed(p: Pending) { pending = p; replaces = null; save() }

    /** Back to the cart without the order (to change it). Ordering again replaces the waiting order at the shop. */
    fun forgetOrder() { replaces = pending?.token; pending = null; save() }

    /** Paid and downloaded: empty the cart. */
    fun finished() { pending = null; replaces = null; lines.clear(); save() }
}
