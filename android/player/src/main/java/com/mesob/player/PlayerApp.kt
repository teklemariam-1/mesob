package com.mesob.player

import android.graphics.BitmapFactory
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.mesob.core.Mesob
import com.mesob.core.Protection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

private enum class Tab { Library, Shop, Device }

private sealed interface Screen {
    data object Tabs : Screen
    data class Play(val item: Item) : Screen
    data class Read(val item: Item) : Screen
    data class Request(val items: List<Item>) : Screen
}

@Composable
fun PlayerApp(startupError: String?) {
    if (startupError != null) {
        Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
            Text("Mesob can't start on this phone: $startupError")
        }
        return
    }
    var screen by remember { mutableStateOf<Screen>(Screen.Tabs) }
    var tab by remember { mutableStateOf(Tab.Library) }
    BackHandler(enabled = screen != Screen.Tabs) { screen = Screen.Tabs }
    when (val s = screen) {
        is Screen.Play -> PlayScreen(s.item) { screen = Screen.Tabs }
        is Screen.Read -> ReaderScreen(s.item) { screen = Screen.Tabs }
        is Screen.Request -> RequestScreen(s.items) { screen = Screen.Tabs }
        Screen.Tabs -> Scaffold(
            bottomBar = {
                NavigationBar {
                    Tab.entries.forEach { t ->
                        NavigationBarItem(
                            selected = tab == t, onClick = { tab = t },
                            icon = { Text(when (t) { Tab.Library -> "▤"; Tab.Shop -> "◈"; Tab.Device -> "◉" }, fontSize = 18.sp) },
                            label = { Text(when (t) { Tab.Library -> "Library"; Tab.Shop -> "Shop"; Tab.Device -> "This phone" }) },
                        )
                    }
                }
            },
        ) { pad ->
            Box(Modifier.padding(pad)) {
                when (tab) {
                    Tab.Library -> LibraryScreen(
                        open = { screen = if (it.kind == "pdf") Screen.Read(it) else Screen.Play(it) },
                        request = { screen = Screen.Request(it) },
                    )
                    Tab.Shop -> StoreScreen(openLibrary = { tab = Tab.Library })
                    Tab.Device -> DeviceScreen()
                }
            }
        }
    }
}

@Composable
private fun LibraryScreen(open: (Item) -> Unit, request: (List<Item>) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf("") }
    var lockedChoice by remember { mutableStateOf<Item?>(null) }
    val scan = rememberLauncherForActivityResult(ScanContract()) { r ->
        val text = r.contents ?: return@rememberLauncherForActivityResult
        status = try { PlayerState.importLicense(text.trim()); "License added." } catch (e: Exception) { "That QR code isn't a license for this phone: ${e.message}" }
    }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris: List<Uri> ->
        scope.launch {
            val msgs = withContext(Dispatchers.IO) { uris.map { runCatching { PlayerState.importUri(it) }.getOrElse { e -> e.message ?: "not added" } } }
            status = msgs.joinToString("\n")
        }
    }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("Library", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { pick.launch(arrayOf("*/*")) }) { Text("Add files") }
            OutlinedButton(onClick = { scan.launch(ScanOptions().setPrompt("Scan the license QR on the shop's screen").setBeepEnabled(false).setOrientationLocked(false)) }) { Text("Scan license") }
        }
        if (status.isNotEmpty()) Text(status, style = MaterialTheme.typography.bodySmall)
        if (PlayerState.library.isEmpty()) {
            Text("Your library is empty. Buy at a Mesob shop over its Wi-Fi (Shop), or open title files a friend sent you: they stay locked until you buy a license.")
        }
        LazyVerticalGrid(GridCells.Adaptive(160.dp), horizontalArrangement = Arrangement.spacedBy(10.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            items(PlayerState.library.sortedWith(compareBy({ it.locked }, { it.titleEn })), key = { it.titleId }) { item ->
                Column(Modifier.clip(RoundedCornerShape(8.dp)).clickable { if (item.locked) lockedChoice = item else open(item) }) {
                    Poster(item, Modifier.fillMaxWidth().aspectRatio(16f / 9f))
                    Text(item.titleEn, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 6.dp))
                    if (item.titleTi.isNotBlank()) Text(item.titleTi, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(if (item.locked) "Locked · buy at a shop" else item.artist.ifBlank { item.kind }, style = MaterialTheme.typography.bodySmall, maxLines = 1)
                }
            }
        }
    }
    lockedChoice?.let { item ->
        AlertDialog(
            onDismissRequest = { lockedChoice = null },
            title = { Text("${item.titleEn} is locked") },
            text = { Text("Buy it at any Mesob shop. Show this phone's request code at the counter, or buy over the shop's Wi-Fi in Shop.") },
            confirmButton = { TextButton(onClick = { lockedChoice = null; request(listOf(item)) }) { Text("Show request code") } },
            dismissButton = { TextButton(onClick = { lockedChoice = null }) { Text("Close") } },
        )
    }
}

@Composable
fun Poster(item: Item, modifier: Modifier) {
    val bmp = remember(item.poster) { item.poster?.let { runCatching { BitmapFactory.decodeFile(it)?.asImageBitmap() }.getOrNull() } }
    Box(modifier.clip(RoundedCornerShape(8.dp)).background(Color(0xFF1C2A3E)), contentAlignment = Alignment.Center) {
        if (bmp != null) Image(bmp, null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        else Text(when (item.kind) { "audio" -> "♪"; "pdf" -> "BOOK"; else -> "▶" }, color = Color.White, fontSize = 28.sp)
    }
}

@Composable
private fun RequestScreen(items: List<Item>, close: () -> Unit) {
    val text = remember(items) { PlayerState.requestFor(items.map { it.titleId }) }
    val bmp = remember(text) { Qr.bitmap(text).asImageBitmap() }
    Column(Modifier.fillMaxSize().background(Color.White).padding(20.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Show this at the shop counter", color = Color.Black, style = MaterialTheme.typography.titleLarge)
        Text(items.joinToString { it.titleEn }, color = Color.Black)
        Image(bmp, "Request code", Modifier.fillMaxWidth().aspectRatio(1f))
        Text("After you pay, scan the license code the shop shows you (Library → Scan license).", color = Color.Black)
        Button(onClick = close) { Text("Done") }
    }
}

@Composable
private fun DeviceScreen() {
    val ctx = LocalContext.current
    val report = remember { Protection.check(ctx) }
    Column(Modifier.fillMaxSize().padding(16.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("This phone", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        Text("Licenses are made for this phone's device code. After a reset or on a new phone, bring your receipt serial to a shop for a free replacement.")
        Text("Device code", fontWeight = FontWeight.SemiBold)
        Text(PlayerState.fingerprint.chunked(4).joinToString(" ").uppercase(), fontFamily = FontFamily.Monospace, fontSize = 20.sp)
        Text("Protection", fontWeight = FontWeight.SemiBold)
        Text(if (PlayerState.store.hardwareBacked) "Device key locked in this phone's secure hardware." else "Device key locked by Android Keystore (software on this phone).")
        Text("Screenshots and screen recording of Mesob are blocked.")
        if (!report.ok) Text(report.problems.joinToString("\n"), color = MaterialTheme.colorScheme.error)
        Text("Mesob ${Mesob.call("core.info").getString("version")}${if (Mesob.isLab) " · LAB BUILD" else ""}", style = MaterialTheme.typography.bodySmall)
    }
}

/** Shared by the player and reader: a faint code that moves every 12 seconds. */
@Composable
fun Watermark(text: String) {
    var spot by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) { while (true) { kotlinx.coroutines.delay(12_000); spot = (spot + 1) % 6 } }
    val align = listOf(Alignment.TopStart, Alignment.CenterEnd, Alignment.BottomStart, Alignment.TopEnd, Alignment.Center, Alignment.BottomEnd)[spot]
    Box(Modifier.fillMaxSize().padding(36.dp), contentAlignment = align) {
        Text(text, color = Color.White.copy(alpha = 0.32f), fontFamily = FontFamily.Monospace, fontSize = 15.sp)
    }
}

fun cacheWipe(dir: File) = dir.listFiles()?.filter { it.name.startsWith("mesob-") }?.forEach { it.delete() }
