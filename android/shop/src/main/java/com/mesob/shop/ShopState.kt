package com.mesob.shop

import android.content.Context
import android.util.Base64
import androidx.compose.runtime.mutableIntStateOf
import com.mesob.core.Mesob
import com.mesob.core.SecretStore
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.time.LocalDate
import java.time.ZoneId

/**
 * Everything the shop tablet holds. Keys are locked by the Android Keystore; the rest lives in
 * app-private storage (titles on the largest storage, usually the microSD card).
 *
 * Rules enforced here (the studio checks them again from the sales log):
 *  - a license is only issued from an unlocked credit pack, and each serial is used once;
 *  - the sale is written to the signed, chained log BEFORE the license leaves the tablet;
 *  - a replacement license reuses the original serial and is limited to [MAX_REISSUES].
 */
object ShopState {
    const val MAX_REISSUES = 2
    lateinit var app: Context
    private lateinit var store: SecretStore
    private lateinit var keys: ShopKeys
    private lateinit var state: JSONObject
    private val log = mutableListOf<JSONObject>() // {entry, hash, seq, kind, serial, title_id, device, price, time}

    /** Bumped on every change so screens refresh. */
    val version = mutableIntStateOf(0)

    private val stateFile get() = File(app.filesDir, "shop-state.json")
    private val logFile get() = File(app.filesDir, "sales-log.json")
    val titlesDir: File
        get() = (app.getExternalFilesDirs(null).filterNotNull().maxByOrNull { it.freeSpace } ?: app.filesDir).let { File(it, "titles").apply { mkdirs() } }

    fun init(context: Context) {
        if (::app.isInitialized) return
        app = context.applicationContext
        store = SecretStore(app, "mesob-shop")
        state = if (stateFile.exists()) JSONObject(stateFile.readText()) else JSONObject()
        if (logFile.exists()) JSONArray(logFile.readText()).let { a -> for (i in 0 until a.length()) log += a.getJSONObject(i) }
        keys = ShopKeys(store)
        keys.ensure()
        if (!state.has("sign_public")) {
            state.put("sign_public", keys.signPublic()).put("kex_public", keys.kexPublic())
            persist()
        }
    }

    val hardwareKeys get() = keys.hardwareKex

    private fun persist() {
        val tmp = File(stateFile.path + ".tmp")
        tmp.writeText(state.toString())
        tmp.renameTo(stateFile)
        val tmpLog = File(logFile.path + ".tmp")
        tmpLog.writeText(JSONArray(log).toString())
        tmpLog.renameTo(logFile)
        version.intValue++
    }

    val isSetUp get() = state.has("shop_cert")
    val shopId get() = state.optLong("shop_id")
    val shopName get() = state.optString("shop_name", "Mesob shop")

    // ---------- Setup ----------

    fun setupRequest(): String = JSONObject()
        .put("sign_public", state.getString("sign_public"))
        .put("kex_public", state.getString("kex_public"))
        .put("model", android.os.Build.MANUFACTURER + " " + android.os.Build.MODEL)
        .put("android", android.os.Build.VERSION.SDK_INT)
        .put("kex_hardware", keys.hardwareKex)
        .put("attestation", JSONArray(keys.attestation()))
        .toString(2)

    /** Accept setup-shop-N.json from the studio if it was made for this tablet's keys. */
    @Synchronized
    fun finishSetup(json: String) {
        val s = JSONObject(json)
        Mesob.call("shop.check_setup", Mesob.obj(
            "studio_cert" to s.getString("studio_cert"), "shop_cert" to s.getString("shop_cert"),
            "sign_public" to state.getString("sign_public"), "kex_public" to state.getString("kex_public"),
        ))
        val shopId = s.getLong("shop_id")
        val sealed = s.getString("sealed_secret")
        val secret = Mesob.call("shop.open_secret", Mesob.obj("sealed" to sealed, "shop_id" to shopId, *keys.openFields(sealed)))
        store.saveB64("code", secret.getString("secret"))
        state.put("shop_id", shopId).put("shop_name", s.optString("shop_name")).put("studio_cert", s.getString("studio_cert")).put("shop_cert", s.getString("shop_cert"))
        persist()
    }

    // ---------- Catalog, credits ----------

    fun catalog(): List<JSONObject> {
        val a = state.optJSONObject("catalog")?.optJSONArray("titles") ?: return emptyList()
        return (0 until a.length()).map { a.getJSONObject(it) }.filter { File(titlesDir, it.getString("title_id") + ".msb").exists() }
    }

    fun title(id: String): JSONObject? = catalog().firstOrNull { it.getString("title_id") == id }

    private fun packs(): JSONArray = state.optJSONArray("packs") ?: JSONArray().also { state.put("packs", it) }

    fun packList(): List<JSONObject> = packs().let { a -> (0 until a.length()).map { a.getJSONObject(it) } }

    fun creditsLeft(): Long = packList().filter { it.optBoolean("unlocked") }.sumOf { it.getLong("serial_start") + it.getLong("serial_count") - it.getLong("next") }
    fun creditsLocked(): Long = packList().filter { !it.optBoolean("unlocked") }.sumOf { it.getLong("serial_count") }

    /** A credit unlock code typed in from the studio's SMS or phone call. */
    @Synchronized
    fun unlock(code: String): String {
        val r = Mesob.call("code.read_unlock", Mesob.obj("secret" to store.loadB64("code"), "shop_id" to shopId, "code" to code))
        val packId = r.getLong("pack_id")
        val p = packList().firstOrNull { it.getLong("pack_id") == packId }
            ?: throw IllegalStateException("Pack $packId hasn't arrived on a sync stick yet. Keep the code and enter it after the next sync.")
        if (p.optBoolean("unlocked")) return "Pack $packId was already unlocked."
        p.put("unlocked", true)
        persist()
        return "Pack $packId unlocked: ${p.getLong("serial_count")} licenses added."
    }

    // ---------- Selling ----------

    data class Sold(val titleId: String, val bundle: String, val serial: Long)

    private fun nextSerial(): Pair<JSONObject, Long> {
        val p = packList().firstOrNull { it.optBoolean("unlocked") && it.getLong("next") < it.getLong("serial_start") + it.getLong("serial_count") }
            ?: throw IllegalStateException("No credits left. Pay the studio and enter the unlock code they send.")
        return p to p.getLong("next")
    }

    /** Core prepares the license, the tablet's Keystore signs it, the core checks the whole chain. */
    private fun issue(pack: JSONObject, serial: Long, titleId: String, devicePublic: String, reissue: Boolean): String {
        val sealed = File(titlesDir, "$titleId.key").readText()
        val p = Mesob.call("shop.issue_prepare", Mesob.obj(
            "shop_cert" to state.getString("shop_cert"), "pack" to pack.getString("record"),
            "sealed_title_key" to sealed, "title_id" to titleId, "device_public" to devicePublic,
            "serial" to serial, "reissue" to reissue, *keys.openFields(sealed),
        ))
        return Mesob.call("shop.issue_finish", Mesob.obj(
            "body" to p.getString("body"), "sig" to keys.sign(p.getString("to_sign")),
            "shop_cert" to state.getString("shop_cert"), "studio_cert" to state.getString("studio_cert"),
            "pack" to pack.getString("record"), "capacity" to state.getJSONObject("capacity").getString(pack.getLong("block_id").toString()),
            "device_public" to devicePublic,
        )).getString("bundle")
    }

    private fun appendLog(kind: String, serial: Long, titleId: String, device: String, price: Long) {
        val prev = log.lastOrNull()?.getString("entry")
        val p = Mesob.call("log.prepare", Mesob.obj(
            "prev" to prev, "kind" to kind, "serial" to serial, "title_id" to titleId, "device" to device,
            "price" to price, "local_time" to System.currentTimeMillis() / 1000,
        ))
        val e = Mesob.call("log.finish", Mesob.obj("body" to p.getString("body"), "sig" to keys.sign(p.getString("to_sign")), "shop_cert" to state.getString("shop_cert")))
        log += JSONObject().put("entry", e.getString("entry")).put("hash", e.getString("hash")).put("seq", e.getLong("seq"))
            .put("kind", kind).put("serial", serial).put("title_id", titleId).put("device", device).put("price", price)
            .put("time", System.currentTimeMillis() / 1000)
    }

    /** Sell titles to one device after cash is taken. Each sale is logged before its license is returned. */
    @Synchronized
    fun sell(devicePublic: String, titleIds: List<String>): List<Sold> {
        val device = Mesob.call("request.parse", Mesob.obj("request" to requestB64(devicePublic, titleIds))).getString("device")
        return titleIds.map { id ->
            val t = title(id) ?: throw IllegalStateException("Title $id isn't in this shop.")
            val (pack, serial) = nextSerial()
            val bundle = issue(pack, serial, id, devicePublic, reissue = false)
            pack.put("next", serial + 1)
            appendLog("sale", serial, id, device, t.getLong("price"))
            persist()
            Sold(id, bundle, serial)
        }
    }

    /** Free replacement after a reset or new phone: same serial, new device, logged and limited. */
    @Synchronized
    fun reissue(devicePublic: String, serial: Long): Sold {
        val sale = log.firstOrNull { it.getString("kind") == "sale" && it.getLong("serial") == serial }
            ?: throw IllegalStateException("Serial $serial wasn't sold by this shop.")
        val count = log.count { it.getString("kind") == "reissue" && it.getLong("serial") == serial }
        if (count >= MAX_REISSUES) throw IllegalStateException("Serial $serial has already been replaced $count times.")
        val titleId = sale.getString("title_id")
        val pack = packList().first { serial >= it.getLong("serial_start") && serial < it.getLong("serial_start") + it.getLong("serial_count") }
        val device = Mesob.call("request.parse", Mesob.obj("request" to requestB64(devicePublic, listOf(titleId)))).getString("device")
        val bundle = issue(pack, serial, titleId, devicePublic, reissue = true)
        appendLog("reissue", serial, titleId, device, 0)
        persist()
        return Sold(titleId, bundle, serial)
    }

    private fun requestB64(devicePublic: String, titleIds: List<String>): String {
        val dev = Base64.decode(devicePublic, Base64.NO_WRAP)
        val b = java.io.ByteArrayOutputStream()
        b.write("MSR1".toByteArray()); b.write(dev); b.write(titleIds.size)
        titleIds.forEach { id -> b.write(id.chunked(2).map { it.toInt(16).toByte() }.toByteArray()) }
        return Base64.encodeToString(b.toByteArray(), Base64.NO_WRAP)
    }

    // ---------- Reports ----------

    private fun today(): LocalDate = LocalDate.now(ZoneId.systemDefault())
    private fun dayOf(epochSec: Long) = java.time.Instant.ofEpochSecond(epochSec).atZone(ZoneId.systemDefault()).toLocalDate()

    data class Today(val day: Int, val count: Int, val cash: Long, val code: String)

    fun todayReport(): Today {
        val d = today()
        val sales = log.filter { it.getString("kind") == "sale" && dayOf(it.getLong("time")) == d }
        val dayNo = (d.toEpochDay() - LocalDate.of(2026, 1, 1).toEpochDay()).toInt().coerceIn(0, 65535)
        val tag = log.lastOrNull()?.getString("hash")?.take(6) ?: "000000"
        val code = Mesob.call("code.report", Mesob.obj(
            "secret" to store.loadB64("code"), "shop_id" to shopId, "day" to dayNo,
            "count" to sales.size.coerceAtMost(65535), "cash" to sales.sumOf { it.getLong("price") }, "log_tag" to tag,
        )).getString("code")
        return Today(dayNo, sales.size, sales.sumOf { it.getLong("price") }, code)
    }

    fun recentSales(n: Int = 30): List<JSONObject> = log.takeLast(n).reversed()

    // ---------- Sync stick ----------

    /** Import studio material: verified certificates, credit packs, catalog, titles and their sealed keys. */
    @Synchronized
    fun importSync(read: (String) -> ByteArray?, list: (String) -> List<String>, copyTitle: (String, File) -> Unit): String {
        val studioCert = read("studio.cert")?.let { Base64.encodeToString(it, Base64.NO_WRAP) } ?: throw IllegalStateException("No studio.cert on the stick.")
        if (studioCert != state.getString("studio_cert")) throw IllegalStateException("This stick is from a different studio.")
        val caps = state.optJSONObject("capacity") ?: JSONObject().also { state.put("capacity", it) }
        list("capacity").forEach { name -> read("capacity/$name")?.let { caps.put(name.removeSuffix(".bin"), Base64.encodeToString(it, Base64.NO_WRAP)) } }
        var newPacks = 0
        list("packs").forEach { name ->
            val rec = Base64.encodeToString(read("packs/$name") ?: return@forEach, Base64.NO_WRAP)
            val blockGuess = Mesob.call("record.decode", Mesob.obj("record" to rec)).getLong("block_id")
            val p = Mesob.call("shop.check_pack", Mesob.obj("studio_cert" to studioCert, "capacity" to caps.getString(blockGuess.toString()), "pack" to rec))
            if (p.getLong("shop_id") != shopId) return@forEach
            if (packList().none { it.getLong("pack_id") == p.getLong("pack_id") }) {
                packs().put(JSONObject().put("pack_id", p.getLong("pack_id")).put("record", rec).put("block_id", p.getLong("block_id"))
                    .put("serial_start", p.getLong("serial_start")).put("serial_count", p.getLong("serial_count"))
                    .put("next", p.getLong("serial_start")).put("unlocked", false))
                newPacks++
            }
        }
        val catalog = read("catalog.json") ?: throw IllegalStateException("No catalog on the stick.")
        val sig = read("catalog.sig") ?: throw IllegalStateException("The catalog isn't signed.")
        Mesob.call("catalog.verify", Mesob.obj("studio_cert" to studioCert, "data" to Base64.encodeToString(catalog, Base64.NO_WRAP), "sig" to Base64.encodeToString(sig, Base64.NO_WRAP)))
        val cat = JSONObject(String(catalog, Charsets.UTF_8))
        var newTitles = 0
        val titles = cat.getJSONArray("titles")
        for (i in 0 until titles.length()) {
            val id = titles.getJSONObject(i).getString("title_id")
            val dest = File(titlesDir, "$id.msb")
            if (!dest.exists() || dest.length() == 0L) {
                val tmp = File(titlesDir, "$id.part")
                copyTitle("titles/$id.msb", tmp)
                val h = Mesob.call("title.inspect", Mesob.obj("path" to tmp.path, "check" to true))
                if (h.getString("title_id") != id) throw IllegalStateException("Title file $id doesn't match its name.")
                tmp.renameTo(dest)
                newTitles++
            }
            read("keys/$id.sealed")?.let { File(titlesDir, "$id.key").writeText(Base64.encodeToString(it, Base64.NO_WRAP)) }
        }
        state.put("catalog", cat)
        persist()
        return "Sync done: ${titles.length()} titles ($newTitles new), $newPacks new credit pack(s) (locked until you enter their codes)."
    }

    /** The whole signed log, for the studio. */
    fun exportLog(): String = JSONObject()
        .put("shop_id", shopId)
        .put("entries", JSONArray(log.map { it.getString("entry") }))
        .toString()

    // ---------- Orders from phones over Wi-Fi (cart → pickup code → cashier) ----------

    data class Order(
        val id: Long,
        /** Shown on the phone and said at the counter, e.g. "K472". */
        val code: String,
        /** The phone's private handle for polling; never shown. */
        val token: String,
        val devicePublic: String,
        val titleIds: List<String>,
        val total: Long,
        val created: Long = System.currentTimeMillis(),
        var status: String = "pending",
        var licenses: List<Sold> = emptyList(),
    )

    val orders = linkedMapOf<Long, Order>()
    private var nextOrder = 1L
    private val random = java.security.SecureRandom()
    /** Said aloud at the counter, so no I or O (they read as 1 and 0). */
    private const val CODE_LETTERS = "ABCDEFGHJKLMNPQRSTUVWXYZ"

    private fun newCode(): String {
        val taken = orders.values.map { it.code }.toHashSet()
        repeat(100) {
            val c = "${CODE_LETTERS[random.nextInt(CODE_LETTERS.length)]}${100 + random.nextInt(900)}"
            if (c !in taken) return c
        }
        return "${CODE_LETTERS[random.nextInt(CODE_LETTERS.length)]}${1000 + random.nextInt(9000)}"
    }

    private fun newToken(): String = ByteArray(24).also { random.nextBytes(it) }.joinToString("") { "%02x".format(it) }

    /** What the cashier typed: "k 472", "K-472" → "K472". */
    fun normalizeCode(typed: String): String = typed.uppercase().filter { it.isLetterOrDigit() }.let {
        if (it.isEmpty()) it else it[0] + it.drop(1).replace('O', '0').replace('I', '1')
    }

    @Synchronized
    fun newOrder(devicePublic: String, titleIds: List<String>, replaces: String? = null): Order {
        val ids = titleIds.distinct()
        require(ids.isNotEmpty() && ids.size <= 20) { "1 to 20 titles per order" }
        // The customer edited the cart and ordered again: drop the old waiting order. Only its private
        // token can do that, so nobody on the Wi-Fi can cancel someone else's order.
        if (replaces != null) orders.values.firstOrNull { it.token == replaces && it.status == "pending" }?.status = "replaced"
        // Anyone on the shop Wi-Fi can post orders; keep the queue from being flooded.
        if (orders.values.count { it.status == "pending" } >= 30) throw IllegalStateException("Too many open orders. Ask at the counter.")
        while (orders.size >= 200) orders.remove(orders.entries.first { it.value.status != "pending" }.key)
        val total = ids.sumOf { (title(it) ?: throw IllegalArgumentException("unknown title $it")).getLong("price") }
        val o = Order(nextOrder++, newCode(), newToken(), devicePublic, ids, total)
        orders[o.id] = o
        version.intValue++
        return o
    }

    fun byToken(token: String): Order? = synchronized(this) { orders.values.firstOrNull { it.token == token } }

    fun byCode(typed: String): Order? = normalizeCode(typed).let { c -> synchronized(this) { orders.values.lastOrNull { it.code == c } } }

    @Synchronized
    fun markPaid(id: Long) {
        val o = orders[id] ?: return
        if (o.status != "pending") return
        o.licenses = sell(o.devicePublic, o.titleIds)
        o.status = "paid"
        version.intValue++
    }

    @Synchronized
    fun cancel(id: Long) {
        orders[id]?.let { if (it.status == "pending") it.status = "cancelled" }
        version.intValue++
    }
}
