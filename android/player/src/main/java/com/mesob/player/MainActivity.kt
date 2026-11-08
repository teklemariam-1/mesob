package com.mesob.player

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.ui.graphics.Color

class MainActivity : ComponentActivity() {
    private var startupError: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Blocks screenshots, screen recording and casting to non-secure displays for the whole app.
        window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        try {
            PlayerState.init(this)
        } catch (e: Exception) {
            startupError = e.message ?: e.toString()
        }
        handle(intent)
        setContent {
            val dark = isSystemInDarkTheme()
            val colors = if (dark) darkColorScheme(primary = Color(0xFFE8B84A), secondary = Color(0xFF8EA6FF))
            else lightColorScheme(primary = Color(0xFF7A5A00), secondary = Color(0xFF2747C9))
            MaterialTheme(colorScheme = colors) { PlayerApp(startupError) }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    /** "Open with Mesob" from Xender, Bluetooth or a file manager. */
    private fun handle(intent: Intent?) {
        if (startupError != null || intent == null) return
        val uri: Uri? = when (intent.action) {
            Intent.ACTION_VIEW -> intent.data
            Intent.ACTION_SEND -> @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_STREAM)
            else -> null
        }
        if (uri == null) return
        val msg = try { PlayerState.importUri(uri) } catch (e: Exception) { e.message ?: "That file couldn't be opened." }
        Toast.makeText(this, msg, Toast.LENGTH_LONG).show()
    }
}
