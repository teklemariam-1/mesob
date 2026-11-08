package com.mesob.player

import android.graphics.BitmapFactory
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

private fun kindLabel(kind: String) = when (kind) { "audio" -> "Music"; "pdf" -> "Book"; else -> "Video" }
private fun kindMark(kind: String) = when (kind) { "audio" -> "♪"; "pdf" -> "BOOK"; else -> "▶" }

/**
 * The in-shop store, Kindle style: search and browse the shop's catalog, add to cart, place the
 * order, show the pickup code at the counter, and the titles land in the library once paid.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun StoreScreen(openLibrary: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    remember { Cart.load(); true }
    var address by rememberSaveable { mutableStateOf(Cart.lastAddress) }
    var catalog by remember { mutableStateOf<ShopClient.Catalog?>(null) }
    var connecting by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var query by rememberSaveable { mutableStateOf("") }
    var kind by rememberSaveable { mutableStateOf("all") }
    var showCart by remember { mutableStateOf(false) }
    var placing by remember { mutableStateOf(false) }
    var cartError by remember { mutableStateOf<String?>(null) }
    val posters = remember { mutableStateMapOf<String, ImageBitmap>() }

    fun connect(addr: String) {
        if (addr.isBlank()) return
        scope.launch {
            connecting = true
            message = null
            try {
                val c = withContext(Dispatchers.IO) { ShopClient.bindToWifi(ctx); ShopClient(ctx, addr).catalog() }
                catalog = c
                address = addr
                val owned = PlayerState.library.filter { !it.locked }.map { it.titleId }.toSet()
                val dropped = Cart.atShop(addr, c.shopName, c.titles.map { it.id }.filter { it !in owned }.toSet())
                if (dropped > 0) message = "$dropped title(s) were taken out of your cart: already yours, or not sold at this shop."
                c.titles.forEach { t ->
                    if (t.id !in posters) launch(Dispatchers.IO) {
                        runCatching { ShopClient(ctx, addr).poster(t.id) }.getOrNull()
                            ?.let { b -> BitmapFactory.decodeByteArray(b, 0, b.size)?.asImageBitmap() }
                            ?.let { posters[t.id] = it }
                    }
                }
            } catch (e: Exception) {
                message = "Can't reach the shop. Check you're on the shop's Wi-Fi and try again. (${e.message})"
            } finally { connecting = false }
        }
    }
    val scan = rememberLauncherForActivityResult(ScanContract()) { r -> r.contents?.let { connect(it.trim()) } }

    // An order waiting at the counter takes over the screen until it's paid or cancelled.
    Cart.pending?.let { p -> OrderScreen(p, posters, openLibrary); return }

    val c = catalog
    if (c == null) {
        ConnectScreen(
            address = address, onAddress = { address = it }, connecting = connecting, message = message,
            onConnect = { connect(address) },
            onScan = { scan.launch(ScanOptions().setPrompt("Scan the QR code on the shop's screen").setBeepEnabled(false)) },
        )
        return
    }

    fun place() {
        scope.launch {
            placing = true
            cartError = null
            try {
                val o = withContext(Dispatchers.IO) { ShopClient(ctx, address).order(Cart.lines.map { it.id }, Cart.replaces) }
                Cart.placed(Cart.Pending(address, c.shopName, o.getString("code"), o.getString("token"), o.getLong("total"), Cart.lines.toList()))
                showCart = false
            } catch (e: Exception) {
                cartError = "The order didn't go through: ${e.message}. Stay on the shop's Wi-Fi and try again."
            } finally { placing = false }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(c.shopName, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text("${c.titles.size} titles · pay cash at the counter", style = MaterialTheme.typography.bodySmall)
                    }
                },
                actions = {
                    TextButton(onClick = { catalog = null }) { Text("Leave") }
                    BadgedBox(badge = { if (Cart.lines.isNotEmpty()) Badge { Text("${Cart.lines.size}") } }, modifier = Modifier.padding(end = 12.dp)) {
                        IconButton(onClick = { showCart = true }) { Text("🛒", fontSize = 22.sp) }
                    }
                },
            )
        },
        bottomBar = {
            if (Cart.lines.isNotEmpty()) Surface(tonalElevation = 3.dp) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("${Cart.lines.size} in cart", style = MaterialTheme.typography.bodySmall)
                        Text("${Cart.total} Nakfa", fontWeight = FontWeight.Bold, fontSize = 18.sp)
                    }
                    Button(onClick = { showCart = true }) { Text("View cart") }
                }
            }
        },
    ) { pad ->
        Column(Modifier.padding(pad).padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedTextField(
                query, { query = it }, singleLine = true, shape = RoundedCornerShape(28.dp),
                placeholder = { Text("Search titles, artists, ርእሲ…") }, leadingIcon = { Text("⌕", fontSize = 20.sp) },
                trailingIcon = { if (query.isNotEmpty()) TextButton(onClick = { query = "" }) { Text("Clear") } },
                modifier = Modifier.fillMaxWidth(),
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf("all" to "All", "video" to "Video", "audio" to "Music", "pdf" to "Books").forEach { (k, label) ->
                    FilterChip(selected = kind == k, onClick = { kind = k }, label = { Text(label) })
                }
            }
            message?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.tertiary) }
            val q = query.trim()
            val shown = c.titles.filter { t ->
                (kind == "all" || t.kind == kind) && (q.isEmpty() || listOf(t.en, t.ti, t.artist).any { it.contains(q, ignoreCase = true) })
            }
            if (shown.isEmpty()) Text(if (q.isEmpty()) "Nothing here yet." else "Nothing matches “$q”.", modifier = Modifier.padding(top = 24.dp))
            LazyVerticalGrid(
                GridCells.Adaptive(160.dp), contentPadding = PaddingValues(bottom = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(18.dp),
            ) {
                items(shown, key = { it.id }) { t ->
                    StoreCard(t, posters[t.id], owned = PlayerState.library.any { it.titleId == t.id && !it.locked }, inCart = t.id in Cart) { Cart.toggle(t) }
                }
            }
        }
    }

    if (showCart) ModalBottomSheet(onDismissRequest = { showCart = false }) {
        CartSheet(c.shopName, posters, placing, cartError, onPlace = { place() })
    }
}

@Composable
private fun PosterBox(kind: String, poster: ImageBitmap?, modifier: Modifier) {
    Box(modifier.clip(RoundedCornerShape(10.dp)).background(Color(0xFF1C2A3E)), contentAlignment = Alignment.Center) {
        if (poster != null) Image(poster, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        else Text(kindMark(kind), color = Color.White.copy(alpha = 0.7f), fontSize = 22.sp)
    }
}

@Composable
private fun StoreCard(t: ShopClient.Title, poster: ImageBitmap?, owned: Boolean, inCart: Boolean, onToggle: () -> Unit) {
    Column {
        PosterBox(t.kind, poster, Modifier.fillMaxWidth().aspectRatio(16f / 9f))
        Text(t.en, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 6.dp))
        if (t.ti.isNotBlank()) Text(t.ti, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(
            listOf(kindLabel(t.kind), t.artist).filter { it.isNotBlank() }.joinToString(" · "),
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis,
        )
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 6.dp)) {
            Text("${t.price} Nfa", fontWeight = FontWeight.Bold, modifier = Modifier.weight(1f))
            val pad = PaddingValues(horizontal = 14.dp, vertical = 4.dp)
            when {
                owned -> Text("Owned", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                inCart -> FilledTonalButton(onClick = onToggle, contentPadding = pad) { Text("✓ In cart") }
                else -> Button(onClick = onToggle, contentPadding = pad) { Text("Add") }
            }
        }
    }
}

@Composable
private fun CartSheet(shopName: String, posters: Map<String, ImageBitmap>, placing: Boolean, error: String?, onPlace: () -> Unit) {
    Column(Modifier.fillMaxWidth().padding(start = 20.dp, end = 20.dp, bottom = 28.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Your cart", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
        if (Cart.lines.isEmpty()) {
            Text("Your cart is empty. Tap Add on anything you like.")
            return@Column
        }
        Text("${Cart.lines.size} title(s) from $shopName", style = MaterialTheme.typography.bodySmall)
        Column(Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Cart.lines.toList().forEach { l ->
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    PosterBox(l.kind, posters[l.id], Modifier.width(80.dp).aspectRatio(16f / 9f))
                    Column(Modifier.weight(1f)) {
                        Text(l.en, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(kindLabel(l.kind), style = MaterialTheme.typography.bodySmall)
                    }
                    Text("${l.price}", fontWeight = FontWeight.SemiBold)
                    TextButton(onClick = { Cart.remove(l.id) }) { Text("Remove") }
                }
            }
        }
        HorizontalDivider()
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Total", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
            Text("${Cart.total} Nakfa", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
        }
        Text(
            "You'll get a short code. Show it at the counter and pay cash. Your titles then download to this phone and play only here.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        Button(onClick = onPlace, enabled = !placing, modifier = Modifier.fillMaxWidth().height(52.dp)) {
            Text(if (placing) "Placing order…" else "Place order · ${Cart.total} Nakfa", fontSize = 16.sp)
        }
    }
}

@Composable
private fun ConnectScreen(address: String, onAddress: (String) -> Unit, connecting: Boolean, message: String?, onConnect: () -> Unit, onScan: () -> Unit) {
    Column(Modifier.fillMaxSize().padding(20.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Text("Shop", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        Card {
            Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Buy at a Mesob shop", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text("1  Join the shop's Wi-Fi. No internet is needed.")
                Text("2  Scan the QR code on the shop's screen.")
                Text("3  Fill your cart, place the order, and pay cash at the counter with your code.")
                Button(onClick = onScan, enabled = !connecting, modifier = Modifier.fillMaxWidth().height(52.dp)) { Text("Scan shop QR code") }
            }
        }
        if (Cart.lastShopName.isNotBlank() && Cart.lastAddress.isNotBlank()) {
            OutlinedButton(onClick = { onAddress(Cart.lastAddress); onConnect() }, enabled = !connecting, modifier = Modifier.fillMaxWidth()) {
                Text("Back to ${Cart.lastShopName}")
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(address, onAddress, label = { Text("Or type the shop address") }, singleLine = true, modifier = Modifier.weight(1f))
            Button(onClick = onConnect, enabled = !connecting && address.isNotBlank()) { Text("Go") }
        }
        if (connecting) LinearProgressIndicator(Modifier.fillMaxWidth())
        message?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        if (Cart.lines.isNotEmpty()) Text("Your cart is saved: ${Cart.lines.size} title(s), ${Cart.total} Nakfa.", style = MaterialTheme.typography.bodySmall)
    }
}

/** Placed: show the pickup code until the cashier marks it paid, then download into the library. */
@Composable
private fun OrderScreen(p: Cart.Pending, posters: Map<String, ImageBitmap>, openLibrary: () -> Unit) {
    val ctx = LocalContext.current
    var phase by remember(p.token) { mutableStateOf("waiting") }
    var progress by remember { mutableFloatStateOf(0f) }
    var detail by remember { mutableStateOf("") }
    var attempt by remember { mutableIntStateOf(0) }
    var count by remember { mutableIntStateOf(0) }

    LaunchedEffect(p.token, attempt) {
        val client = ShopClient(ctx, p.address)
        withContext(Dispatchers.IO) { runCatching { ShopClient.bindToWifi(ctx) } }
        var state: JSONObject? = null
        while (true) {
            val s = withContext(Dispatchers.IO) { runCatching { client.orderState(p.token) }.getOrNull() }
            if (s == null) phase = "offline"
            else when (s.getString("status")) {
                "pending" -> phase = "waiting"
                "paid" -> { state = s; break }
                else -> { phase = "cancelled"; return@LaunchedEffect }
            }
            delay(2000)
        }
        phase = "downloading"
        val lic = state!!.getJSONArray("licenses")
        count = lic.length()
        try {
            for (i in 0 until lic.length()) {
                val l = lic.getJSONObject(i)
                val titleId = l.getString("title_id")
                PlayerState.importLicense(l.getString("bundle"))
                val name = p.lines.firstOrNull { it.id == titleId }?.en ?: "title"
                if (PlayerState.library.none { it.titleId == titleId }) {
                    detail = "Downloading ${i + 1} of ${lic.length()} · $name"
                    progress = 0f
                    withContext(Dispatchers.IO) {
                        val part = client.download(titleId) { progress = it }
                        val uri = part.inputStream().use { PlayerState.saveToDownloads("$titleId.msb", it) }
                        part.delete()
                        PlayerState.importTitle(uri)
                    }
                }
            }
            phase = "done"
        } catch (e: Exception) {
            detail = e.message ?: "connection lost"
            phase = "failed"
        }
    }

    Column(
        Modifier.fillMaxSize().padding(24.dp).verticalScroll(rememberScrollState()),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Spacer(Modifier.height(12.dp))
        when (phase) {
            "waiting", "offline" -> {
                Text("Show this code at the counter", style = MaterialTheme.typography.titleMedium)
                Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.primary) {
                    Text(
                        p.code, fontFamily = FontFamily.Monospace, fontWeight = FontWeight.Bold, fontSize = 76.sp, letterSpacing = 6.sp,
                        color = MaterialTheme.colorScheme.onPrimary, modifier = Modifier.padding(horizontal = 32.dp, vertical = 10.dp),
                    )
                }
                Text("Pay ${p.total} Nakfa in cash", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                Text(p.shopName, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        p.lines.forEach { l ->
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                PosterBox(l.kind, posters[l.id], Modifier.width(56.dp).aspectRatio(16f / 9f))
                                Text(l.en, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text("${l.price}")
                            }
                        }
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    Text(
                        if (phase == "offline") "Can't reach the shop right now. Stay on its Wi-Fi; this keeps trying."
                        else "Waiting for the cashier. This continues by itself once you've paid.",
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                TextButton(onClick = { Cart.forgetOrder() }) { Text("Change my order") }
            }
            "downloading" -> {
                Text("Paid. Thank you!", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                Text(detail.ifEmpty { "Adding your titles…" })
                LinearProgressIndicator(progress = { progress }, modifier = Modifier.fillMaxWidth())
                Text("Keep this screen open and stay on the shop's Wi-Fi.", style = MaterialTheme.typography.bodySmall)
            }
            "done" -> {
                Text("✓", fontSize = 64.sp, color = MaterialTheme.colorScheme.primary)
                Text("$count ${if (count == 1) "title is" else "titles are"} in your library", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center)
                Text("They play on this phone only, without internet.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                Button(onClick = { Cart.finished(); openLibrary() }, modifier = Modifier.fillMaxWidth().height(52.dp)) { Text("Open library") }
                TextButton(onClick = { Cart.finished() }) { Text("Keep shopping") }
            }
            "failed" -> {
                Text("The download stopped", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                Text("You've paid, and your licenses are saved on this phone. Stay on the shop's Wi-Fi and try again; finished parts are kept. ($detail)", textAlign = TextAlign.Center)
                Button(onClick = { attempt++ }) { Text("Try again") }
            }
            else -> {
                Text("Order ${p.code} was cancelled", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                Text("Nothing was charged. Your cart is still here if you want to order again.", textAlign = TextAlign.Center)
                Button(onClick = { Cart.forgetOrder() }) { Text("Back to the shop") }
            }
        }
    }
}
