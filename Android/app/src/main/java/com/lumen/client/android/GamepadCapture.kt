package com.lumen.client.android

import android.content.Context
import android.hardware.input.InputManager
import android.os.Handler
import android.os.Looper
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent

/**
 * Forwards game controllers (Xbox, PlayStation, Switch Pro, generic) to the computer, where they appear as virtual
 * Xbox 360 controllers. Main thread only. The full state is re-sent every 250 ms so a lost datagram heals.
 */
class GamepadCapture(private val ctx: Context, private val client: StreamClient) : InputManager.InputDeviceListener {
    private class Pad(val deviceId: Int, val slot: Int) {
        var buttons = 0
        var dpadKey = 0
        var dpadHat = 0
        var lt = 0
        var rt = 0
        var lx = 0
        var ly = 0
        var rx = 0
        var ry = 0
    }

    var onToast: ((String) -> Unit)? = null

    private val pads = HashMap<Int, Pad>()
    private val handler = Handler(Looper.getMainLooper())
    private var running = false
    private val refresh = object : Runnable {
        override fun run() {
            if (!running) return
            for (p in pads.values) send(p)
            handler.postDelayed(this, 250)
        }
    }

    fun start() {
        if (running) return
        running = true
        val im = ctx.getSystemService(Context.INPUT_SERVICE) as InputManager
        im.registerInputDeviceListener(this, handler)
        for (id in InputDevice.getDeviceIds()) {
            val d = InputDevice.getDevice(id)
            if (isGamepad(d)) padFor(id)
        }
        handler.postDelayed(refresh, 250)
    }

    fun stop() {
        if (!running) return
        running = false
        handler.removeCallbacks(refresh)
        val im = ctx.getSystemService(Context.INPUT_SERVICE) as InputManager
        im.unregisterInputDeviceListener(this)
        for (p in pads.values) client.sendInput(InputEvent.GamepadPresence(p.slot, false))
        pads.clear()
    }

    override fun onInputDeviceAdded(deviceId: Int) {
        if (isGamepad(InputDevice.getDevice(deviceId))) padFor(deviceId)
    }

    override fun onInputDeviceChanged(deviceId: Int) {}

    override fun onInputDeviceRemoved(deviceId: Int) {
        val p = pads.remove(deviceId) ?: return
        client.sendInput(InputEvent.GamepadPresence(p.slot, false))
    }

    private fun isGamepad(d: InputDevice?): Boolean {
        if (d == null || d.isVirtual) return false
        val s = d.sources
        return (s and InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD ||
            (s and InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK
    }

    private fun padFor(deviceId: Int): Pad? {
        val existing = pads[deviceId]
        if (existing != null) return existing
        val dev = InputDevice.getDevice(deviceId)
        if (!isGamepad(dev)) return null
        var slot = -1
        for (s in 0 until 4) {
            var used = false
            for (p in pads.values) if (p.slot == s) used = true
            if (!used) { slot = s; break }
        }
        if (slot < 0) return null
        val pad = Pad(deviceId, slot)
        pads[deviceId] = pad
        client.sendInput(InputEvent.GamepadPresence(slot, true))
        onToast?.invoke("Controller connected: " + (dev?.name ?: "gamepad"))
        send(pad)
        return pad
    }

    private fun buttonBit(code: Int): Int = when (code) {
        KeyEvent.KEYCODE_BUTTON_A -> 0x1000
        KeyEvent.KEYCODE_BUTTON_B -> 0x2000
        KeyEvent.KEYCODE_BUTTON_X -> 0x4000
        KeyEvent.KEYCODE_BUTTON_Y -> 0x8000
        KeyEvent.KEYCODE_BUTTON_L1 -> 0x100
        KeyEvent.KEYCODE_BUTTON_R1 -> 0x200
        KeyEvent.KEYCODE_BUTTON_THUMBL -> 0x40
        KeyEvent.KEYCODE_BUTTON_THUMBR -> 0x80
        KeyEvent.KEYCODE_BUTTON_START -> 0x10
        KeyEvent.KEYCODE_BUTTON_SELECT -> 0x20
        KeyEvent.KEYCODE_BUTTON_MODE -> 0x400
        else -> 0
    }

    private fun dpadBit(code: Int): Int = when (code) {
        KeyEvent.KEYCODE_DPAD_UP -> 1
        KeyEvent.KEYCODE_DPAD_DOWN -> 2
        KeyEvent.KEYCODE_DPAD_LEFT -> 4
        KeyEvent.KEYCODE_DPAD_RIGHT -> 8
        else -> 0
    }

    /** A key event from a gamepad. Returns true when it was handled. */
    fun onKey(ev: KeyEvent): Boolean {
        if (!Prefs.controllersOn) return false
        val code = ev.keyCode
        val bit = buttonBit(code)
        val dbit = dpadBit(code)
        val isTrigger = code == KeyEvent.KEYCODE_BUTTON_L2 || code == KeyEvent.KEYCODE_BUTTON_R2
        if (bit == 0 && dbit == 0 && !isTrigger) return false
        val pad = padFor(ev.deviceId) ?: return false
        if (ev.repeatCount > 0) return true
        val down = ev.action == KeyEvent.ACTION_DOWN
        if (bit != 0) pad.buttons = if (down) pad.buttons or bit else pad.buttons and bit.inv()
        if (dbit != 0) pad.dpadKey = if (down) pad.dpadKey or dbit else pad.dpadKey and dbit.inv()
        if (code == KeyEvent.KEYCODE_BUTTON_L2) pad.lt = if (down) 255 else 0
        if (code == KeyEvent.KEYCODE_BUTTON_R2) pad.rt = if (down) 255 else 0
        send(pad)
        return true
    }

    private fun has(dev: InputDevice?, axis: Int, source: Int): Boolean =
        dev != null && dev.getMotionRange(axis, source) != null

    private fun axisValue(ev: MotionEvent, dev: InputDevice?, axis: Int): Float {
        val v = ev.getAxisValue(axis)
        val flat = dev?.getMotionRange(axis, ev.source)?.flat ?: 0.1f
        val dead = maxOf(flat, 0.08f)
        return if (Math.abs(v) <= dead) 0f else v
    }

    private fun axis16(v: Float): Int = (maxOf(-1f, minOf(1f, v)) * 32767f).toInt()
    private fun trig8(v: Float): Int = (maxOf(0f, minOf(1f, v)) * 255f).toInt()

    /** A joystick motion event. Returns true when it was handled. */
    fun onMotion(ev: MotionEvent): Boolean {
        if (!Prefs.controllersOn) return false
        val pad = padFor(ev.deviceId) ?: return false
        val dev = ev.device
        val src = ev.source

        val lsx = axisValue(ev, dev, MotionEvent.AXIS_X)
        val lsy = axisValue(ev, dev, MotionEvent.AXIS_Y)

        val hasLTrig = has(dev, MotionEvent.AXIS_LTRIGGER, src) || has(dev, MotionEvent.AXIS_BRAKE, src)
        val rsx: Float
        val rsy: Float
        var l = 0f
        var r = 0f
        if (hasLTrig || !(has(dev, MotionEvent.AXIS_RX, src) && has(dev, MotionEvent.AXIS_Z, src) && has(dev, MotionEvent.AXIS_RZ, src))) {
            // Xbox-style: right stick on Z / RZ, triggers on LTRIGGER / RTRIGGER (or BRAKE / GAS)
            if (has(dev, MotionEvent.AXIS_Z, src)) {
                rsx = axisValue(ev, dev, MotionEvent.AXIS_Z)
                rsy = axisValue(ev, dev, MotionEvent.AXIS_RZ)
            } else {
                rsx = axisValue(ev, dev, MotionEvent.AXIS_RX)
                rsy = axisValue(ev, dev, MotionEvent.AXIS_RY)
            }
            l = maxOf(ev.getAxisValue(MotionEvent.AXIS_LTRIGGER), ev.getAxisValue(MotionEvent.AXIS_BRAKE))
            r = maxOf(ev.getAxisValue(MotionEvent.AXIS_RTRIGGER), ev.getAxisValue(MotionEvent.AXIS_GAS))
        } else {
            // legacy Xbox 360 layout: right stick on RX / RY, triggers on Z / RZ
            rsx = axisValue(ev, dev, MotionEvent.AXIS_RX)
            rsy = axisValue(ev, dev, MotionEvent.AXIS_RY)
            l = ev.getAxisValue(MotionEvent.AXIS_Z)
            r = ev.getAxisValue(MotionEvent.AXIS_RZ)
        }
        if (l > 0f || r > 0f || pad.lt > 0 || pad.rt > 0) {
            // keep a key-driven full press (L2 / R2 buttons) unless the axes report something
            if (l > 0f || pad.lt != 255) pad.lt = trig8(l)
            if (r > 0f || pad.rt != 255) pad.rt = trig8(r)
        }

        val hx = ev.getAxisValue(MotionEvent.AXIS_HAT_X)
        val hy = ev.getAxisValue(MotionEvent.AXIS_HAT_Y)
        var hat = 0
        if (hy < -0.5f) hat = hat or 1
        if (hy > 0.5f) hat = hat or 2
        if (hx < -0.5f) hat = hat or 4
        if (hx > 0.5f) hat = hat or 8
        pad.dpadHat = hat

        pad.lx = axis16(lsx)
        pad.ly = axis16(-lsy) // XInput: stick up = positive
        pad.rx = axis16(rsx)
        pad.ry = axis16(-rsy)
        send(pad)
        return true
    }

    private fun send(p: Pad) {
        if (!Prefs.controllersOn) return
        val b = (p.buttons or p.dpadKey or p.dpadHat) and 0xFFFF
        client.sendInput(InputEvent.Gamepad(p.slot, b, p.lt, p.rt, p.lx, p.ly, p.rx, p.ry))
    }
}
