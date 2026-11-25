package com.lumen.client.android

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build

/** Lock-protected jitter buffer of interleaved stereo s16le frames (48 kHz). */
class AudioRing {
    private val capacity = 24000      // frames (500 ms)
    private val targetFrames = 2880   // ~60 ms jitter buffer
    private val maxFrames = 7200      // ~150 ms: beyond this the oldest data is dropped

    private val buf = ByteArray(capacity * 4)
    private val lock = Any()
    private var readPos = 0
    private var count = 0
    private var primed = false
    private var mutedFlag = false

    var muted: Boolean
        get() = synchronized(lock) { mutedFlag }
        set(v) {
            synchronized(lock) {
                mutedFlag = v
                if (v) { count = 0; readPos = 0; primed = false }
            }
        }

    fun reset() {
        synchronized(lock) { count = 0; readPos = 0; primed = false }
    }

    fun push(data: ByteArray) {
        val frames = minOf(data.size / 4, capacity / 2)
        if (frames <= 0) return
        synchronized(lock) {
            if (mutedFlag) return
            if (count + frames > maxFrames) {
                val drop = minOf(count, count + frames - targetFrames)
                if (drop > 0) { readPos = (readPos + drop) % capacity; count -= drop }
            }
            var w = (readPos + count) % capacity
            var remaining = frames
            var src = 0
            while (remaining > 0) {
                val n = minOf(remaining, capacity - w)
                System.arraycopy(data, src * 4, buf, w * 4, n * 4)
                src += n
                remaining -= n
                w = (w + n) % capacity
            }
            count += frames
        }
    }

    /** Fills [dst] with [frames] frames (4 bytes each). Silence while (re)buffering or on underrun. */
    fun pop(dst: ByteArray, frames: Int) {
        synchronized(lock) {
            if (!primed) {
                if (count >= targetFrames) primed = true
                else { java.util.Arrays.fill(dst, 0, frames * 4, 0.toByte()); return }
            }
            val n = minOf(frames, count)
            var r = readPos
            var done = 0
            while (done < n) {
                val k = minOf(n - done, capacity - r)
                System.arraycopy(buf, r * 4, dst, done * 4, k * 4)
                done += k
                r = (r + k) % capacity
            }
            readPos = r
            count -= n
            if (n < frames) {
                java.util.Arrays.fill(dst, n * 4, frames * 4, 0.toByte())
                primed = false // underrun: refill the jitter buffer before resuming
            }
        }
    }
}

/** Plays the host's 48 kHz stereo s16le stream with a low-latency AudioTrack. */
class AudioPlayer {
    private val ring = AudioRing()
    private var track: AudioTrack? = null
    private var thread: Thread? = null

    @Volatile
    private var running = false

    var muted: Boolean
        get() = ring.muted
        set(v) { ring.muted = v }

    fun push(pcm: ByteArray) = ring.push(pcm)

    @Synchronized
    fun start() {
        if (running) return
        var made: AudioTrack? = null
        try {
            val minBuf = AudioTrack.getMinBufferSize(48000, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            val bytes = maxOf(minBuf, 960 * 4)
            val b = AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                        .build()
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setSampleRate(48000)
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                        .build()
                )
                .setBufferSizeInBytes(bytes)
                .setTransferMode(AudioTrack.MODE_STREAM)
            if (Build.VERSION.SDK_INT >= 26) b.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            val built: AudioTrack = b.build()
            built.play()
            made = built
        } catch (e: Exception) {
            return
        }
        val t: AudioTrack = made ?: return
        ring.reset()
        track = t
        running = true
        val th = Thread({
            val chunk = ByteArray(240 * 4)
            while (running) {
                ring.pop(chunk, 240)
                val n = try { t.write(chunk, 0, chunk.size) } catch (e: Exception) { -1 }
                if (n < 0) {
                    try { Thread.sleep(5) } catch (ie: InterruptedException) { }
                }
            }
        }, "lumen-audio")
        th.isDaemon = true
        th.priority = Thread.MAX_PRIORITY
        thread = th
        th.start()
    }

    @Synchronized
    fun stop() {
        running = false
        try { thread?.join(300) } catch (e: InterruptedException) { }
        thread = null
        val t = track
        track = null
        if (t != null) {
            try { t.stop() } catch (e: Exception) { }
            try { t.release() } catch (e: Exception) { }
        }
        ring.reset()
    }
}
