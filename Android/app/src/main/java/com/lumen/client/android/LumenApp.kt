package com.lumen.client.android

import android.app.Application

class LumenApp : Application() {
    companion object {
        lateinit var client: StreamClient
            private set
    }

    override fun onCreate() {
        super.onCreate()
        Prefs.init(this)
        client = StreamClient(this)
    }
}
