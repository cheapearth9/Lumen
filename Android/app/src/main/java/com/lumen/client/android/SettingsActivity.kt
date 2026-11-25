package com.lumen.client.android

import android.app.Activity
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Switch
import android.widget.TextView

/** Settings: touch mode, mirror / extend, sound, resolution, frame rate, bitrate, controllers, keyboard. */
class SettingsActivity : Activity() {
    private lateinit var customRow: LinearLayout
    private lateinit var widthField: EditText
    private lateinit var heightField: EditText

    private fun radios(options: Array<String>, selected: Int, onSelect: (Int) -> Unit): RadioGroup {
        val g = RadioGroup(this)
        g.orientation = RadioGroup.HORIZONTAL
        val ids = IntArray(options.size)
        for (i in options.indices) {
            val b = RadioButton(this)
            b.text = options[i]
            b.setTextColor(Ui.color(this, R.color.lumen_text))
            val id = View.generateViewId()
            b.id = id
            ids[i] = id
            g.addView(b, RadioGroup.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }
        if (selected >= 0 && selected < ids.size) g.check(ids[selected])
        g.setOnCheckedChangeListener { _, checkedId ->
            for (i in ids.indices) if (ids[i] == checkedId) onSelect(i)
        }
        return g
    }

    private fun switchRow(title: String, on: Boolean, onChange: (Boolean) -> Unit): Switch {
        val s = Switch(this)
        s.text = title
        s.textSize = 16f
        s.setTextColor(Ui.color(this, R.color.lumen_text))
        s.isChecked = on
        s.setOnCheckedChangeListener { _, checked -> onChange(checked) }
        s.setPadding(0, Ui.dp(this, 8), 0, Ui.dp(this, 8))
        return s
    }

    private fun note(text: String): TextView {
        val t = Ui.text(this, text, 13f, R.color.lumen_text2)
        t.setPadding(0, Ui.dp(this, 6), 0, 0)
        return t
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val scroll = ScrollView(this)
        scroll.isFillViewport = true
        val width = minOf(resources.displayMetrics.widthPixels, Ui.dp(this, 560))
        val col = LinearLayout(this)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(Ui.dp(this, 16), Ui.dp(this, 16), Ui.dp(this, 16), Ui.dp(this, 32))
        scroll.addView(col, FrameLayout.LayoutParams(width, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER_HORIZONTAL))

        val bar = LinearLayout(this)
        bar.orientation = LinearLayout.HORIZONTAL
        bar.gravity = Gravity.CENTER_VERTICAL
        bar.addView(Ui.text(this, "Settings", 30f, R.color.lumen_text, true), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val done = Ui.text(this, "Done", 15f, R.color.lumen_accent, true)
        done.setPadding(Ui.dp(this, 12), Ui.dp(this, 12), Ui.dp(this, 4), Ui.dp(this, 12))
        done.setOnClickListener { finish() }
        bar.addView(done)
        col.addView(bar)

        // Touch
        col.addView(Ui.sectionTitle(this, "Touch"))
        val touchCard = Ui.card(this)
        touchCard.addView(radios(arrayOf("Touchpad", "Touchscreen"), if (Prefs.touchMode == Prefs.TOUCHPAD) 0 else 1) { i ->
            Prefs.touchMode = if (i == 0) Prefs.TOUCHPAD else Prefs.TOUCHSCREEN
        })
        touchCard.addView(note("Touchpad moves the cursor like a laptop trackpad. Touchscreen sends your touches to the computer where you tap (native multi-touch on Windows and Linux)."))
        col.addView(touchCard)

        // Display
        col.addView(Ui.sectionTitle(this, "Display"))
        val dispCard = Ui.card(this)
        dispCard.addView(radios(arrayOf("Mirror", "Extend"), if (Prefs.extend) 1 else 0) { i -> Prefs.extend = (i == 1) })
        dispCard.addView(note("Mirror shows the computer's existing screen. Extend adds a new screen to the computer, sized for this device."))
        dispCard.addView(switchRow("Sound", Prefs.audioOn) { v -> Prefs.audioOn = v })
        col.addView(dispCard)

        // Resolution
        col.addView(Ui.sectionTitle(this, "Resolution"))
        val resCard = Ui.card(this)
        customRow = LinearLayout(this)
        customRow.orientation = LinearLayout.HORIZONTAL
        customRow.gravity = Gravity.CENTER_VERTICAL
        widthField = EditText(this)
        widthField.inputType = InputType.TYPE_CLASS_NUMBER
        widthField.hint = "Width"
        widthField.setText(Prefs.resW.toString())
        heightField = EditText(this)
        heightField.inputType = InputType.TYPE_CLASS_NUMBER
        heightField.hint = "Height"
        heightField.setText(Prefs.resH.toString())
        customRow.addView(widthField, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        customRow.addView(Ui.text(this, "  x  ", 16f, R.color.lumen_text))
        customRow.addView(heightField, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        customRow.visibility = if (Prefs.resMode == 2) View.VISIBLE else View.GONE
        resCard.addView(radios(arrayOf("Full screen", "16:9 1080p", "Custom"), Prefs.resMode) { i ->
            Prefs.resMode = i
            customRow.visibility = if (i == 2) View.VISIBLE else View.GONE
        })
        resCard.addView(customRow)
        resCard.addView(note("Full screen uses this device's own resolution. Custom accepts 640x360 up to 3840x2160; the picture keeps the computer screen's shape inside it."))
        col.addView(resCard)

        // Quality
        col.addView(Ui.sectionTitle(this, "Quality"))
        val qCard = Ui.card(this)
        qCard.addView(Ui.text(this, "Frame rate", 14f, R.color.lumen_text2))
        val fpsValues = intArrayOf(30, 60, 120)
        var fpsSel = 1
        for (i in fpsValues.indices) if (fpsValues[i] == Prefs.fps) fpsSel = i
        qCard.addView(radios(arrayOf("30", "60", "120"), fpsSel) { i -> Prefs.fps = fpsValues[i] })
        val mbpsLabel = Ui.text(this, "Wi-Fi bitrate: " + Prefs.mbpsWifi + " Mbps", 14f, R.color.lumen_text2)
        mbpsLabel.setPadding(0, Ui.dp(this, 12), 0, 0)
        qCard.addView(mbpsLabel)
        val seek = SeekBar(this)
        seek.max = 72
        seek.progress = maxOf(8, minOf(80, Prefs.mbpsWifi)) - 8
        seek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar, progress: Int, fromUser: Boolean) {
                Prefs.mbpsWifi = progress + 8
                mbpsLabel.text = "Wi-Fi bitrate: " + (progress + 8) + " Mbps"
            }

            override fun onStartTrackingTouch(sb: SeekBar) {}

            override fun onStopTrackingTouch(sb: SeekBar) {}
        })
        qCard.addView(seek, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        col.addView(qCard)

        // Keyboard + controllers
        col.addView(Ui.sectionTitle(this, "Keyboard and controllers"))
        val kCard = Ui.card(this)
        kCard.addView(switchRow("Show the on-screen keyboard when streaming starts", Prefs.keyboardAuto) { v -> Prefs.keyboardAuto = v })
        kCard.addView(note("You can also open or close it from the menu while streaming. Hardware keyboards and mice work too."))
        kCard.addView(switchRow("Send controllers to the computer", Prefs.controllersOn) { v -> Prefs.controllersOn = v })
        kCard.addView(note("A Bluetooth or USB controller shows up as an Xbox 360 controller on the computer (Windows needs the ViGEmBus driver)."))
        col.addView(kCard)

        setContentView(scroll)
    }

    override fun onPause() {
        super.onPause()
        if (!::widthField.isInitialized) return
        val w = widthField.text.toString().trim().toIntOrNull()
        val h = heightField.text.toString().trim().toIntOrNull()
        if (w != null) Prefs.resW = w
        if (h != null) Prefs.resH = h
    }
}
