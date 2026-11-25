package com.lumen.client.android

import java.io.ByteArrayOutputStream

const val LUMEN_PORT = 47900

object PacketType {
    const val HELLO = 1
    const val WELCOME = 2
    const val VIDEO = 3
    const val INPUT = 4
    const val KEYFRAME_REQUEST = 5
    const val PING = 6
    const val BYE = 7
    const val STATUS = 8
    const val DISCOVER = 9
    const val ANNOUNCE = 10
    const val HANDSHAKE = 11
    const val AUDIO = 12
    const val PAIR = 13
}

val EMPTY_BYTES = ByteArray(0)

class ByteWriter {
    private val out = ByteArrayOutputStream()
    fun u8(v: Int): ByteWriter { out.write(v and 0xFF); return this }
    fun u16(v: Int): ByteWriter { out.write((v shr 8) and 0xFF); out.write(v and 0xFF); return this }
    fun i16(v: Int): ByteWriter = u16(v)
    fun bytes(b: ByteArray): ByteWriter { out.write(b, 0, b.size); return this }
    fun toByteArray(): ByteArray = out.toByteArray()
}

/** Reads big-endian values. Reading past the end sets [ok] to false and returns 0. */
class ByteReader(private val data: ByteArray, private var pos: Int = 0) {
    var ok: Boolean = true
        private set
    val remaining: Int get() = data.size - pos

    fun u8(): Int {
        if (pos >= data.size) { ok = false; return 0 }
        val v = data[pos].toInt() and 0xFF
        pos++
        return v
    }
    fun u16(): Int {
        val a = u8()
        val b = u8()
        return (a shl 8) or b
    }
    fun i16(): Int = u16().toShort().toInt()
    fun take(n: Int): ByteArray {
        if (n < 0 || remaining < n) { ok = false; return EMPTY_BYTES }
        val r = data.copyOfRange(pos, pos + n)
        pos += n
        return r
    }
    fun rest(): ByteArray = take(remaining)
}

class Packet(
    val type: Int,
    val flags: Int,
    val id: Int,
    val fragIndex: Int,
    val fragCount: Int,
    val timestampMs: Int,
    val payload: ByteArray
)

object Wire {
    const val MAGIC_HI = 0x4C
    const val MAGIC_LO = 0x4D

    private fun put16(o: ByteArray, at: Int, v: Int) {
        o[at] = (v shr 8).toByte()
        o[at + 1] = v.toByte()
    }
    private fun put32(o: ByteArray, at: Int, v: Int) {
        o[at] = (v shr 24).toByte()
        o[at + 1] = (v shr 16).toByte()
        o[at + 2] = (v shr 8).toByte()
        o[at + 3] = v.toByte()
    }
    private fun get16(d: ByteArray, at: Int): Int = ((d[at].toInt() and 0xFF) shl 8) or (d[at + 1].toInt() and 0xFF)
    private fun get32(d: ByteArray, at: Int): Int =
        ((d[at].toInt() and 0xFF) shl 24) or ((d[at + 1].toInt() and 0xFF) shl 16) or
            ((d[at + 2].toInt() and 0xFF) shl 8) or (d[at + 3].toInt() and 0xFF)

    fun encode(
        type: Int,
        flags: Int = 0,
        id: Int = 0,
        fragIndex: Int = 0,
        fragCount: Int = 1,
        timestampMs: Int = 0,
        payload: ByteArray = EMPTY_BYTES
    ): ByteArray {
        val out = ByteArray(16 + payload.size)
        out[0] = MAGIC_HI.toByte()
        out[1] = MAGIC_LO.toByte()
        out[2] = type.toByte()
        out[3] = flags.toByte()
        put32(out, 4, id)
        put16(out, 8, fragIndex)
        put16(out, 10, fragCount)
        put32(out, 12, timestampMs)
        System.arraycopy(payload, 0, out, 16, payload.size)
        return out
    }

    fun decode(d: ByteArray): Packet? {
        if (d.size < 16) return null
        if ((d[0].toInt() and 0xFF) != MAGIC_HI || (d[1].toInt() and 0xFF) != MAGIC_LO) return null
        val type = d[2].toInt() and 0xFF
        if (type < 1 || type > 13) return null
        val fi = get16(d, 8)
        val fc = get16(d, 10)
        if (fc < 1 || fi >= fc) return null
        return Packet(type, d[3].toInt() and 0xFF, get32(d, 4), fi, fc, get32(d, 12), d.copyOfRange(16, d.size))
    }
}

/** Reassembles fragmented video frames. Frame ids compare with wrap-around (signed difference). */
class Reassembler {
    class Frame(val id: Int, val flags: Int, val timestampMs: Int, val data: ByteArray, val gap: Boolean)

    private class Partial(count: Int, val flags: Int, val ts: Int) {
        val parts: Array<ByteArray?> = arrayOfNulls(count)
        var received = 0
    }

    private val partials = HashMap<Int, Partial>()
    private var last = 0
    private var hasLast = false

    fun reset() {
        partials.clear()
        hasLast = false
        last = 0
    }

    fun add(p: Packet): Frame? {
        if (hasLast && (p.id - last) <= 0) return null // old / duplicate

        var part = partials[p.id]
        if (part == null) {
            part = Partial(p.fragCount, p.flags, p.timestampMs)
            partials[p.id] = part
        }
        if (part.parts.size != p.fragCount || p.fragIndex >= part.parts.size) return null
        if (part.parts[p.fragIndex] == null) {
            part.parts[p.fragIndex] = p.payload
            part.received++
        }

        if (part.received == part.parts.size) {
            var total = 0
            for (b in part.parts) total += b?.size ?: 0
            val data = ByteArray(total)
            var o = 0
            for (b in part.parts) {
                if (b != null) {
                    System.arraycopy(b, 0, data, o, b.size)
                    o += b.size
                }
            }
            val gap = hasLast && (p.id - last) != 1
            last = p.id
            hasLast = true
            val done = p.id
            val it = partials.keys.iterator()
            while (it.hasNext()) {
                val k = it.next()
                if ((k - done) <= 0) it.remove()
            }
            return Frame(p.id, part.flags, part.ts, data, gap)
        }

        if (partials.size > 32) {
            var oldest: Int? = null
            for (k in partials.keys) {
                val o = oldest
                if (o == null || (k - o) < 0) oldest = k
            }
            if (oldest != null) partials.remove(oldest)
        }
        return null
    }
}

class HelloInfo(var width: Int, var height: Int, var fps: Int, var bitrateMbps: Int, var flags: Int) {
    companion object {
        const val FLAG_AUDIO = 1
        const val FLAG_EXTEND = 2
    }

    val wantsAudio: Boolean get() = (flags and FLAG_AUDIO) != 0
    val wantsExtend: Boolean get() = (flags and FLAG_EXTEND) != 0

    fun encode(): ByteArray =
        ByteWriter().u16(width).u16(height).u8(fps).u16(bitrateMbps).u8(flags).toByteArray()

    fun copy(): HelloInfo = HelloInfo(width, height, fps, bitrateMbps, flags)
}

class WelcomeInfo(
    val width: Int,
    val height: Int,
    val fps: Int,
    val caps: Int,
    val sourceWidth: Int,
    val sourceHeight: Int,
    val mode: Int
) {
    companion object {
        const val CAP_NATIVE_TOUCH = 1
        const val CAP_AUDIO = 2

        fun decode(d: ByteArray): WelcomeInfo? {
            val r = ByteReader(d)
            val w = r.u16()
            val h = r.u16()
            val f = r.u8()
            if (!r.ok) return null
            val caps = if (r.remaining >= 1) r.u8() else 0
            val sw = if (r.remaining >= 2) r.u16() else w
            val sh = if (r.remaining >= 2) r.u16() else h
            val mode = if (r.remaining >= 1) r.u8() else 0
            return WelcomeInfo(w, h, f, caps, sw, sh, mode)
        }
    }
}

sealed class InputEvent {
    abstract fun encode(): ByteArray

    /** Events that must not be lost (sent twice, de-duplicated on the host). */
    open val isDiscrete: Boolean get() = false

    class MouseRel(val dx: Int, val dy: Int) : InputEvent() {
        override fun encode(): ByteArray = ByteWriter().u8(1).i16(dx).i16(dy).toByteArray()
    }

    class MouseAbs(val x: Int, val y: Int) : InputEvent() {
        override fun encode(): ByteArray = ByteWriter().u8(2).u16(x).u16(y).toByteArray()
    }

    class MouseButton(val index: Int, val down: Boolean) : InputEvent() {
        override val isDiscrete: Boolean get() = true
        override fun encode(): ByteArray = ByteWriter().u8(3).u8(index).u8(if (down) 1 else 0).toByteArray()
    }

    class Scroll(val dx: Int, val dy: Int) : InputEvent() {
        override fun encode(): ByteArray = ByteWriter().u8(4).i16(dx).i16(dy).toByteArray()
    }

    class Key(val code: Int, val down: Boolean) : InputEvent() {
        override val isDiscrete: Boolean get() = true
        override fun encode(): ByteArray = ByteWriter().u8(5).u16(code).u8(if (down) 1 else 0).toByteArray()
    }

    class Touch(val id: Int, val phase: Int, val x: Int, val y: Int) : InputEvent() {
        override val isDiscrete: Boolean get() = phase >= 2
        override fun encode(): ByteArray = ByteWriter().u8(6).u8(id).u8(phase).u16(x).u16(y).toByteArray()
    }

    class Gamepad(
        val index: Int, val buttons: Int, val lt: Int, val rt: Int,
        val lx: Int, val ly: Int, val rx: Int, val ry: Int
    ) : InputEvent() {
        override fun encode(): ByteArray =
            ByteWriter().u8(7).u8(index).u16(buttons).u8(lt).u8(rt).i16(lx).i16(ly).i16(rx).i16(ry).toByteArray()
    }

    class GamepadPresence(val index: Int, val present: Boolean) : InputEvent() {
        override val isDiscrete: Boolean get() = true
        override fun encode(): ByteArray = ByteWriter().u8(8).u8(index).u8(if (present) 1 else 0).toByteArray()
    }
}
