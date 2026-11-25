package com.lumen.client.android

import android.os.Handler
import android.os.Looper

/**
 * Batches relative pointer movement (touchpad mode and captured hardware mouse) and sends it every ~4 ms so the network
 * is not flooded. Main thread only.
 */
class InputCapture(private val client: StreamClient) {
    private var accDX = 0f
    private var accDY = 0f
    private var running = false

    /** True while the hardware mouse is captured (game mode). */
    var relativeMode = false
    private val handler = Handler(Looper.getMainLooper())

    private val tickRunnable = object : Runnable {
        override fun run() {
            if (!running) return
            flush()
            handler.postDelayed(this, 4)
        }
    }

    fun start() {
        if (running) return
        running = true
        handler.postDelayed(tickRunnable, 4)
    }

    fun stop() {
        running = false
        handler.removeCallbacks(tickRunnable)
        accDX = 0f
        accDY = 0f
    }

    fun addRelative(dx: Float, dy: Float) {
        accDX += dx
        accDY += dy
    }

    private fun flush() {
        val sx = accDX.toInt()   // toward zero; the sub-pixel remainder is kept
        val sy = accDY.toInt()
        accDX -= sx
        accDY -= sy
        if (sx != 0 || sy != 0) {
            client.sendInput(
                InputEvent.MouseRel(
                    maxOf(-32000, minOf(32000, sx)),
                    maxOf(-32000, minOf(32000, sy))
                )
            )
        }
    }
}
