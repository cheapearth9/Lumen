package com.lumen.client.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.PopupMenu
import android.widget.TextView

/** Transparent view on top of the video that receives all touches, mouse and hover events. */
class TouchSurface(ctx: Context, private val owner: StreamActivity) : View(ctx) {
    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        owner.surfaceTouch(event)
        return true
    }

    override fun onGenericMotionEvent(event: MotionEvent): Boolean = owner.surfaceGeneric(event)
}

/** Immersive full-screen stream: video, input capture, small menu button and stats line. */
class StreamActivity : Activity(), TouchDelegate {
    private companion object {
        const val ID_INFO = 1
        const val ID_MIRROR = 2
        const val ID_EXTEND = 3
        const val ID_SOUND = 4
        const val ID_PAD = 5
        const val ID_SCREEN = 6
        const val ID_KEYBOARD = 7
        const val ID_CAPTURE = 8
        const val ID_DISCONNECT = 9
        const val ID_ORIENT_AUTO = 10
        const val ID_ORIENT_PORTRAIT = 11
        const val ID_ORIENT_LANDSCAPE = 12
    }

    private var ready = false
    private lateinit var client: StreamClient
    private lateinit var input: InputCapture
    private lateinit var engine: TouchEngine
    private lateinit var gamepads: GamepadCapture
    private lateinit var root: FrameLayout
    private lateinit var surfaceView: SurfaceView
    private lateinit var touchSurface: TouchSurface
    private lateinit var softKb: SoftKeyboardView
    private lateinit var statsLabel: TextView
    private lateinit var toastLabel: TextView
    private val handler = Handler(Looper.getMainLooper())
    private val hideToast = Runnable { toastLabel.visibility = View.GONE }

    private var locked = false
    private var kbShown = false
    private var lastButtons = 0
    private val held = HashSet<Int>()
    private var density = 1f
    private var lastLpW = -1
    private var lastLpH = -1

    private val listener = object : StreamClient.Listener {
        override fun onState(state: StreamClient.State, message: String) {
            if (state == StreamClient.State.IDLE || state == StreamClient.State.FAILED) finish()
        }

        override fun onStats(text: String) {
            statsLabel.text = text
        }

        override fun onToast(text: String) {
            showToast(text)
        }

        override fun onVideoSize(width: Int, height: Int) {
            updateVideoLayout()
        }

        override fun onHostInfo() {
            engine.hostHasNativeTouch = client.hostSupportsNativeTouch
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Prefs.applyOrientation(this, true)
        client = LumenApp.client
        if (client.state != StreamClient.State.STREAMING) {
            finish()
            return
        }
        ready = true
        density = resources.displayMetrics.density
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        input = InputCapture(client)
        engine = TouchEngine(density)
        engine.delegate = this
        engine.mode = Prefs.touchMode
        engine.hostHasNativeTouch = client.hostSupportsNativeTouch
        gamepads = GamepadCapture(this, client)
        gamepads.onToast = { t -> showToast(t) }

        root = FrameLayout(this)
        root.setBackgroundColor(0xFF000000.toInt())

        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                client.decoder.setSurface(holder.surface)
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

            override fun surfaceDestroyed(holder: SurfaceHolder) {
                client.decoder.setSurface(null)
            }
        })
        root.addView(surfaceView, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER))

        touchSurface = TouchSurface(this, this)
        root.addView(touchSurface, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))

        softKb = SoftKeyboardView(this)
        softKb.send = { e -> client.sendInput(e) }
        softKb.onKey = { ev -> handleKey(ev) }
        root.addView(softKb, FrameLayout.LayoutParams(1, 1))

        statsLabel = TextView(this)
        statsLabel.setTextColor(0xB3FFFFFF.toInt())
        statsLabel.textSize = 11f
        statsLabel.typeface = android.graphics.Typeface.MONOSPACE
        statsLabel.text = client.statsText
        val sl = FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.START)
        sl.leftMargin = Ui.dp(this, 12)
        sl.topMargin = Ui.dp(this, 8)
        root.addView(statsLabel, sl)

        val menuBtn = ImageButton(this)
        menuBtn.setImageResource(R.drawable.ic_more)
        menuBtn.setBackgroundResource(R.drawable.bg_round)
        menuBtn.contentDescription = "Menu"
        menuBtn.setOnClickListener { v -> showMenu(v) }
        val ml = FrameLayout.LayoutParams(Ui.dp(this, 44), Ui.dp(this, 44), Gravity.TOP or Gravity.END)
        ml.rightMargin = Ui.dp(this, 12)
        ml.topMargin = Ui.dp(this, 8)
        root.addView(menuBtn, ml)

        toastLabel = TextView(this)
        toastLabel.setTextColor(0xFFFFFFFF.toInt())
        toastLabel.textSize = 14f
        toastLabel.setBackgroundColor(0xB3000000.toInt())
        toastLabel.setPadding(Ui.dp(this, 14), Ui.dp(this, 8), Ui.dp(this, 14), Ui.dp(this, 8))
        toastLabel.visibility = View.GONE
        val tl = FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL)
        tl.bottomMargin = Ui.dp(this, 24)
        root.addView(toastLabel, tl)

        if (Build.VERSION.SDK_INT >= 26) {
            touchSurface.setOnCapturedPointerListener { _, e -> capturedEvent(e) }
            softKb.setOnCapturedPointerListener { _, e -> capturedEvent(e) }
        }

        root.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> updateVideoLayout() }
        setContentView(root)
        touchSurface.requestFocus()
        hideSystemUi()
        client.addListener(listener)
        if (Prefs.keyboardAuto) handler.post { setKeyboard(true) }
    }

    override fun onResume() {
        super.onResume()
        if (!ready) return
        hideSystemUi()
        engine.mode = Prefs.touchMode
        input.start()
        if (Prefs.controllersOn) gamepads.start()
    }

    override fun onPause() {
        super.onPause()
        if (!ready) return
        engine.cancelAll()
        syncButtons(0)
        for (c in held) client.sendInput(InputEvent.Key(c, false))
        held.clear()
        gamepads.stop()
        input.stop()
        if (locked && Build.VERSION.SDK_INT >= 26) touchSurface.releasePointerCapture()
        locked = false
    }

    /** Leaving the app ends the session (like Apollo/Sunshine): the computer drops back to its normal displays. */
    override fun onStop() {
        super.onStop()
        if (ready && !isChangingConfigurations) client.disconnect(10 * 60_000L)
    }

    override fun onDestroy() {
        super.onDestroy()
        if (!ready) return
        handler.removeCallbacksAndMessages(null)
        client.removeListener(listener)
        client.decoder.setSurface(null)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus && ready) hideSystemUi()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
    }

    @Suppress("DEPRECATION", "OVERRIDE_DEPRECATION")
    override fun onBackPressed() {
        AlertDialog.Builder(this)
            .setMessage("Disconnect from the computer?")
            .setPositiveButton("Disconnect") { _, _ -> client.disconnect() }
            .setNegativeButton("Stay", null)
            .show()
    }

    override fun onPointerCaptureChanged(hasCapture: Boolean) {
        super.onPointerCaptureChanged(hasCapture)
        if (!ready) return
        locked = hasCapture
        input.relativeMode = hasCapture
        engine.pointerLocked = hasCapture
        if (!hasCapture) syncButtons(0)
    }

    // MARK: Layout / geometry

    private fun videoRect(): FloatArray {
        val cw = root.width.toFloat()
        val ch = root.height.toFloat()
        if (cw <= 0f || ch <= 0f) return floatArrayOf(0f, 0f, 1f, 1f)
        val vw = maxOf(1, client.videoWidth).toFloat()
        val vh = maxOf(1, client.videoHeight).toFloat()
        val s = minOf(cw / vw, ch / vh)
        val w = vw * s
        val h = vh * s
        return floatArrayOf((cw - w) / 2f, (ch - h) / 2f, w, h)
    }

    private fun updateVideoLayout() {
        if (root.width <= 0 || root.height <= 0) return
        val r = videoRect()
        val w = maxOf(1, r[2].toInt())
        val h = maxOf(1, r[3].toInt())
        if (w == lastLpW && h == lastLpH) return
        lastLpW = w
        lastLpH = h
        val lp = FrameLayout.LayoutParams(w, h, Gravity.CENTER)
        surfaceView.layoutParams = lp
    }

    override fun engineEmit(e: InputEvent) {
        client.sendInput(e)
    }

    override fun engineMoveCursor(dx: Float, dy: Float) {
        input.addRelative(dx, dy)
    }

    override fun engineNormalise(x: Float, y: Float): IntArray {
        val r = videoRect()
        val nx = maxOf(0f, minOf(1f, (x - r[0]) / r[2]))
        val ny = maxOf(0f, minOf(1f, (y - r[1]) / r[3]))
        return intArrayOf((nx * 65535f).toInt(), (ny * 65535f).toInt())
    }

    override fun engineSourcePixelsPerDp(): Float {
        val r = videoRect()
        val widthDp = r[2] / density
        val src = if (client.hostSourceWidth > 0) client.hostSourceWidth else client.videoWidth
        if (widthDp <= 0f) return 1f
        return src.toFloat() / widthDp
    }

    // MARK: Touch / mouse

    fun surfaceTouch(ev: MotionEvent) {
        if (ev.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE) {
            mouseTouch(ev)
            return
        }
        engine.onTouchEvent(ev)
    }

    private fun sendAbs(x: Float, y: Float) {
        val p = engineNormalise(x, y)
        client.sendInput(InputEvent.MouseAbs(p[0], p[1]))
    }

    private fun mouseTouch(ev: MotionEvent) {
        val act = ev.actionMasked
        sendAbs(ev.x, ev.y)
        if (act == MotionEvent.ACTION_UP || act == MotionEvent.ACTION_CANCEL) syncButtons(0)
        else syncButtons(ev.buttonState)
    }

    private fun syncButtons(state: Int) {
        val masks = intArrayOf(MotionEvent.BUTTON_PRIMARY, MotionEvent.BUTTON_SECONDARY, MotionEvent.BUTTON_TERTIARY)
        for (i in 0 until 3) {
            val was = (lastButtons and masks[i]) != 0
            val now = (state and masks[i]) != 0
            if (was != now) client.sendInput(InputEvent.MouseButton(i, now))
        }
        lastButtons = state
    }

    private fun sendScroll(ev: MotionEvent) {
        val vs = ev.getAxisValue(MotionEvent.AXIS_VSCROLL)
        val hs = ev.getAxisValue(MotionEvent.AXIS_HSCROLL)
        val dy = maxOf(-2000f, minOf(2000f, vs * 24f)).toInt()
        val dx = maxOf(-2000f, minOf(2000f, -hs * 24f)).toInt()
        if (dx != 0 || dy != 0) client.sendInput(InputEvent.Scroll(dx, dy))
    }

    fun surfaceGeneric(ev: MotionEvent): Boolean {
        if (!ev.isFromSource(InputDevice.SOURCE_CLASS_POINTER)) return false
        val act = ev.actionMasked
        if (act == MotionEvent.ACTION_HOVER_MOVE || act == MotionEvent.ACTION_HOVER_ENTER) {
            if (!locked) sendAbs(ev.x, ev.y)
            return true
        }
        if (act == MotionEvent.ACTION_SCROLL) {
            sendScroll(ev)
            return true
        }
        return false
    }

    private fun capturedEvent(ev: MotionEvent): Boolean {
        val act = ev.actionMasked
        if (act == MotionEvent.ACTION_SCROLL) {
            sendScroll(ev)
            return true
        }
        if (ev.isFromSource(InputDevice.SOURCE_TOUCHPAD) && !ev.isFromSource(InputDevice.SOURCE_MOUSE_RELATIVE)) return true
        if (act == MotionEvent.ACTION_MOVE) {
            for (i in 0 until ev.historySize) input.addRelative(ev.getHistoricalX(i), ev.getHistoricalY(i))
            input.addRelative(ev.x, ev.y)
        }
        if (act == MotionEvent.ACTION_UP || act == MotionEvent.ACTION_CANCEL) syncButtons(0)
        else syncButtons(ev.buttonState)
        return true
    }

    // MARK: Keyboard

    override fun dispatchKeyEvent(ev: KeyEvent): Boolean {
        if (!ready) return super.dispatchKeyEvent(ev)
        val src = ev.source
        val isPad = (src and InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD ||
            (src and InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK
        if (isPad) {
            if (gamepads.onKey(ev)) return true
            return super.dispatchKeyEvent(ev)
        }
        if (ev.keyCode == KeyEvent.KEYCODE_BACK) {
            // a secondary mouse click is delivered as Back: it is a right click, not navigation
            if (ev.isFromSource(InputDevice.SOURCE_MOUSE)) return true
            return super.dispatchKeyEvent(ev)
        }
        if (handleKey(ev)) return true
        return super.dispatchKeyEvent(ev)
    }

    override fun dispatchGenericMotionEvent(ev: MotionEvent): Boolean {
        if (ready && ev.isFromSource(InputDevice.SOURCE_JOYSTICK) && ev.actionMasked == MotionEvent.ACTION_MOVE) {
            if (gamepads.onMotion(ev)) return true
        }
        return super.dispatchGenericMotionEvent(ev)
    }

    private fun handleKey(ev: KeyEvent): Boolean {
        val mac = KeyMap.table[ev.keyCode] ?: return false
        val down = ev.action == KeyEvent.ACTION_DOWN
        if (!down && ev.action != KeyEvent.ACTION_UP) return false
        if (down && ev.repeatCount > 0) return true
        if (ev.keyCode == KeyEvent.KEYCODE_L && ev.isCtrlPressed && ev.isAltPressed) {
            if (down) toggleLock()
            return true
        }
        if (down) held.add(mac) else held.remove(mac)
        client.sendInput(InputEvent.Key(mac, down))
        return true
    }

    private fun setKeyboard(show: Boolean) {
        val imm = getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        if (show) {
            softKb.requestFocus()
            imm.showSoftInput(softKb, 0)
        } else {
            imm.hideSoftInputFromWindow(softKb.windowToken, 0)
            touchSurface.requestFocus()
        }
        kbShown = show
    }

    private fun toggleLock() {
        if (Build.VERSION.SDK_INT < 26) {
            showToast("Mouse capture needs Android 8 or newer")
            return
        }
        if (locked) {
            touchSurface.releasePointerCapture()
        } else {
            touchSurface.requestFocus()
            touchSurface.requestPointerCapture()
        }
    }

    // MARK: Menu / toast

    private fun showToast(text: String) {
        toastLabel.text = text
        toastLabel.visibility = View.VISIBLE
        handler.removeCallbacks(hideToast)
        handler.postDelayed(hideToast, 4000)
    }

    private fun showMenu(anchor: View) {
        val pm = PopupMenu(this, anchor)
        val m = pm.menu
        val info = m.add(0, ID_INFO, 0, "Computer: " + client.hostModeText)
        info.isEnabled = false
        val mirror = m.add(0, ID_MIRROR, 1, "Mirror")
        mirror.isCheckable = true
        mirror.isChecked = !Prefs.extend
        val extend = m.add(0, ID_EXTEND, 2, "Extend")
        extend.isCheckable = true
        extend.isChecked = Prefs.extend
        val sound = m.add(0, ID_SOUND, 3, if (client.hostSupportsAudio) "Sound" else "Sound (not available on this computer)")
        sound.isCheckable = true
        sound.isChecked = Prefs.audioOn
        sound.isEnabled = client.hostSupportsAudio
        val pad = m.add(0, ID_PAD, 4, "Touchpad")
        pad.isCheckable = true
        pad.isChecked = Prefs.touchMode == Prefs.TOUCHPAD
        val screen = m.add(0, ID_SCREEN, 5, "Touchscreen")
        screen.isCheckable = true
        screen.isChecked = Prefs.touchMode == Prefs.TOUCHSCREEN
        m.add(0, ID_KEYBOARD, 6, if (kbShown) "Hide keyboard" else "Show keyboard")
        if (Build.VERSION.SDK_INT >= 26) {
            m.add(0, ID_CAPTURE, 7, if (locked) "Release mouse (Ctrl+Alt+L)" else "Capture mouse for games (Ctrl+Alt+L)")
        }
        val oa = m.add(1, ID_ORIENT_AUTO, 8, "Auto-rotate"); oa.isCheckable = true; oa.isChecked = Prefs.orientation == 0
        val op = m.add(1, ID_ORIENT_PORTRAIT, 8, "Portrait"); op.isCheckable = true; op.isChecked = Prefs.orientation == 1
        val ol = m.add(1, ID_ORIENT_LANDSCAPE, 8, "Landscape"); ol.isCheckable = true; ol.isChecked = Prefs.orientation == 2
        m.add(0, ID_DISCONNECT, 9, "Disconnect")
        pm.setOnMenuItemClickListener { item ->
            when (item.itemId) {
                ID_MIRROR -> client.setExtend(false)
                ID_EXTEND -> client.setExtend(true)
                ID_SOUND -> client.setAudio(!Prefs.audioOn)
                ID_PAD -> {
                    Prefs.touchMode = Prefs.TOUCHPAD
                    engine.mode = Prefs.TOUCHPAD
                }
                ID_SCREEN -> {
                    Prefs.touchMode = Prefs.TOUCHSCREEN
                    engine.mode = Prefs.TOUCHSCREEN
                }
                ID_KEYBOARD -> setKeyboard(!kbShown)
                ID_CAPTURE -> toggleLock()
                ID_ORIENT_AUTO -> { Prefs.orientation = 0; Prefs.applyOrientation(this, true); client.reconnectForResolution(this) }
                ID_ORIENT_PORTRAIT -> { Prefs.orientation = 1; Prefs.applyOrientation(this, true); client.reconnectForResolution(this) }
                ID_ORIENT_LANDSCAPE -> { Prefs.orientation = 2; Prefs.applyOrientation(this, true); client.reconnectForResolution(this) }
                ID_DISCONNECT -> client.disconnect()
            }
            true
        }
        pm.show()
    }
}
