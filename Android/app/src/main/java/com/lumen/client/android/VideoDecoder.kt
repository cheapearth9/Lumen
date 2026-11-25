package com.lumen.client.android

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.SystemClock
import android.view.Surface
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer

/**
 * HEVC decoder (MediaCodec, synchronous mode) rendering straight to a Surface.
 * The host sends VPS/SPS/PPS separately and the access unit with 4-byte length prefixes (AVCC);
 * MediaCodec wants Annex B, so each length prefix is replaced by a 00 00 00 01 start code.
 */
class VideoDecoder {
    companion object {
        const val OK = 0
        const val NOT_RUNNING = 1
        const val DROPPED = 2
        private const val MAX_INPUT = 2 * 1024 * 1024
        private const val MIME = "video/hevc"
        private val START_CODE = byteArrayOf(0, 0, 0, 1)
    }

    /** Called (from any thread) when the decoder needs the next frame to be a keyframe. */
    var onNeedKeyframe: (() -> Unit)? = null

    /** Called (from any thread) when no HEVC decoding is possible on this device. */
    var onError: ((String) -> Unit)? = null

    private val lock = Any()
    private var surface: Surface? = null
    private var sets: List<ByteArray> = emptyList()
    private var csd: ByteArray = EMPTY_BYTES
    private var width = 0
    private var height = 0
    private var fps = 60
    private var codec: MediaCodec? = null
    private var outThread: Thread? = null

    @Volatile
    private var outRunning = false
    private var waitingKey = true
    private var lastStartAttempt = 0L

    private fun sameSets(a: List<ByteArray>, b: List<ByteArray>): Boolean {
        if (a.size != b.size) return false
        for (i in a.indices) if (!a[i].contentEquals(b[i])) return false
        return true
    }

    /** Main thread: the video surface appeared (or went away: null). */
    fun setSurface(s: Surface?) {
        var started = false
        synchronized(lock) {
            if (surface === s) return
            stopLocked()
            surface = s
            if (s != null && sets.isNotEmpty()) started = startLocked()
        }
        if (started) onNeedKeyframe?.invoke()
    }

    /** Parameter sets (VPS, SPS, PPS) of a keyframe. Restarts the codec when they or the size changed. */
    fun configure(newSets: List<ByteArray>, w: Int, h: Int, rate: Int) {
        synchronized(lock) {
            val same = sameSets(sets, newSets) && w == width && h == height
            sets = newSets
            width = w
            height = h
            fps = rate
            if (same && codec != null) return
            stopLocked()
            if (surface != null) startLocked()
        }
    }

    /** Stops decoding and forgets the stream parameters (keeps the surface). */
    fun reset() {
        synchronized(lock) {
            stopLocked()
            sets = emptyList()
        }
    }

    fun release() {
        synchronized(lock) {
            stopLocked()
            surface = null
            sets = emptyList()
        }
    }

    private fun buildCsd(list: List<ByteArray>): ByteArray {
        val o = ByteArrayOutputStream()
        for (s in list) {
            o.write(START_CODE, 0, 4)
            o.write(s, 0, s.size)
        }
        return o.toByteArray()
    }

    private fun format(full: Boolean): MediaFormat {
        val f = MediaFormat.createVideoFormat(MIME, width, height)
        f.setByteBuffer("csd-0", ByteBuffer.wrap(csd))
        f.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_INPUT)
        if (full) {
            f.setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            if (Build.VERSION.SDK_INT >= 23) {
                f.setInteger(MediaFormat.KEY_PRIORITY, 0)
                f.setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
            }
            if (Build.VERSION.SDK_INT >= 30) f.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            // vendor low-latency switches (ignored by decoders that do not know them)
            f.setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
            f.setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
            f.setInteger("vendor.low-latency.enable", 1)
        }
        return f
    }

    private fun startLocked(): Boolean {
        val s = surface ?: return false
        if (sets.isEmpty() || width <= 0 || height <= 0) return false
        lastStartAttempt = SystemClock.elapsedRealtime()
        csd = buildCsd(sets)
        var lastError: Exception? = null
        for (full in booleanArrayOf(true, false)) {
            var c: MediaCodec? = null
            try {
                val created: MediaCodec = MediaCodec.createDecoderByType(MIME)
                c = created
                created.configure(format(full), s, null, 0)
                created.start()
                codec = created
                waitingKey = true
                outRunning = true
                val started: MediaCodec = created
                val t = Thread({ outputLoop(started) }, "lumen-decode-out")
                t.isDaemon = true
                outThread = t
                t.start()
                return true
            } catch (e: Exception) {
                lastError = e
                try { c?.release() } catch (e2: Exception) { }
            }
        }
        onError?.invoke("This device cannot decode the HEVC video stream (" + (lastError?.message ?: "decoder error") + ")")
        return false
    }

    private fun stopLocked() {
        outRunning = false
        val t = outThread
        outThread = null
        val c = codec
        codec = null
        if (t != null && t !== Thread.currentThread()) {
            try { t.join(300) } catch (e: InterruptedException) { }
        }
        if (c != null) {
            try { c.stop() } catch (e: Exception) { }
            try { c.release() } catch (e: Exception) { }
        }
    }

    private fun outputLoop(c: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        while (outRunning) {
            try {
                val i = c.dequeueOutputBuffer(info, 10000)
                if (i >= 0) c.releaseOutputBuffer(i, true) // render immediately, no jitter buffer
            } catch (e: Exception) {
                if (outRunning) {
                    synchronized(lock) { if (codec === c) stopLocked() }
                    onNeedKeyframe?.invoke()
                }
                break
            }
        }
    }

    /** Queues one access unit given as 4-byte-length-prefixed NAL units at data[off, off+len). */
    fun queueFrame(data: ByteArray, off: Int, len: Int, key: Boolean): Int =
        synchronized(lock) { queueLocked(data, off, len, key) }

    private fun queueLocked(data: ByteArray, off: Int, len: Int, key: Boolean): Int {
            val c = codec
            if (c == null) {
                if (surface != null && sets.isNotEmpty() &&
                    SystemClock.elapsedRealtime() - lastStartAttempt > 1000
                ) {
                    if (startLocked()) onNeedKeyframe?.invoke()
                }
                return NOT_RUNNING
            }
            if (waitingKey) {
                if (!key) return DROPPED
                waitingKey = false
            }
            val extra = if (key) csd.size else 0
            if (len + extra > MAX_INPUT) {
                waitingKey = true
                return DROPPED
            }
            try {
                val idx = c.dequeueInputBuffer(2000)
                if (idx < 0) {
                    waitingKey = true
                    return DROPPED
                }
                val buf = c.getInputBuffer(idx)
                if (buf == null || buf.capacity() < len + extra) {
                    c.queueInputBuffer(idx, 0, 0, 0, 0)
                    waitingKey = true
                    return DROPPED
                }
                buf.clear()
                if (key) buf.put(csd)
                var p = off
                val end = off + len
                var nals = 0
                while (p + 4 <= end) {
                    val n = ((data[p].toInt() and 0xFF) shl 24) or ((data[p + 1].toInt() and 0xFF) shl 16) or
                        ((data[p + 2].toInt() and 0xFF) shl 8) or (data[p + 3].toInt() and 0xFF)
                    if (n <= 0 || p + 4 + n > end) break
                    buf.put(START_CODE)
                    buf.put(data, p + 4, n)
                    p += 4 + n
                    nals++
                }
                if (nals == 0) {
                    c.queueInputBuffer(idx, 0, 0, 0, 0)
                    waitingKey = true
                    return DROPPED
                }
                val flags = if (key) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                c.queueInputBuffer(idx, 0, buf.position(), System.nanoTime() / 1000, flags)
                return OK
            } catch (e: Exception) {
                lastStartAttempt = SystemClock.elapsedRealtime()
                stopLocked()
                return DROPPED
            }
    }
}
