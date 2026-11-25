package com.lumen.client.android

import android.os.Handler
import android.os.Looper
import android.view.MotionEvent

/** Where the engine sends its results. Implemented by the stream activity. */
interface TouchDelegate {
    fun engineEmit(e: InputEvent)

    /** Relative cursor movement in host pixels. */
    fun engineMoveCursor(dx: Float, dy: Float)

    /** Maps a point in view pixels to 0..65535 over the video rectangle: [x, y]. */
    fun engineNormalise(x: Float, y: Float): IntArray

    /** Host pixels per dp of the displayed video. */
    fun engineSourcePixelsPerDp(): Float
}

/**
 * Turns raw Android touches into Lumen input events (port of the iOS TouchEngine). Main thread only.
 *  - Touchpad: relative cursor with mild acceleration, 1-finger tap = left click, 2-finger tap = right click,
 *    2-finger drag = natural scroll, tap then touch down quickly and drag = left-button drag.
 *  - Touchscreen + host with native touch: real multi-touch is forwarded (hold = the host's touch-and-hold).
 *  - Touchscreen + host without native touch: emulation (tap = click, 1-finger drag = scroll,
 *    long press = right click, 2-finger tap = right click).
 */
class TouchEngine(private val density: Float) {
    private enum class Route { PAD, NATIVE, EMULATED }

    private class Track(
        val id: Int,
        val slot: Int,
        val startX: Float,
        val startY: Float,
        var x: Float,
        var y: Float,
        var lastTime: Long
    ) {
        var speed = 0f        // smoothed px / second
        var moved = false
    }

    companion object {
        private const val TAP_MAX = 300L
        private const val TWO_TAP_MAX = 400L
        private const val TAP_DRAG_WINDOW = 300L
        private const val LONG_PRESS = 350L
        private const val LOCKED_GAIN = 1.6f
    }

    var delegate: TouchDelegate? = null

    var mode: String = Prefs.TOUCHSCREEN
        set(v) { if (v != field) { field = v; cancelAll() } }
    var hostHasNativeTouch: Boolean = false
        set(v) { if (v != field) { field = v; cancelAll() } }
    /** Game mode (pointer capture): touch behaves like a plain touchpad with a fixed gain. */
    var pointerLocked: Boolean = false
        set(v) { if (v != field) { field = v; cancelAll() } }

    private val handler = Handler(Looper.getMainLooper())
    private val tracks = HashMap<Int, Track>()
    private var usedSlots = 0
    private var route = Route.EMULATED

    private var gestureStart = 0L
    private var gestureMax = 0
    private var gestureMoved = false
    private var scrolling = false
    private var scrollRemX = 0f
    private var scrollRemY = 0f
    private var buttonHeld = false
    private var lastTapEnd = -100000L
    private var longPressDone = false
    private val longPressRunnable = Runnable { longPressFired() }

    private val slop: Float get() = 10f * density

    // MARK: Entry point

    fun onTouchEvent(ev: MotionEvent) {
        when (ev.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                if (tracks.isNotEmpty()) cancelAll()
                down(ev.getPointerId(0), ev.getX(0), ev.getY(0), ev.eventTime)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                val i = ev.actionIndex
                down(ev.getPointerId(i), ev.getX(i), ev.getY(i), ev.eventTime)
            }
            MotionEvent.ACTION_MOVE -> {
                for (i in 0 until ev.pointerCount) move(ev.getPointerId(i), ev.getX(i), ev.getY(i), ev.eventTime)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val i = ev.actionIndex
                finish(ev.getPointerId(i), ev.getX(i), ev.getY(i), ev.eventTime, false)
            }
            MotionEvent.ACTION_CANCEL -> {
                val ids = ArrayList<Int>(tracks.keys)
                for (id in ids) {
                    val tr = tracks[id] ?: continue
                    finish(id, tr.x, tr.y, ev.eventTime, true)
                }
            }
        }
    }

    /** Releases everything that is held and forgets all touches (mode change, view going away). */
    fun cancelAll() {
        cancelLongPress()
        if (buttonHeld) {
            buttonHeld = false
            delegate?.engineEmit(InputEvent.MouseButton(0, false))
        }
        if (route == Route.NATIVE) {
            for (tr in tracks.values) delegate?.engineEmit(touchEvent(tr, 3))
        }
        tracks.clear()
        usedSlots = 0
    }

    // MARK: Common

    private fun down(id: Int, x: Float, y: Float, t: Long) {
        if (tracks.containsKey(id)) return
        val slot = allocSlot()
        if (slot < 0) return
        if (tracks.isEmpty()) beginGesture(t)
        val tr = Track(id, slot, x, y, x, y, t)
        tracks[id] = tr
        gestureMax = maxOf(gestureMax, tracks.size)
        when (route) {
            Route.PAD -> padDown()
            Route.NATIVE -> nativeDown(tr)
            Route.EMULATED -> emuDown()
        }
    }

    private fun move(id: Int, x: Float, y: Float, t: Long) {
        val tr = tracks[id] ?: return
        val dx = x - tr.x
        val dy = y - tr.y
        if (dx == 0f && dy == 0f) return
        val dt = maxOf(4L, t - tr.lastTime)
        tr.speed = tr.speed * 0.6f + (Math.hypot(dx.toDouble(), dy.toDouble()).toFloat() * 1000f / dt) * 0.4f
        tr.lastTime = t
        tr.x = x
        tr.y = y
        if (!tr.moved && Math.hypot((x - tr.startX).toDouble(), (y - tr.startY).toDouble()) > slop) {
            tr.moved = true
            gestureMoved = true
        }
        when (route) {
            Route.PAD -> padMove(tr, dx, dy)
            Route.NATIVE -> nativeMove(tr)
            Route.EMULATED -> emuMove(tr, dx, dy)
        }
    }

    private fun finish(id: Int, x: Float, y: Float, t: Long, cancelled: Boolean) {
        val tr = tracks[id] ?: return
        tr.x = x
        tr.y = y
        val last = tracks.size == 1
        when (route) {
            Route.PAD -> padEnd(last, t, cancelled)
            Route.NATIVE -> nativeEnd(tr, cancelled)
            Route.EMULATED -> emuEnd(tr, last, t, cancelled)
        }
        tracks.remove(id)
        usedSlots = usedSlots and (1 shl tr.slot).inv()
        if (tracks.isEmpty()) endGesture()
    }

    private fun allocSlot(): Int {
        for (i in 0 until 10) {
            if ((usedSlots and (1 shl i)) == 0) {
                usedSlots = usedSlots or (1 shl i)
                return i
            }
        }
        return -1
    }

    private fun beginGesture(t: Long) {
        route = if (pointerLocked || mode == Prefs.TOUCHPAD) Route.PAD
        else if (hostHasNativeTouch) Route.NATIVE
        else Route.EMULATED
        gestureStart = t
        gestureMax = 0
        gestureMoved = false
        scrolling = false
        scrollRemX = 0f
        scrollRemY = 0f
        longPressDone = false
    }

    private fun endGesture() {
        cancelLongPress()
        if (buttonHeld) {
            buttonHeld = false
            delegate?.engineEmit(InputEvent.MouseButton(0, false))
        }
    }

    private fun click(button: Int) {
        delegate?.engineEmit(InputEvent.MouseButton(button, true))
        delegate?.engineEmit(InputEvent.MouseButton(button, false))
    }

    private fun sendAbs(x: Float, y: Float) {
        val d = delegate ?: return
        val p = d.engineNormalise(x, y)
        d.engineEmit(InputEvent.MouseAbs(p[0], p[1]))
    }

    private fun clamp16(v: Float): Int = maxOf(-32000f, minOf(32000f, v)).toInt()

    /** dx / dy in dp. */
    private fun scrollBy(dx: Float, dy: Float) {
        if (!scrolling) {
            if (!gestureMoved) return
            scrolling = true
        }
        scrollRemX += dx
        scrollRemY += dy
        val ix = scrollRemX.toInt()
        val iy = scrollRemY.toInt()
        scrollRemX -= ix
        scrollRemY -= iy
        if (ix != 0 || iy != 0) delegate?.engineEmit(InputEvent.Scroll(clamp16(ix.toFloat()), clamp16(iy.toFloat())))
    }

    private fun releaseButton() {
        if (buttonHeld) {
            buttonHeld = false
            delegate?.engineEmit(InputEvent.MouseButton(0, false))
        }
    }

    // MARK: Touchpad

    private fun padDown() {
        if (tracks.size == 1) {
            // Tap, then touch down again quickly: hold the left button while dragging.
            if (gestureStart - lastTapEnd < TAP_DRAG_WINDOW) {
                buttonHeld = true
                delegate?.engineEmit(InputEvent.MouseButton(0, true))
            }
        } else {
            releaseButton()
        }
    }

    private fun padMove(tr: Track, dxPx: Float, dyPx: Float) {
        if (gestureMax >= 3) return
        val dx = dxPx / density
        val dy = dyPx / density
        val n = tracks.size
        if (n == 1 && gestureMax == 1) {
            var gain = LOCKED_GAIN
            if (!pointerLocked) {
                val ppp = delegate?.engineSourcePixelsPerDp() ?: 1f
                val base = minOf(3f, maxOf(0.5f, ppp * 0.6f))
                val accel = 1f + minOf(1.2f, tr.speed / (2200f * density))
                gain = base * accel
            }
            delegate?.engineMoveCursor(dx * gain, dy * gain)
        } else if (n == 2 && gestureMax == 2) {
            scrollBy(dx * 0.5f, dy * 0.5f)
        }
    }

    private fun padEnd(last: Boolean, ts: Long, cancelled: Boolean) {
        if (!last) return
        if (buttonHeld) {
            buttonHeld = false
            delegate?.engineEmit(InputEvent.MouseButton(0, false))
            lastTapEnd = if (gestureMoved || cancelled) -100000L else ts
            return
        }
        if (cancelled || gestureMoved || longPressDone) return
        val dur = ts - gestureStart
        if (gestureMax == 1 && dur < TAP_MAX) {
            click(0)
            lastTapEnd = ts
        } else if (gestureMax == 2 && dur < TWO_TAP_MAX) {
            click(1)
        }
    }

    // MARK: Touchscreen, emulated (host without native touch)

    private fun emuDown() {
        longPressDone = false
        if (tracks.size == 1) {
            scheduleLongPress()
        } else {
            cancelLongPress()
            releaseButton()
        }
    }

    private fun emuMove(tr: Track, dxPx: Float, dyPx: Float) {
        if (buttonHeld) {
            if (tracks.size == 1) sendAbs(tr.x, tr.y)
            return
        }
        if (tr.moved) cancelLongPress()
        if (gestureMax >= 3) return
        val n = tracks.size
        if (n == gestureMax) {
            val f = 1f / n / density
            scrollBy(dxPx * f, dyPx * f) // natural scrolling
        }
    }

    private fun emuEnd(tr: Track, last: Boolean, ts: Long, cancelled: Boolean) {
        if (!last) return
        cancelLongPress()
        if (buttonHeld) {
            buttonHeld = false
            sendAbs(tr.x, tr.y)
            delegate?.engineEmit(InputEvent.MouseButton(0, false))
            return
        }
        if (cancelled || gestureMoved) return
        val dur = ts - gestureStart
        if (gestureMax == 1 && dur < TAP_MAX) {
            sendAbs(tr.startX, tr.startY)
            click(0)
        } else if (gestureMax == 2 && dur < TWO_TAP_MAX) {
            click(1)
        }
    }

    private fun scheduleLongPress() {
        cancelLongPress()
        handler.postDelayed(longPressRunnable, LONG_PRESS)
    }

    private fun cancelLongPress() {
        handler.removeCallbacks(longPressRunnable)
    }

    private fun longPressFired() {
        if (route != Route.EMULATED || tracks.size != 1 || gestureMax != 1 || gestureMoved || buttonHeld) return
        val tr = tracks.values.firstOrNull() ?: return
        sendAbs(tr.x, tr.y)
        click(1) // touch and hold = right click
        longPressDone = true
    }

    // MARK: Touchscreen, native multi-touch

    private fun touchEvent(tr: Track, phase: Int): InputEvent {
        val p = delegate?.engineNormalise(tr.x, tr.y) ?: intArrayOf(0, 0)
        return InputEvent.Touch(tr.slot, phase, p[0], p[1])
    }

    // One finger = a real touch; holding still = the computer's own touch-and-hold. Everything goes straight through.
    private fun nativeDown(tr: Track) { delegate?.engineEmit(touchEvent(tr, 0)) }

    private fun nativeMove(tr: Track) { delegate?.engineEmit(touchEvent(tr, 1)) }

    private fun nativeEnd(tr: Track, cancelled: Boolean) { delegate?.engineEmit(touchEvent(tr, if (cancelled) 3 else 2)) }
}
