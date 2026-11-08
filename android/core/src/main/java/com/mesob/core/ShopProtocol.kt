package com.mesob.core

import java.net.InetAddress

/**
 * The shop's local-network API, served by the Shop app on the shop Wi-Fi.
 * Plain HTTP is safe: title files are encrypted and signed, licenses are signed and sealed to one
 * device, so a fake shop can't produce anything a Player accepts.
 *
 *   GET  /api/catalog            → { shop_id, shop_name, titles:[{title_id,title_en,title_ti,artist,kind,size,price}] }
 *   GET  /api/poster/{title_id}  → image bytes
 *   POST /api/orders             { device_public, title_ids:[...], replaces?: token } → { code, token, total, items:[{title_id,title_en,price}] }
 *   GET  /api/orders/{token}     → { code, status: pending|paid|cancelled, total, licenses:[{title_id,bundle}] }
 *
 * The code (e.g. "K472": one letter, three digits, no I/O lookalikes) is what the customer shows or
 * says at the counter. The token (48 hex) is the customer's private handle for polling; codes are
 * short and guessable, so they are never accepted by the API.
 *   GET  /api/titles/{title_id}  → the .msb file (Range supported)
 */
object ShopProtocol {
    const val PORT = 8765
    const val QR_PREFIX = "mesob-shop://"

    /** "192.168.8.10", "192.168.8.10:8765" or "mesob-shop://…" → base URL. Only local addresses are allowed. */
    fun baseUrl(address: String): String {
        var a = address.trim().removePrefix(QR_PREFIX).trimEnd('/')
        if (!a.contains(':')) a += ":$PORT"
        val host = a.substringBefore(':')
        require(isLocal(host)) { "Mesob shops are only on local Wi-Fi addresses (192.168.x.x, 10.x.x.x, 172.16-31.x.x)." }
        return "http://$a/"
    }

    fun isLocal(host: String): Boolean = try {
        val addr = InetAddress.getByName(host)
        addr.isSiteLocalAddress || addr.isLoopbackAddress || addr.isLinkLocalAddress
    } catch (e: Exception) {
        false
    }
}
