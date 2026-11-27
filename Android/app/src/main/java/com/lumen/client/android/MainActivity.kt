package com.lumen.client.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/** Connect screen: computers found on the network, recent computers, manual IP entry. */
class MainActivity : Activity() {
    private lateinit var client: StreamClient
    private lateinit var hostsBox: LinearLayout
    private lateinit var recentsBox: LinearLayout
    private lateinit var recentsTitle: TextView
    private lateinit var searchingRow: LinearLayout
    private lateinit var statusRow: LinearLayout
    private lateinit var statusText: TextView
    private lateinit var statusProgress: ProgressBar
    private lateinit var statusCancel: Button
    private lateinit var ipField: EditText
    private var pairingDialog: AlertDialog? = null
    private var launched = false

    private val listener = object : StreamClient.Listener {
        override fun onState(state: StreamClient.State, message: String) {
            applyState()
            if (state == StreamClient.State.STREAMING) openStream()
        }

        override fun onHosts(hosts: List<DiscoveredHost>) {
            renderHosts(hosts)
        }

        override fun onPairing(hostName: String, code: String?) {
            showPairing(hostName, code)
        }

        override fun onToast(text: String) {
            Toast.makeText(this@MainActivity, text, Toast.LENGTH_SHORT).show()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        client = LumenApp.client
        Prefs.applyOrientation(this, false)
        buildUi()
    }

    private fun buildUi() {
        val scroll = ScrollView(this)
        scroll.isFillViewport = true
        val width = minOf(resources.displayMetrics.widthPixels, Ui.dp(this, 560))
        val col = LinearLayout(this)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(Ui.dp(this, 16), Ui.dp(this, 16), Ui.dp(this, 16), Ui.dp(this, 32))
        scroll.addView(col, FrameLayout.LayoutParams(width, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER_HORIZONTAL))

        // Title bar
        val bar = LinearLayout(this)
        bar.orientation = LinearLayout.HORIZONTAL
        bar.gravity = Gravity.CENTER_VERTICAL
        val title = Ui.text(this, "Lumen", 30f, R.color.lumen_text, true)
        bar.addView(title, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val refresh = Ui.text(this, "Refresh", 15f, R.color.lumen_accent, true)
        refresh.setPadding(Ui.dp(this, 12), Ui.dp(this, 12), Ui.dp(this, 12), Ui.dp(this, 12))
        refresh.setOnClickListener { client.rescanNow() }
        bar.addView(refresh)
        val settings = Ui.text(this, "Settings", 15f, R.color.lumen_accent, true)
        settings.setPadding(Ui.dp(this, 12), Ui.dp(this, 12), Ui.dp(this, 4), Ui.dp(this, 12))
        settings.setOnClickListener { startActivity(Intent(this, SettingsActivity::class.java)) }
        bar.addView(settings)
        col.addView(bar)

        // Status (connecting / failed)
        statusRow = LinearLayout(this)
        statusRow.orientation = LinearLayout.HORIZONTAL
        statusRow.gravity = Gravity.CENTER_VERTICAL
        statusRow.background = Ui.cardBackground(this, false)
        statusRow.setPadding(Ui.dp(this, 16), Ui.dp(this, 12), Ui.dp(this, 8), Ui.dp(this, 12))
        statusProgress = ProgressBar(this)
        statusRow.addView(statusProgress, LinearLayout.LayoutParams(Ui.dp(this, 28), Ui.dp(this, 28)))
        statusText = Ui.text(this, "", 15f, R.color.lumen_text)
        val stLp = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
        stLp.leftMargin = Ui.dp(this, 12)
        statusRow.addView(statusText, stLp)
        statusCancel = Button(this)
        statusCancel.text = "Cancel"
        statusCancel.setOnClickListener { client.disconnect() }
        statusRow.addView(statusCancel)
        statusRow.visibility = View.GONE
        val srLp = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        srLp.topMargin = Ui.dp(this, 8)
        col.addView(statusRow, srLp)

        // Found computers
        col.addView(Ui.sectionTitle(this, "Computers on your network"))
        searchingRow = LinearLayout(this)
        searchingRow.orientation = LinearLayout.HORIZONTAL
        searchingRow.gravity = Gravity.CENTER_VERTICAL
        searchingRow.setPadding(Ui.dp(this, 8), Ui.dp(this, 8), 0, Ui.dp(this, 8))
        searchingRow.addView(ProgressBar(this), LinearLayout.LayoutParams(Ui.dp(this, 24), Ui.dp(this, 24)))
        val sTxt = Ui.text(this, "Searching…", 15f, R.color.lumen_text2)
        sTxt.setPadding(Ui.dp(this, 12), 0, 0, 0)
        searchingRow.addView(sTxt)
        col.addView(searchingRow)
        hostsBox = LinearLayout(this)
        hostsBox.orientation = LinearLayout.VERTICAL
        col.addView(hostsBox)

        // Recents
        recentsTitle = Ui.sectionTitle(this, "Recent")
        col.addView(recentsTitle)
        recentsBox = LinearLayout(this)
        recentsBox.orientation = LinearLayout.VERTICAL
        col.addView(recentsBox)

        // Manual IP
        col.addView(Ui.sectionTitle(this, "Connect by IP address"))
        val ipCard = Ui.card(this)
        ipField = EditText(this)
        ipField.hint = "e.g. 192.168.1.20"
        ipField.setSingleLine(true)
        ipField.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
        ipField.imeOptions = EditorInfo.IME_ACTION_GO
        ipField.setText(Prefs.manualHost)
        ipField.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_GO) {
                connectManual()
                true
            } else {
                false
            }
        }
        ipCard.addView(ipField, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        val go = Button(this)
        go.text = "Connect"
        go.setOnClickListener { connectManual() }
        ipCard.addView(go, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        col.addView(ipCard)

        val hint = Ui.text(
            this,
            "Start Lumen Host on the computer. The first time you connect, this device shows a 5-digit code: type it on the computer.",
            13f, R.color.lumen_text2
        )
        hint.setPadding(Ui.dp(this, 4), Ui.dp(this, 16), Ui.dp(this, 4), 0)
        col.addView(hint)

        setContentView(scroll)
    }

    override fun onStart() {
        super.onStart()
        launched = false
        client.addListener(listener)
        client.setDiscoveryActive(true)
        renderHosts(client.hosts)
        renderRecents()
        applyState()
        if (client.state == StreamClient.State.STREAMING) openStream()
    }

    override fun onStop() {
        super.onStop()
        client.removeListener(listener)
        client.setDiscoveryActive(false)
        pairingDialog?.dismiss()
        pairingDialog = null
    }

    private fun connectManual() {
        val a = ipField.text.toString().trim()
        if (a.isEmpty()) return
        Prefs.manualHost = a
        client.connect(a, a, a)
    }

    private fun openStream() {
        if (launched) return
        launched = true
        startActivity(Intent(this, StreamActivity::class.java))
    }

    private fun applyState() {
        val s = client.state
        val msg = client.stateMessage
        when (s) {
            StreamClient.State.CONNECTING, StreamClient.State.STREAMING -> {
                statusRow.visibility = View.VISIBLE
                statusProgress.visibility = View.VISIBLE
                statusCancel.visibility = View.VISIBLE
                statusText.setTextColor(Ui.color(this, R.color.lumen_text))
                statusText.text = "Connecting…"
            }
            StreamClient.State.FAILED -> {
                statusRow.visibility = View.VISIBLE
                statusProgress.visibility = View.GONE
                statusCancel.visibility = View.GONE
                statusText.setTextColor(Ui.color(this, R.color.lumen_error))
                statusText.text = if (msg.isEmpty()) "Connection failed" else msg
            }
            else -> statusRow.visibility = View.GONE
        }
    }

    private fun renderHosts(hosts: List<DiscoveredHost>) {
        hostsBox.removeAllViews()
        searchingRow.visibility = if (hosts.isEmpty()) View.VISIBLE else View.GONE
        for (h in hosts) {
            val r = Ui.row(this, h.name, h.platformLabel)
            r.setOnClickListener { client.connect(h.address, h.name, h.name) }
            hostsBox.addView(r)
        }
    }

    private fun renderRecents() {
        recentsBox.removeAllViews()
        val list = Prefs.recents()
        recentsTitle.visibility = if (list.isEmpty()) View.GONE else View.VISIBLE
        for (rh in list) {
            val r = Ui.row(this, rh.name, rh.address)
            r.setOnClickListener { client.connect(rh.address, rh.name, rh.key) }
            r.setOnLongClickListener {
                AlertDialog.Builder(this)
                    .setMessage("Remove " + rh.name + " from recent computers?")
                    .setPositiveButton("Remove") { _, _ ->
                        Prefs.removeRecent(rh.address)
                        renderRecents()
                    }
                    .setNegativeButton("Cancel", null)
                    .show()
                true
            }
            recentsBox.addView(r)
        }
    }

    private fun showPairing(hostName: String, code: String?) {
        pairingDialog?.dismiss()
        pairingDialog = null
        if (code == null) return
        val body = LinearLayout(this)
        body.orientation = LinearLayout.VERTICAL
        body.setPadding(Ui.dp(this, 24), Ui.dp(this, 16), Ui.dp(this, 24), Ui.dp(this, 8))
        body.addView(Ui.text(this, "Type this code on " + hostName + " to pair it with this device:", 15f, R.color.lumen_text))
        val c = Ui.text(this, code, 44f, R.color.lumen_accent, true)
        c.gravity = Gravity.CENTER
        c.letterSpacing = 0.2f
        c.setPadding(0, Ui.dp(this, 20), 0, Ui.dp(this, 12))
        body.addView(c, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        body.addView(Ui.text(this, "Enter it in the Lumen Host window (or terminal) on the computer. You only do this once.", 13f, R.color.lumen_text2))
        val d = AlertDialog.Builder(this)
            .setTitle("Pair with " + hostName)
            .setView(body)
            .setCancelable(false)
            .setNegativeButton("Cancel") { _, _ -> client.disconnect() }
            .create()
        pairingDialog = d
        d.show()
    }
}
