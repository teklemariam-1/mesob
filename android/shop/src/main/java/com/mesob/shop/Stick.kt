package com.mesob.shop

import android.content.Context
import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import java.io.File

/** A USB stick (or any folder) chosen by the user through Android's folder picker. */
class Stick(private val context: Context, tree: Uri) {
    private val root = DocumentFile.fromTreeUri(context, tree) ?: throw IllegalArgumentException("Can't open that folder.")

    /** The MESOB folder at the stick's root (created if needed). */
    val mesob: DocumentFile = root.findFile("MESOB") ?: root.createDirectory("MESOB") ?: throw IllegalStateException("Can't write to that stick.")

    fun dir(parent: DocumentFile, name: String, create: Boolean = false): DocumentFile? =
        parent.findFile(name) ?: if (create) parent.createDirectory(name) else null

    fun file(parent: DocumentFile, path: String): DocumentFile? {
        var d: DocumentFile? = parent
        val parts = path.split('/')
        for (p in parts.dropLast(1)) d = d?.findFile(p)
        return d?.findFile(parts.last())
    }

    fun read(f: DocumentFile): ByteArray = context.contentResolver.openInputStream(f.uri)!!.use { it.readBytes() }

    fun write(parent: DocumentFile, name: String, data: ByteArray, mime: String = "application/octet-stream") {
        parent.findFile(name)?.delete()
        val f = parent.createFile(mime, name) ?: throw IllegalStateException("Can't write $name to the stick.")
        context.contentResolver.openOutputStream(f.uri, "w")!!.use { it.write(data) }
    }

    fun copyOut(f: DocumentFile, dest: File) {
        context.contentResolver.openInputStream(f.uri)!!.use { inp -> dest.outputStream().use { inp.copyTo(it, 1 shl 16) } }
    }

    fun copyIn(parent: DocumentFile, name: String, src: File) {
        parent.findFile(name)?.let { if (it.length() == src.length()) return else it.delete() }
        val f = parent.createFile("application/octet-stream", name) ?: throw IllegalStateException("Can't write $name to the stick.")
        context.contentResolver.openOutputStream(f.uri, "w")!!.use { out -> src.inputStream().use { it.copyTo(out, 1 shl 16) } }
    }
}
