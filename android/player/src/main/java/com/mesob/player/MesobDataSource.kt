package com.mesob.player

import android.net.Uri
import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.BaseDataSource
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DataSpec
import com.mesob.core.MesobNative
import java.io.IOException

/**
 * Media3 data source over an open core session. ExoPlayer asks for byte ranges; the core decrypts
 * exactly those bytes into ExoPlayer's buffer. No cache, nothing written to storage.
 */
@UnstableApi
class MesobDataSource(private val handle: Long) : BaseDataSource(false) {
    private var uri: Uri? = null
    private var pos = 0L
    private var remaining = 0L
    private var opened = false

    override fun open(dataSpec: DataSpec): Long {
        uri = dataSpec.uri
        val size = MesobNative.sessionSize(handle)
        if (size < 0) throw IOException("Playback session closed")
        pos = dataSpec.position
        remaining = if (dataSpec.length != C.LENGTH_UNSET.toLong()) dataSpec.length else size - pos
        if (remaining < 0) throw IOException("Seek past the end")
        transferInitializing(dataSpec)
        opened = true
        transferStarted(dataSpec)
        return remaining
    }

    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
        if (length == 0) return 0
        if (remaining == 0L) return C.RESULT_END_OF_INPUT
        val want = minOf(length.toLong(), remaining).toInt()
        val n = MesobNative.sessionRead(handle, pos, buffer, offset, want)
        if (n < 0) throw IOException("The title could not be read. Copy it again from the shop.")
        if (n == 0) return C.RESULT_END_OF_INPUT
        pos += n
        remaining -= n
        bytesTransferred(n)
        return n
    }

    override fun getUri(): Uri? = uri

    override fun close() {
        if (opened) {
            opened = false
            transferEnded()
        }
        uri = null
    }

    class Factory(private val handle: Long) : DataSource.Factory {
        override fun createDataSource(): DataSource = MesobDataSource(handle)
    }
}
