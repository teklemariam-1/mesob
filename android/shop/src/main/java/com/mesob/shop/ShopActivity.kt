package com.mesob.shop

import android.graphics.Bitmap
import android.net.Uri
import android.os.Bundle
import android.util.Base64
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.mesob.core.Mesob
import com.mesob.core.ShopProtocol
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream

class ShopActivity : ComponentActivity() {
    private val server = ShopServer()
    private var startupError: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        try {
            ShopState.init(this)
            server.start(5000, false)
        } catch (e: Exception) {
            startupError = e.message ?: e.toString()
        }
        setContent { MaterialTheme(colorScheme = lightColorScheme(primary = Color(0xFF2747C9))) { ShopApp(startupError) } }
    }

    override fun onDestroy() {
        server.stop()
        super.onDestroy()
    }
}

private fun qr(text: String, size: Int = 900): Bitmap {
    val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, mapOf(EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.L, EncodeHintType.MARGIN to 2))
    val px = IntArray(m.width * m.height) { i -> if (m.get(i % m.width, i / m.width)) 0xFF000000.toInt() else 0xFFFFFFFF.toInt() }
    return Bitmap.createBitmap(px, m.width, m.height, Bitmap.Config.ARGB_8888)
}

private enum class View { Home, SellQr, Usb, Credits, Report, Sync }

@Composable
private fun ShopApp(startupError: String?) {
    if (startupError != null) {
        Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) { Text("Mesob Shop can't start: $startupError") }
        return
    }
    @Suppress("UNUSED_VARIABLE") val v = ShopState.version.intValue
    if (!ShopState.isSetUp) { SetupScreen(); return }
    var view by remember { mutableStateOf(View.Home) }
    BackHandler(enabled = view != View.Home) { view = View.Home }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().background(Color(0xFF0A1628)).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("${ShopState.shopName} · shop ${ShopState.shopId}", color = Color.White, fontWeight = FontWeight.SemiBold, fontSize = 20.sp)
                Text("${ShopState.creditsLeft()} credits left${if (ShopState.creditsLocked() > 0) " · ${ShopState.creditsLocked()} locked" else ""}", color = Color(0xFFE8B84A))
            }
            if (view != View.Home) TextButton(onClick = { view = View.Home }) { Text("Back", color = Color.White) }
        }
        when (view) {
            View.Home -> HomeScreen { view = it }
            View.SellQr -> SellQrScreen()
            View.Usb -> UsbScreen()
            View.Credits -> CreditsScreen()
            View.Report -> ReportScreen()
            View.Sync -> SyncScreen()
        }
    }
}

@Composable
private fun SetupScreen() {
    val ctx = LocalContext.current
    var status by remember { mutableStateOf("") }
    val saveReq = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
        uri ?: return@rememberLauncherForActivityResult
        status = try {
            val s = Stick(ctx, uri)
            s.write(s.mesob, "setup-request.json", ShopState.setupRequest().toByteArray(), "application/json")
            "Saved MESOB/setup-request.json. Take the stick to the studio."
        } catch (e: Exception) { "Not saved: ${e.message}" }
    }
    val finish = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
        uri ?: return@rememberLauncherForActivityResult
        status = try {
            val s = Stick(ctx, uri)
            val files = s.mesob.listFiles().filter { it.name?.startsWith("setup-shop-") == true }
            var done = false
            for (f in files) if (runCatching { ShopState.finishSetup(String(s.read(f))) }.isSuccess) { done = true; break }
            if (done) "Setup finished." else "No setup on this stick was made for this tablet."
        } catch (e: Exception) { "Setup failed: ${e.message}" }
    }
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text("Set up this shop", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        Text("1. Plug in a USB stick and save this tablet's setup request. Take it to the studio.")
        Button(onClick = { saveReq.launch(null) }) { Text("Save setup request to stick") }
        Text("2. When the stick comes back from the studio, finish setup.")
        Button(onClick = { finish.launch(null) }) { Text("Finish setup from stick") }
        if (status.isNotEmpty()) Text(status)
    }
}

@Composable
private fun HomeScreen(go: (View) -> Unit) {
    val scope = rememberCoroutineScope()
    @Suppress("UNUSED_VARIABLE") val v = ShopState.version.intValue
    val address = ShopServer.localAddress()
    var error by remember { mutableStateOf<String?>(null) }
    var typed by remember { mutableStateOf("") }
    var confirm by remember { mutableStateOf<ShopState.Order?>(null) }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) { while (true) { kotlinx.coroutines.delay(20_000); now = System.currentTimeMillis() } }

    Row(Modifier.fillMaxSize().padding(16.dp), horizontalArrangement = Arrangement.spacedBy(24.dp)) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            val all = ShopState.orders.values.filter { it.status != "replaced" }.reversed()
            val pending = all.filter { it.status == "pending" }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Orders", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                if (pending.isNotEmpty()) Badge(containerColor = Color(0xFFE8A33D), contentColor = Color.Black) {
                    Text("${pending.size} waiting", Modifier.padding(horizontal = 6.dp, vertical = 2.dp))
                }
            }
            OutlinedTextField(
                typed, { typed = it.uppercase().take(6) },
                label = { Text("Customer's order code") }, placeholder = { Text("e.g. K472") }, singleLine = true,
                textStyle = TextStyle(fontFamily = FontFamily.Monospace, fontSize = 24.sp, fontWeight = FontWeight.SemiBold),
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters, imeAction = ImeAction.Search),
                trailingIcon = { if (typed.isNotEmpty()) TextButton(onClick = { typed = "" }) { Text("Clear") } },
                modifier = Modifier.fillMaxWidth(),
            )
            val code = ShopState.normalizeCode(typed)
            val shown = if (code.isEmpty()) pending else all.filter { it.code.startsWith(code) }
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            if (shown.isEmpty()) Text(
                if (code.isEmpty()) "No orders waiting. Customers join the shop Wi-Fi, scan the QR code on the right, fill a cart in the Mesob app and get a code like K472 to show you."
                else "No order $code. Check the code on the customer's phone.",
                color = Color(0xFF5A6478),
            )
            LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                items(shown, key = { it.id }) { o ->
                    OrderCard(o, highlight = code.length >= 4 && o.code == code, now = now, onPaid = { confirm = o }, onCancel = { ShopState.cancel(o.id) })
                }
                val done = if (code.isEmpty()) all.filter { it.status != "pending" }.take(6) else emptyList()
                if (done.isNotEmpty()) {
                    item { Text("Recent", style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp)) }
                    items(done, key = { "d${it.id}" }) { o ->
                        Row(Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                            Text(o.code, fontFamily = FontFamily.Monospace, fontWeight = FontWeight.SemiBold, modifier = Modifier.width(72.dp))
                            Text("${o.titleIds.size} title(s) · ${o.total} Nakfa", Modifier.weight(1f))
                            Text(if (o.status == "paid") "Paid" else "Cancelled", color = if (o.status == "paid") Color(0xFF1E7B45) else Color(0xFF8A6D3B))
                        }
                    }
                }
            }
        }
        Column(Modifier.width(320.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            if (address != null) {
                val text = "${ShopProtocol.QR_PREFIX}$address:${ShopProtocol.PORT}"
                Text("Scan to shop", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Image(remember(text) { qr(text, 600).asImageBitmap() }, "Shop address", Modifier.size(240.dp))
                Text("$address", fontFamily = FontFamily.Monospace, fontSize = 22.sp)
            } else Text("Turn on Wi-Fi (connect the tablet to the shop router).")
            Button(onClick = { go(View.SellQr) }, Modifier.fillMaxWidth()) { Text("Sell by QR code") }
            OutlinedButton(onClick = { go(View.Usb) }, Modifier.fillMaxWidth()) { Text("USB request (PC customers)") }
            OutlinedButton(onClick = { go(View.Credits) }, Modifier.fillMaxWidth()) { Text("Enter credit code") }
            OutlinedButton(onClick = { go(View.Report) }, Modifier.fillMaxWidth()) { Text("Today's report code") }
            OutlinedButton(onClick = { go(View.Sync) }, Modifier.fillMaxWidth()) { Text("Sync stick") }
        }
    }

    confirm?.let { o ->
        AlertDialog(
            onDismissRequest = { confirm = null },
            title = { Text("Take ${o.total} Nakfa for order ${o.code}?") },
            text = { Text("This signs ${o.titleIds.size} license(s) for the customer's phone and uses ${o.titleIds.size} of your credits. Tap Paid only once the cash is in your hand.") },
            confirmButton = {
                Button(onClick = {
                    confirm = null
                    scope.launch {
                        error = withContext(Dispatchers.IO) { runCatching { ShopState.markPaid(o.id) }.exceptionOrNull()?.message }
                        if (error == null) typed = ""
                    }
                }) { Text("Paid") }
            },
            dismissButton = { TextButton(onClick = { confirm = null }) { Text("Not yet") } },
        )
    }
}

@Composable
private fun OrderCard(o: ShopState.Order, highlight: Boolean, now: Long, onPaid: () -> Unit, onCancel: () -> Unit) {
    Card(
        colors = CardDefaults.cardColors(containerColor = if (highlight) Color(0xFFFFF1D6) else Color(0xFFF3F5FA)),
        border = if (highlight) androidx.compose.foundation.BorderStroke(2.dp, Color(0xFFE8A33D)) else null,
    ) {
        Column(Modifier.fillMaxWidth().padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(o.code, fontFamily = FontFamily.Monospace, fontSize = 30.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.width(14.dp))
                Column(Modifier.weight(1f)) {
                    Text("${o.total} Nakfa", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                    Text("${o.titleIds.size} title(s) · ${ago(now - o.created)}", style = MaterialTheme.typography.bodySmall)
                }
                Text(
                    when (o.status) { "pending" -> "Waiting"; "paid" -> "Paid"; else -> "Cancelled" },
                    color = when (o.status) { "pending" -> Color(0xFF8A5A00); "paid" -> Color(0xFF1E7B45); else -> Color(0xFF6B7280) },
                    fontWeight = FontWeight.SemiBold,
                )
            }
            o.titleIds.forEach { id ->
                val t = ShopState.title(id)
                Row {
                    Text(t?.optString("title_en") ?: id, Modifier.weight(1f), maxLines = 1)
                    Text("${t?.optLong("price") ?: 0}", color = Color(0xFF5A6478))
                }
            }
            if (o.status == "pending") Row(horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 4.dp)) {
                Button(onClick = onPaid, modifier = Modifier.weight(1f)) { Text("Paid · ${o.total} Nakfa") }
                OutlinedButton(onClick = onCancel) { Text("Cancel") }
            }
        }
    }
}

private fun ago(ms: Long): String = when {
    ms < 60_000 -> "just now"
    ms < 3_600_000 -> "${ms / 60_000} min ago"
    else -> "${ms / 3_600_000} h ago"
}

@Composable
private fun SellQrScreen() {
    val scope = rememberCoroutineScope()
    var request by remember { mutableStateOf<org.json.JSONObject?>(null) }
    var reissueSerial by remember { mutableStateOf("") }
    var sold by remember { mutableStateOf<List<ShopState.Sold>>(emptyList()) }
    var shown by remember { mutableIntStateOf(0) }
    var status by remember { mutableStateOf("Scan the request code on the customer's phone.") }
    val scan = rememberLauncherForActivityResult(ScanContract()) { r ->
        val text = r.contents ?: return@rememberLauncherForActivityResult
        request = try { Mesob.call("request.parse", Mesob.obj("request" to text.trim())) } catch (e: Exception) { status = "That isn't a Mesob request code."; null }
        sold = emptyList(); shown = 0
    }
    Column(Modifier.fillMaxSize().padding(16.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Button(onClick = { scan.launch(ScanOptions().setPrompt("Scan the customer's request code").setBeepEnabled(false)) }) { Text("Scan customer's code") }
        Text(status)
        val req = request
        if (req != null && sold.isEmpty()) {
            val ids = req.getJSONArray("title_ids").let { a -> (0 until a.length()).map { a.getString(it) } }
            val titles = ids.map { ShopState.title(it) }
            titles.forEachIndexed { i, t -> Text(if (t == null) "Unknown title ${ids[i]} (not in this shop)" else "${t.getString("title_en")} · ${t.getLong("price")} Nakfa") }
            val total = titles.filterNotNull().sumOf { it.getLong("price") }
            Text("Device ${req.getString("device").take(8).uppercase()}", fontFamily = FontFamily.Monospace)
            Button(enabled = titles.all { it != null }, onClick = {
                scope.launch {
                    try {
                        sold = withContext(Dispatchers.IO) { ShopState.sell(req.getString("device_public"), ids) }
                        status = "Sold. Let the customer scan each license code."
                    } catch (e: Exception) { status = e.message ?: "Not sold." }
                }
            }) { Text("Cash received: $total Nakfa") }
            HorizontalDivider()
            Text("Replacement for a lost or reset phone (free)", fontWeight = FontWeight.SemiBold)
            OutlinedTextField(reissueSerial, { reissueSerial = it.filter(Char::isDigit) }, label = { Text("Serial number from the receipt") })
            OutlinedButton(enabled = reissueSerial.isNotEmpty(), onClick = {
                scope.launch {
                    try {
                        sold = listOf(withContext(Dispatchers.IO) { ShopState.reissue(req.getString("device_public"), reissueSerial.toLong()) })
                        status = "Replacement issued. Let the customer scan the code."
                    } catch (e: Exception) { status = e.message ?: "No replacement." }
                }
            }) { Text("Issue replacement") }
        }
        if (sold.isNotEmpty()) {
            val s = sold[shown]
            Text("License ${shown + 1} of ${sold.size} · ${ShopState.title(s.titleId)?.getString("title_en")} · serial ${s.serial}", fontWeight = FontWeight.SemiBold)
            Image(remember(s.bundle) { qr(s.bundle).asImageBitmap() }, "License code", Modifier.fillMaxWidth().aspectRatio(1f).background(Color.White))
            Text("Receipt: serial ${s.serial}. The customer keeps this number for a free replacement.")
            if (shown < sold.size - 1) Button(onClick = { shown++ }) { Text("Next license") }
        }
    }
}

@Composable
private fun UsbScreen() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf("Plug in the customer's USB stick. Their Mesob Player saved a request file (.msr) on it.") }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
        uri ?: return@rememberLauncherForActivityResult
        scope.launch {
            status = withContext(Dispatchers.IO) {
                try {
                    val root = androidx.documentfile.provider.DocumentFile.fromTreeUri(ctx, uri)!!
                    val req = root.listFiles().firstOrNull { it.name?.endsWith(".msr") == true } ?: return@withContext "No request file (.msr) at the top of this stick."
                    val bytes = ctx.contentResolver.openInputStream(req.uri)!!.use { it.readBytes() }
                    val r = Mesob.call("request.parse", Mesob.obj("request" to Base64.encodeToString(bytes, Base64.NO_WRAP)))
                    val ids = r.getJSONArray("title_ids").let { a -> (0 until a.length()).map { a.getString(it) } }
                    val sold = ShopState.sell(r.getString("device_public"), ids)
                    val out = ByteArrayOutputStream()
                    sold.forEach { s ->
                        val b = Base64.decode(s.bundle, Base64.NO_WRAP)
                        out.write(b.size shr 8); out.write(b.size and 0xff); out.write(b)
                    }
                    root.findFile("mesob-licenses.msls")?.delete()
                    val lf = root.createFile("application/octet-stream", "mesob-licenses.msls")!!
                    ctx.contentResolver.openOutputStream(lf.uri, "w")!!.use { it.write(out.toByteArray()) }
                    sold.forEach { s ->
                        val f = java.io.File(ShopState.titlesDir, "${s.titleId}.msb")
                        if (root.findFile("${s.titleId}.msb") == null) {
                            val tf = root.createFile("application/octet-stream", "${s.titleId}.msb")!!
                            ctx.contentResolver.openOutputStream(tf.uri, "w")!!.use { o -> f.inputStream().use { it.copyTo(o, 1 shl 16) } }
                        }
                    }
                    "Sold ${sold.size} title(s), serials ${sold.joinToString { it.serial.toString() }}. Licenses and files are on the stick: the customer adds them in Mesob Player."
                } catch (e: Exception) { "Not sold: ${e.message}" }
            }
        }
    }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Take payment first, then:", fontWeight = FontWeight.SemiBold)
        Button(onClick = { pick.launch(null) }) { Text("Choose customer's stick and sell") }
        Text(status)
    }
}

@Composable
private fun CreditsScreen() {
    var code by remember { mutableStateOf("") }
    var status by remember { mutableStateOf("Pay the studio, then type the code they send by SMS or read over the phone.") }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        OutlinedTextField(code, { code = it.uppercase() }, label = { Text("Credit code") }, textStyle = LocalTextStyle.current.copy(fontFamily = FontFamily.Monospace, fontSize = 22.sp), modifier = Modifier.fillMaxWidth())
        Button(onClick = { status = try { ShopState.unlock(code).also { code = "" } } catch (e: Exception) { "Not accepted: ${e.message}" } }) { Text("Unlock credits") }
        Text(status)
        HorizontalDivider()
        ShopState.packList().forEach { p -> Text("Pack ${p.getLong("pack_id")}: ${p.getLong("serial_count")} licenses, ${p.getLong("serial_start") + p.getLong("serial_count") - p.getLong("next")} left · ${if (p.optBoolean("unlocked")) "unlocked" else "locked"}") }
    }
}

@Composable
private fun ReportScreen() {
    val r = remember { runCatching { ShopState.todayReport() } }
    Column(Modifier.fillMaxSize().padding(16.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        r.onSuccess { t ->
            Text("Today: ${t.count} sales · ${t.cash} Nakfa", style = MaterialTheme.typography.titleLarge)
            Text("Send this code to the studio by SMS or read it over the phone:")
            Text(t.code, fontFamily = FontFamily.Monospace, fontSize = 30.sp, fontWeight = FontWeight.SemiBold)
        }.onFailure { Text("No report: ${it.message}") }
        HorizontalDivider()
        Text("Recent sales", fontWeight = FontWeight.SemiBold)
        ShopState.recentSales().forEach { s -> Text("#${s.getLong("seq")} · ${s.getString("kind")} · serial ${s.getLong("serial")} · ${ShopState.title(s.getString("title_id"))?.getString("title_en") ?: s.getString("title_id")} · ${s.getLong("price")} Nakfa") }
    }
}

@Composable
private fun SyncScreen() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf("The sync stick from the studio brings titles and credits; it takes your sales log back.") }
    var busy by remember { mutableStateOf(false) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri: Uri? ->
        uri ?: return@rememberLauncherForActivityResult
        busy = true
        scope.launch {
            status = withContext(Dispatchers.IO) {
                try {
                    val s = Stick(ctx, uri)
                    val lines = mutableListOf<String>()
                    val incoming = s.dir(s.mesob, "to-shop-${ShopState.shopId}")
                    if (incoming != null) {
                        lines += ShopState.importSync(
                            read = { path -> s.file(incoming, path)?.let { s.read(it) } },
                            list = { dir -> s.dir(incoming, dir)?.listFiles()?.mapNotNull { it.name } ?: emptyList() },
                            copyTitle = { path, dest -> s.copyOut(s.file(incoming, path) ?: throw IllegalStateException("$path missing on stick"), dest) },
                        )
                    } else lines += "Nothing from the studio for shop ${ShopState.shopId} on this stick."
                    val out = s.dir(s.mesob, "from-shop-${ShopState.shopId}", create = true)!!
                    s.write(out, "log.json", ShopState.exportLog().toByteArray(), "application/json")
                    lines += "Sales log written for the studio."
                    lines.joinToString("\n")
                } catch (e: Exception) { "Sync failed: ${e.message}" }
            }
            busy = false
        }
    }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Button(enabled = !busy, onClick = { pick.launch(null) }) { Text("Choose sync stick") }
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        Text(status)
    }
}
