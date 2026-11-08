package com.mesob.player

import android.graphics.Bitmap
import android.graphics.pdf.PdfRenderer
import android.os.Build
import android.os.ParcelFileDescriptor
import android.system.Os
import android.system.OsConstants
import androidx.annotation.OptIn
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.MediaItem
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.ui.PlayerView
import com.mesob.core.Mesob
import com.mesob.core.MesobNative
import com.mesob.core.Protection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream

private class Session(val handle: Long, val serial: Long, val size: Long)

private fun openSession(item: Item): Session {
    val r = Mesob.call(
        "session.open",
        Mesob.obj("fd" to PlayerState.fd(item.uri), "bundle" to item.bundle, "device_secret" to PlayerState.deviceSecret),
    )
    return Session(r.getLong("handle"), r.getJSONObject("license").getLong("serial"), r.getLong("size"))
}

@Composable
private fun Blocked(text: String, close: () -> Unit) {
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        Text(text, textAlign = TextAlign.Center)
        Spacer(Modifier.height(16.dp))
        Button(onClick = close) { Text("Back") }
    }
}

@OptIn(UnstableApi::class)
@Composable
fun PlayScreen(item: Item, close: () -> Unit) {
    val ctx = LocalContext.current
    val check = remember { Protection.check(ctx) }
    if (!check.ok) { Blocked(check.problems.joinToString("\n"), close); return }
    val session = remember { runCatching { openSession(item) } }
    val s = session.getOrElse { Blocked("This title can't play: ${it.message}", close); return }
    val player = remember {
        ExoPlayer.Builder(ctx).build().apply {
            val media = MediaItem.Builder().setUri("mesob://title/${item.titleId}").setMimeType(item.mime).build()
            setMediaSource(ProgressiveMediaSource.Factory(MesobDataSource.Factory(s.handle)).createMediaSource(media))
            prepare()
            playWhenReady = true
        }
    }
    DisposableEffect(Unit) {
        onDispose {
            player.release()
            MesobNative.sessionClose(s.handle)
        }
    }
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        AndroidView(factory = { PlayerView(it).apply { this.player = player; keepScreenOn = true } }, modifier = Modifier.fillMaxSize())
        if (item.kind == "audio") {
            Column(Modifier.align(Alignment.Center).padding(bottom = 120.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Poster(item, Modifier.size(220.dp))
                Text(item.name, color = Color.White, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 12.dp))
                Text(item.artist, color = Color.White.copy(alpha = 0.8f))
            }
        }
        Watermark(Protection.watermark(s.serial, PlayerState.fingerprint))
    }
}

/**
 * PDF books. The decrypted PDF exists only in memory: an anonymous memory file on Android 11+,
 * or (older phones) a private cache file that is deleted as soon as it's opened.
 */
@Composable
fun ReaderScreen(item: Item, close: () -> Unit) {
    val ctx = LocalContext.current
    val check = remember { Protection.check(ctx) }
    if (!check.ok) { Blocked(check.problems.joinToString("\n"), close); return }
    var renderer by remember { mutableStateOf<PdfRenderer?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var page by remember { mutableIntStateOf(0) }
    var serial by remember { mutableLongStateOf(0L) }
    LaunchedEffect(Unit) {
        try {
            renderer = withContext(Dispatchers.IO) {
                val s = openSession(item)
                serial = s.serial
                val bytes = ByteArray(s.size.toInt())
                var off = 0
                while (off < bytes.size) {
                    val n = MesobNative.sessionRead(s.handle, off.toLong(), bytes, off, minOf(1 shl 20, bytes.size - off))
                    if (n <= 0) break
                    off += n
                }
                MesobNative.sessionClose(s.handle)
                val pfd = memoryFile(ctx.cacheDir, bytes)
                bytes.fill(0)
                PdfRenderer(pfd)
            }
        } catch (e: Exception) { error = e.message }
    }
    DisposableEffect(Unit) { onDispose { renderer?.close(); cacheWipe(ctx.cacheDir) } }
    val r = renderer
    if (error != null) { Blocked("This book can't open: $error", close); return }
    if (r == null) { Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return }
    val bmp = remember(page) {
        r.openPage(page).use { p ->
            val w = 1400
            val b = Bitmap.createBitmap(w, (w * p.height / p.width.toFloat()).toInt(), Bitmap.Config.ARGB_8888)
            b.eraseColor(android.graphics.Color.WHITE)
            p.render(b, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
            b.asImageBitmap()
        }
    }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { if (page > 0) page-- }) { Text("Previous") }
            OutlinedButton(onClick = { if (page < r.pageCount - 1) page++ }) { Text("Next") }
            Text("${page + 1} / ${r.pageCount}")
        }
        Box(Modifier.fillMaxSize()) {
            Image(bmp, "Page ${page + 1}", Modifier.fillMaxSize())
            Box(Modifier.fillMaxSize().background(Color.Transparent)) { Watermark(Protection.watermark(serial, PlayerState.fingerprint)) }
        }
    }
}

private fun memoryFile(cache: File, bytes: ByteArray): ParcelFileDescriptor {
    if (Build.VERSION.SDK_INT >= 30) {
        val fd = Os.memfd_create("mesob-pdf", 0)
        FileOutputStream(fd).use { it.write(bytes) }
        Os.lseek(fd, 0, OsConstants.SEEK_SET)
        return ParcelFileDescriptor.dup(fd).also { Os.close(fd) }
    }
    val f = File.createTempFile("mesob-", ".bin", cache)
    f.writeBytes(bytes)
    val pfd = ParcelFileDescriptor.open(f, ParcelFileDescriptor.MODE_READ_ONLY)
    f.delete()
    return pfd
}
