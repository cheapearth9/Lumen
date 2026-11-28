package com.lumen.client.android

import android.content.Context
import android.content.SharedPreferences
import android.hardware.display.DisplayManager
import android.util.Base64
import android.util.DisplayMetrics
import android.view.Display
import org.json.JSONArray
import org.json.JSONObject

class RecentHost(val name: String, val address: String, val key: String)

/** App-private preferences, recent hosts and the per-host pairing secrets. */
object Prefs {
    private lateinit var sp: SharedPreferences
    private lateinit var pairSp: SharedPreferences

    fun init(ctx: Context) {
        sp = ctx.applicationContext.getSharedPreferences("lumen", Context.MODE_PRIVATE)
        pairSp = ctx.applicationContext.getSharedPreferences("lumen_pairing", Context.MODE_PRIVATE)
    }

    const val TOUCHPAD = "touchpad"
    const val TOUCHSCREEN = "touchscreen"

    var touchMode: String
        get() = sp.getString("touchMode", TOUCHSCREEN) ?: TOUCHSCREEN
        set(v) { sp.edit().putString("touchMode", v).apply() }

    /** 0 auto, 1 portrait, 2 landscape. */
    var orientation: Int
        get() = sp.getInt("orientation", 0)
        set(v) { sp.edit().putInt("orientation", v).apply() }

    /** Applies the orientation choice to an activity; the stream defaults to landscape when set to auto. */
    fun applyOrientation(a: android.app.Activity, stream: Boolean) {
        a.requestedOrientation = when (orientation) {
            1 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            2 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            else -> if (stream) android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                    else android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }
    }

    var extend: Boolean
        get() = sp.getBoolean("extendMode", false)
        set(v) { sp.edit().putBoolean("extendMode", v).apply() }

    var audioOn: Boolean
        get() = sp.getBoolean("audioOn", true)
        set(v) { sp.edit().putBoolean("audioOn", v).apply() }

    var fps: Int
        get() = sp.getInt("fps", 60)
        set(v) { sp.edit().putInt("fps", v).apply() }

    var mbpsCable: Int
        get() = sp.getInt("mbpsCable", 100)
        set(v) { sp.edit().putInt("mbpsCable", v).apply() }

    var mbpsWifi: Int
        get() = sp.getInt("mbpsWifi", 40)
        set(v) { sp.edit().putInt("mbpsWifi", v).apply() }

    /** 0 full screen (device native), 1 16:9 1080p, 2 custom. */
    var resMode: Int
        get() = sp.getInt("resMode", 0)
        set(v) { sp.edit().putInt("resMode", v).apply() }

    var resW: Int
        get() = sp.getInt("resW", 1920)
        set(v) { sp.edit().putInt("resW", v).apply() }

    var resH: Int
        get() = sp.getInt("resH", 1080)
        set(v) { sp.edit().putInt("resH", v).apply() }

    var controllersOn: Boolean
        get() = sp.getBoolean("controllersOn", true)
        set(v) { sp.edit().putBoolean("controllersOn", v).apply() }

    var keyboardAuto: Boolean
        get() = sp.getBoolean("keyboardAuto", false)
        set(v) { sp.edit().putBoolean("keyboardAuto", v).apply() }

    var manualHost: String
        get() = sp.getString("manualHost", "") ?: ""
        set(v) { sp.edit().putString("manualHost", v).apply() }

    @Suppress("DEPRECATION")
    fun requestedResolution(ctx: Context): Pair<Int, Int> {
        val (w, h) = requestedLandscape(ctx)
        val portrait = when (orientation) {
            1 -> true
            2 -> false
            else -> ctx.resources.displayMetrics.let { it.heightPixels > it.widthPixels }
        }
        return if (portrait) Pair(minOf(w, h), maxOf(w, h)) else Pair(maxOf(w, h), minOf(w, h))
    }

    @Suppress("DEPRECATION")
    private fun requestedLandscape(ctx: Context): Pair<Int, Int> {
        when (resMode) {
            1 -> return Pair(1920, 1080)
            2 -> {
                val w = minOf(3840, maxOf(640, resW)) and 1.inv()
                val h = minOf(2160, maxOf(360, resH)) and 1.inv()
                return Pair(w, h)
            }
        }
        val dm = DisplayMetrics()
        try {
            val d = (ctx.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).getDisplay(Display.DEFAULT_DISPLAY)
            d.getRealMetrics(dm)
        } catch (e: Exception) {
            dm.widthPixels = 1920
            dm.heightPixels = 1080
        }
        val big = maxOf(dm.widthPixels, dm.heightPixels)
        val small = minOf(dm.widthPixels, dm.heightPixels)
        return Pair(minOf(3840, big) and 1.inv(), minOf(2160, small) and 1.inv())
    }

    fun helloInfo(ctx: Context): HelloInfo {
        val (w, h) = requestedResolution(ctx)
        var flags = 0
        if (audioOn) flags = flags or HelloInfo.FLAG_AUDIO
        if (extend) flags = flags or HelloInfo.FLAG_EXTEND
        if (!VideoDecoder.hasHevcDecoder()) flags = flags or HelloInfo.FLAG_H264
        return HelloInfo(w, h, minOf(255, maxOf(1, fps)), minOf(80, maxOf(8, mbpsWifi)), flags)
    }

    // Recent hosts

    fun recents(): List<RecentHost> {
        val s = sp.getString("recents", null) ?: return emptyList()
        return try {
            val a = JSONArray(s)
            val out = ArrayList<RecentHost>()
            for (i in 0 until a.length()) {
                val o = a.getJSONObject(i)
                out.add(RecentHost(o.getString("name"), o.getString("address"), o.getString("key")))
            }
            out
        } catch (e: Exception) {
            emptyList()
        }
    }

    private fun saveRecents(list: List<RecentHost>) {
        val a = JSONArray()
        for (r in list) {
            val o = JSONObject()
            o.put("name", r.name)
            o.put("address", r.address)
            o.put("key", r.key)
            a.put(o)
        }
        sp.edit().putString("recents", a.toString()).apply()
    }

    fun addRecent(h: RecentHost) {
        val list = ArrayList<RecentHost>()
        list.add(h)
        for (r in recents()) if (r.address != h.address) list.add(r)
        saveRecents(if (list.size > 5) list.subList(0, 5) else list)
    }

    fun removeRecent(address: String) {
        saveRecents(recents().filter { it.address != address })
    }

    // Pairing secrets (app-private storage, one per host key)

    fun loadPairingKey(hostKey: String): ByteArray? {
        val s = pairSp.getString("k." + hostKey, null) ?: return null
        return try {
            Base64.decode(s, Base64.NO_WRAP)
        } catch (e: Exception) {
            null
        }
    }

    fun savePairingKey(hostKey: String, secret: ByteArray) {
        pairSp.edit().putString("k." + hostKey, Base64.encodeToString(secret, Base64.NO_WRAP)).apply()
    }

    fun deletePairingKey(hostKey: String) {
        pairSp.edit().remove("k." + hostKey).apply()
    }
}
