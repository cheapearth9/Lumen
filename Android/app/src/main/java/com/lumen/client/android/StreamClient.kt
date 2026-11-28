package com.lumen.client.android

import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.io.OutputStream
import java.io.InputStream
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.ThreadFactory
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/** One computer in the "Computers on your network" list. */
class DiscoveredHost(val address: String, val name: String, val platform: Int) {
    val platformLabel: String
        get() = when (platform) {
            1 -> "Mac"
            2 -> "Windows"
            3 -> "Linux"
            else -> ""
        }
}

/**
 * Networking + encryption + reassembly + hand-off to the decoder / audio. Session state lives on the `exec` thread;
 * input events are sent from their own thread. Public state is updated on `exec` and mirrored to listeners on the main thread.
 */
class StreamClient(private val app: Context) {
    enum class State { IDLE, CONNECTING, STREAMING, FAILED }

    interface Listener {
        fun onState(state: State, message: String) {}
        fun onHosts(hosts: List<DiscoveredHost>) {}
        fun onStats(text: String) {}
        fun onToast(text: String) {}
        fun onVideoSize(width: Int, height: Int) {}
        fun onHostInfo() {}

        /** Show (code != null) or dismiss (code == null) the pairing code that must be typed on the computer. */
        fun onPairing(hostName: String, code: String?) {}
    }

    private class Target(val name: String, val address: String, val key: String)

    // Snapshot readable from any thread
    @Volatile var state: State = State.IDLE
        private set
    @Volatile var stateMessage: String = ""
        private set
    @Volatile var videoWidth: Int = 16
        private set
    @Volatile var videoHeight: Int = 9
        private set
    @Volatile var hostCaps: Int = 0
        private set
    @Volatile var hostSourceWidth: Int = 0
        private set
    @Volatile var hostSourceHeight: Int = 0
        private set
    @Volatile var hostMode: Int = 0
        private set
    @Volatile var statsText: String = ""
        private set
    @Volatile var hosts: List<DiscoveredHost> = emptyList()
        private set

    val hostSupportsNativeTouch: Boolean get() = (hostCaps and WelcomeInfo.CAP_NATIVE_TOUCH) != 0
    val hostSupportsAudio: Boolean get() = (hostCaps and WelcomeInfo.CAP_AUDIO) != 0
    val hostModeText: String get() = if (hostMode == 1) "Extended display" else "Mirroring"

    val decoder = VideoDecoder()
    val audio = AudioPlayer()

    private val main = Handler(Looper.getMainLooper())
    private val listeners = CopyOnWriteArrayList<Listener>()
    private val exec = ScheduledThreadPoolExecutor(1, ThreadFactory { r ->
        val t = Thread(r, "lumen-client")
        t.isDaemon = true
        t
    })
    private val inputExec = ScheduledThreadPoolExecutor(1, ThreadFactory { r ->
        val t = Thread(r, "lumen-input")
        t.isDaemon = true
        t.priority = Thread.MAX_PRIORITY
        t
    })

    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var remote: InetAddress? = null

    // USB cable (adb forward) transport: one TCP connection, packets framed with a u32 big-endian length.
    @Volatile private var tcp: Socket? = null
    private var tcpOut: OutputStream? = null
    private val tcpLock = Any()
    private var isCable = false
    private var suppressCableUntil = 0L
    private var cableServer: ServerSocket? = null
    private var epoch = 0
    private var timer: ScheduledFuture<*>? = null

    private var target = Target("", "", "")
    private var pairingKey: ByteArray? = null
    @Volatile private var session: SecureSession? = null
    private var handshakeDone = false
    private var handshakeSentAt = 0L
    private var handshakeRetried = false
    private var replyAt = 0L
    private var sealedOK = false

    private var pairingActive = false
    private var pairing: PairingClient? = null
    private var pairTries = 0
    private var pairStep1At = 0L
    private var pairStep3: ByteArray? = null
    private var pairStep3At = 0L
    private var pairStep3SentAt = 0L

    private val reassembler = Reassembler()
    private var lastSets: List<ByteArray> = emptyList()
    private var needKey = true
    private var lastKeyRequest = 0L
    private var hello: HelloInfo? = null
    @Volatile private var gotWelcome = false
    private var gotFirstFrame = false
    private var connectedAt = 0L
    private var welcomeAt = 0L
    private var lastRx = 0L
    private var tick = 0
    private val inputSeq = AtomicInteger(0)
    private var audioRunning = false

    private var statFrames = 0
    private var statBytes = 0
    private var rttMs = 0

    // Discovery
    private val scanner = SubnetScanner(app)
    private val scanned = HashMap<String, Pair<ScanResult, Long>>()
    private val wakeQ = LinkedBlockingQueue<Int>()
    @Volatile private var discoveryOn = false
    private var discoveryThread: Thread? = null

    init {
        decoder.onNeedKeyframe = {
            exec.execute {
                needKey = true
                requestKeyframe()
            }
        }
        decoder.onError = { msg -> exec.execute { fail(msg) } }
    }

    private fun now(): Long = SystemClock.elapsedRealtime()

    fun addListener(l: Listener) { listeners.addIfAbsent(l) }
    fun removeListener(l: Listener) { listeners.remove(l) }

    private fun post(block: () -> Unit) { main.post { block() } }

    private fun setState(s: State, msg: String) {
        state = s
        stateMessage = msg
        post { for (l in listeners) l.onState(s, msg) }
    }

    private fun toast(text: String) {
        post { for (l in listeners) l.onToast(text) }
    }

    private fun notifyPairing(name: String, code: String?) {
        post { for (l in listeners) l.onPairing(name, code) }
    }

    // MARK: Discovery

    fun setDiscoveryActive(on: Boolean) {
        synchronized(this) {
            if (on) {
                if (discoveryOn && discoveryThread?.isAlive == true) return
                discoveryOn = true
                val t = Thread({
                    while (discoveryOn) {
                        if (state != State.CONNECTING && state != State.STREAMING) scanOnce()
                        try {
                            wakeQ.poll(5, TimeUnit.SECONDS)
                            wakeQ.clear()
                        } catch (e: InterruptedException) {
                            break
                        }
                    }
                }, "lumen-discovery")
                t.isDaemon = true
                discoveryThread = t
                t.start()
            } else {
                discoveryOn = false
                wakeQ.offer(1)
            }
        }
    }

    /** Scan again right now (refresh button). */
    fun rescanNow() { wakeQ.offer(1) }

    private fun scanOnce() {
        try {
            scanner.scan { r ->
                synchronized(scanned) { scanned[r.address] = Pair(r, now()) }
                publishHosts()
            }
        } catch (e: Exception) {
            // ignore, try again later
        }
        synchronized(scanned) {
            val cutoff = now() - 12000
            val it = scanned.values.iterator()
            while (it.hasNext()) if (it.next().second < cutoff) it.remove()
        }
        publishHosts()
    }

    private fun publishHosts() {
        val list = ArrayList<DiscoveredHost>()
        synchronized(scanned) {
            for (p in scanned.values) list.add(DiscoveredHost(p.first.address, p.first.name, p.first.platform))
        }
        list.sortWith(Comparator { a, b -> a.name.compareTo(b.name, ignoreCase = true) })
        hosts = list
        post { for (l in listeners) l.onHosts(list) }
    }

    // MARK: Connect

    /** [key] names the pairing secret (host name for discovered computers, the typed address otherwise). */
    fun connect(address: String, name: String, key: String) {
        exec.execute {
            try {
                begin(Target(name, address.trim(), key))
            } catch (e: Exception) {
                fail("Could not connect: " + (e.message ?: "error"))
            }
        }
    }

    fun disconnect() {
        exec.execute {
            send(PacketType.BYE, 0, EMPTY_BYTES)
            // The host re-offers the cable every couple of seconds: stay put after the user chose to leave.
            if (isCable) suppressCableUntil = now() + 30_000
            teardown(true)
        }
    }

    // MARK: Live settings (stream menu)

    /** Mute / unmute. Unmuting a stream that started without audio asks the host for it with a new hello. */
    fun setAudio(on: Boolean) {
        Prefs.audioOn = on
        exec.execute {
            audio.muted = !on
            val h = hello
            if (on && h != null && handshakeDone && !h.wantsAudio) {
                val n = h.copy()
                n.flags = n.flags or HelloInfo.FLAG_AUDIO
                switchHello(n)
            }
        }
    }

    /** Mirror / Extend while streaming: a new hello with the new flag (the host restarts the session). */
    fun setExtend(on: Boolean) {
        Prefs.extend = on
        exec.execute {
            val h = hello
            if (h == null || !handshakeDone || h.wantsExtend == on) return@execute
            val n = h.copy()
            n.flags = if (on) n.flags or HelloInfo.FLAG_EXTEND else n.flags and HelloInfo.FLAG_EXTEND.inv()
            n.flags = if (Prefs.audioOn) n.flags or HelloInfo.FLAG_AUDIO else n.flags and HelloInfo.FLAG_AUDIO.inv()
            switchHello(n)
        }
    }

    /** The orientation choice changed: ask the computer for a screen of the new shape. */
    fun reconnectForResolution(ctx: android.content.Context) {
        val (w, h) = Prefs.requestedResolution(ctx)
        exec.execute {
            val cur = hello
            if (cur == null || !handshakeDone || (cur.width == w && cur.height == h)) return@execute
            val n = cur.copy()
            n.width = w; n.height = h
            switchHello(n)
        }
    }

    private fun switchHello(h: HelloInfo) {
        hello = h
        gotWelcome = false
        gotFirstFrame = false
        needKey = true
        lastSets = emptyList()
        reassembler.reset()
        welcomeAt = now()
        lastRx = now()
    }

    // MARK: Session lifecycle (exec thread)

    // MARK: USB cable (exec thread unless noted)

    init { startCableListener() }

    private fun startCableListener() {
        val t = Thread({
            while (true) {
                var server: ServerSocket? = null
                try {
                    server = ServerSocket()
                    server.reuseAddress = true
                    server.bind(java.net.InetSocketAddress(InetAddress.getByName("127.0.0.1"), LUMEN_CABLE_PORT), 2)
                    cableServer = server
                    while (true) {
                        val c = server.accept()
                        exec.execute { cableAccepted(c) }
                    }
                } catch (e: Exception) {
                    try { server?.close() } catch (e2: Exception) { }
                }
                try { Thread.sleep(2000) } catch (e: InterruptedException) { return@Thread }
            }
        }, "lumen-cable-listen")
        t.isDaemon = true
        t.start()
    }

    /** The host connected through `adb forward`. Auto-connect only when idle (and not right after the user disconnected). */
    private fun cableAccepted(c: Socket) {
        val busy = state == State.CONNECTING || state == State.STREAMING
        if (busy || now() < suppressCableUntil) {
            try { c.close() } catch (e: Exception) { }
            return
        }
        try {
            c.tcpNoDelay = true
            begin(Target("USB cable", "", "usb-cable"), c)
        } catch (e: Exception) {
            try { c.close() } catch (e2: Exception) { }
        }
    }

    private fun tcpReceiveLoop(c: Socket, myEpoch: Int) {
        try {
            val input: InputStream = c.getInputStream()
            val head = ByteArray(4)
            while (!c.isClosed) {
                readFully(input, head, 4)
                val len = ((head[0].toInt() and 0xFF) shl 24) or ((head[1].toInt() and 0xFF) shl 16) or
                    ((head[2].toInt() and 0xFF) shl 8) or (head[3].toInt() and 0xFF)
                if (len < 0 || len > 64 * 1024 * 1024) break
                val data = ByteArray(len)
                readFully(input, data, len)
                exec.execute { if (epoch == myEpoch) process(data) }
            }
        } catch (e: Exception) {
        }
        exec.execute {
            if (epoch == myEpoch && tcp === c) fail("The USB cable connection was closed")
        }
    }

    private fun readFully(input: InputStream, buf: ByteArray, n: Int) {
        var got = 0
        while (got < n) {
            val r = input.read(buf, got, n - got)
            if (r < 0) throw IOException("closed")
            got += r
        }
    }

    private fun begin(t: Target, cable: Socket? = null) {
        teardown(false)
        epoch++
        hello = Prefs.helloInfo(app)
        if (cable != null) hello?.bitrateMbps = Prefs.mbpsCable
        isCable = cable != null
        target = t
        pairingKey = null
        session = null
        handshakeDone = false
        handshakeRetried = false
        replyAt = 0L
        sealedOK = false
        pairingActive = false
        pairing = null
        pairStep3 = null
        gotWelcome = false
        gotFirstFrame = false
        needKey = true
        lastSets = emptyList()
        reassembler.reset()
        connectedAt = now()
        lastRx = now()
        tick = 0
        statFrames = 0
        statBytes = 0
        audio.muted = !Prefs.audioOn
        setState(State.CONNECTING, "")

        val myEpoch = epoch
        if (cable != null) {
            synchronized(tcpLock) { tcp = cable; tcpOut = cable.getOutputStream() }
            val rt = Thread({ tcpReceiveLoop(cable, myEpoch) }, "lumen-recv-usb")
            rt.isDaemon = true
            rt.priority = Thread.MAX_PRIORITY
            rt.start()
        } else {
            val addr: InetAddress = try {
                InetAddress.getByName(t.address)
            } catch (e: Exception) {
                fail("Unknown address: " + t.address)
                return
            }
            val s = DatagramSocket()
            NetBind.bind(app, s)
            try { s.receiveBufferSize = 1 shl 21 } catch (e: Exception) { }
            try { s.trafficClass = 0xB8 } catch (e: Exception) { }
            socket = s
            remote = addr
            val rt = Thread({ receiveLoop(s, addr, myEpoch) }, "lumen-recv")
            rt.isDaemon = true
            rt.priority = Thread.MAX_PRIORITY
            rt.start()
        }
        timer = exec.scheduleWithFixedDelay(Runnable { onTick() }, 250, 250, TimeUnit.MILLISECONDS)

        val k = Prefs.loadPairingKey(t.key)
        if (k != null) {
            pairingKey = k
            beginHandshake()
        } else {
            startPairing()
        }
    }

    private fun teardown(notify: Boolean) {
        timer?.cancel(false)
        timer = null
        val s = socket
        socket = null
        try { s?.close() } catch (e: Exception) { }
        remote = null
        val c = tcp
        synchronized(tcpLock) { tcp = null; tcpOut = null }
        try { c?.close() } catch (e: Exception) { }
        session = null
        handshakeDone = false
        if (pairingActive) notifyPairing(target.name, null)
        pairingActive = false
        pairing = null
        pairStep3 = null
        gotWelcome = false
        hello = null
        epoch++
        if (audioRunning) {
            audio.stop()
            audioRunning = false
        }
        decoder.reset()
        if (notify) setState(State.IDLE, "")
    }

    private fun fail(msg: String) {
        teardown(false)
        setState(State.FAILED, msg)
    }

    private fun receiveLoop(s: DatagramSocket, addr: InetAddress, myEpoch: Int) {
        val buf = ByteArray(2048)
        while (!s.isClosed) {
            try {
                val p = DatagramPacket(buf, buf.size)
                s.receive(p)
                if (p.address != addr) continue
                val data = buf.copyOf(p.length)
                exec.execute { if (epoch == myEpoch) process(data) }
            } catch (e: IOException) {
                if (s.isClosed) break
                try { Thread.sleep(5) } catch (ie: InterruptedException) { }
            }
        }
    }

    // MARK: Pairing / handshake

    private fun deviceName(): String {
        val m = (Build.MANUFACTURER ?: "").trim()
        val d = (Build.MODEL ?: "Android").trim()
        return if (m.isEmpty() || d.startsWith(m, ignoreCase = true)) d else "$m $d"
    }

    private fun startPairing() {
        pairingActive = true
        pairingKey = null
        session = null
        handshakeDone = false
        replyAt = 0L
        sealedOK = false
        pairing = null
        pairTries = 0
        pairStep3 = null
        lastRx = now()
        connectedAt = now()
        setState(State.CONNECTING, "")
        sendPairStep1()
    }

    private fun sendPair(payload: ByteArray) {
        rawSend(Wire.encode(PacketType.PAIR, payload = payload))
    }

    private fun sendPairStep1() {
        if (!pairingActive) return
        val p = PairingClient()
        pairing = p
        pairTries++
        pairStep1At = now()
        sendPair(p.start(deviceName()))
    }

    private fun failPairing(msg: String) {
        notifyPairing(target.name, null)
        pairingActive = false
        pairing = null
        pairStep3 = null
        fail(msg)
    }

    private fun handlePair(payload: ByteArray) {
        if (!pairingActive || payload.isEmpty()) return
        val p = pairing ?: return
        when (payload[0].toInt()) {
            2 -> {
                if (pairStep3 != null) return
                val s3 = p.handleStep2(payload) ?: return
                val code = p.code ?: return
                pairStep3 = s3
                pairStep3At = now()
                pairStep3SentAt = now()
                sendPair(s3)
                notifyPairing(target.name, code)
            }
            4 -> {
                if (pairStep3 == null || payload.size < 2) return
                when (payload[1].toInt()) {
                    0 -> {
                        val secret = p.secret ?: return
                        Prefs.savePairingKey(target.key, secret)
                        notifyPairing(target.name, null)
                        pairingActive = false
                        pairing = null
                        pairStep3 = null
                        pairingKey = secret
                        session = null
                        handshakeDone = false
                        handshakeRetried = false
                        replyAt = 0L
                        sealedOK = false
                        connectedAt = now()
                        lastRx = now()
                        setState(State.CONNECTING, "")
                        beginHandshake()
                    }
                    1 -> failPairing("Wrong code. Try pairing again.")
                    2 -> failPairing("The computer is busy or temporarily locked, try again in a minute")
                    else -> failPairing("Pairing failed")
                }
            }
        }
    }

    /** The stored secret did not work (host forgot this device or was reset): pair again. */
    private fun restartPairing() {
        Prefs.deletePairingKey(target.key)
        startPairing()
    }

    private fun beginHandshake() {
        if (pairingActive || session != null) return
        val key = pairingKey ?: return
        val (s, clientHello) = SecureSession.client(key)
        session = s
        handshakeDone = false
        handshakeSentAt = now()
        rawSend(Wire.encode(PacketType.HANDSHAKE, payload = clientHello))
    }

    private fun handleHandshake(payload: ByteArray) {
        val s = session ?: return
        if (handshakeDone || s.isReady) return
        if (s.finish(payload)) {
            handshakeDone = true
            replyAt = now()
            lastRx = now()
        }
    }

    // MARK: Timer (handshake / hello retries, pings, stats, watchdog)

    private fun onTick() {
        try {
            tickInner()
        } catch (e: Exception) {
            // keep the timer alive
        }
    }

    private fun tickInner() {
        if (socket == null && tcp == null) return
        val n = now()
        if (pairingActive) {
            lastRx = n
            connectedAt = n
            val p3 = pairStep3
            if (p3 == null) {
                if (pairing == null) {
                    sendPairStep1()
                } else if (n - pairStep1At > 4000) {
                    if (pairTries >= 3) failPairing("Could not reach the computer") else sendPairStep1()
                }
            } else {
                if (n - pairStep3At > 125000) {
                    failPairing("Pairing timed out")
                } else if (n - pairStep3SentAt > 1000) {
                    pairStep3SentAt = n
                    sendPair(p3)
                }
            }
            return
        }
        tick++

        if (pairingKey != null) {
            if (!handshakeDone) {
                if (session == null) {
                    beginHandshake()
                } else if (n - handshakeSentAt > 1000) {
                    session = null
                    beginHandshake()
                }
            } else if (!gotWelcome) {
                val h = hello
                if (h != null) {
                    send(PacketType.HELLO, 0, h.encode())
                    if (!sealedOK && replyAt != 0L) {
                        val waited = n - replyAt
                        if (!handshakeRetried && waited > 2000) {
                            // One fresh handshake in case a stale reply gave us mismatching keys.
                            handshakeRetried = true
                            session = null
                            handshakeDone = false
                            beginHandshake()
                            return
                        }
                        if (waited > 4000) {
                            restartPairing()
                            return
                        }
                    }
                }
            } else if (tick % 4 == 0) {
                send(PacketType.PING, now().toInt(), EMPTY_BYTES)
                val fps = statFrames
                val mbps = statBytes.toDouble() * 8.0 / 1e6
                statFrames = 0
                statBytes = 0
                val text = String.format(Locale.US, "Wi-Fi · %d fps · %.1f Mbps · %d ms", fps, mbps, rttMs)
                statsText = text
                post { for (l in listeners) l.onStats(text) }
            }
        }

        // Watchdog: hosts may spend ~10 s probing encoders before the first frame; once streaming be quick.
        if (!gotWelcome) {
            val since = n - (if (handshakeDone) maxOf(lastRx, connectedAt) else connectedAt)
            if (since > 15000) fail(if (handshakeDone) "The computer did not answer" else "Could not reach the computer")
        } else if (!gotFirstFrame) {
            if (n - welcomeAt > 15000) fail("The computer did not start streaming")
        } else if (n - lastRx > 4000) {
            fail("Lost connection to the computer")
        }
    }

    // MARK: Send

    private fun rawSend(pkt: ByteArray) {
        if (tcp != null) {
            synchronized(tcpLock) {
                val o = tcpOut ?: return
                try {
                    val h = byteArrayOf((pkt.size ushr 24).toByte(), (pkt.size ushr 16).toByte(), (pkt.size ushr 8).toByte(), pkt.size.toByte())
                    o.write(h)
                    o.write(pkt)
                    o.flush()
                } catch (e: IOException) {
                }
            }
            return
        }
        val s = socket ?: return
        val a = remote ?: return
        try {
            s.send(DatagramPacket(pkt, pkt.size, a, LUMEN_PORT))
        } catch (e: IOException) {
            // dropped, UDP
        }
    }

    /** Seals and sends. Dropped silently until the handshake finished. */
    private fun send(type: Int, id: Int, payload: ByteArray) {
        val s = session ?: return
        if (!s.isReady) return
        val sealed = s.seal(Wire.encode(type, id = id, payload = payload)) ?: return
        rawSend(sealed)
    }

    /** Thread-safe; may be called from the main thread. */
    fun sendInput(e: InputEvent) {
        inputExec.execute {
            if (!gotWelcome) return@execute
            val s = session
            if (s == null || !s.isReady) return@execute
            val plain = Wire.encode(PacketType.INPUT, id = inputSeq.incrementAndGet(), payload = e.encode())
            val first = s.seal(plain) ?: return@execute
            rawSend(first)
            if (e.isDiscrete) {
                // Key / button events are sent twice (sealed separately) so one lost datagram can't cause a stuck key.
                inputExec.schedule(Runnable {
                    val again = s.seal(plain)
                    if (again != null) rawSend(again)
                }, 6, TimeUnit.MILLISECONDS)
            }
        }
    }

    // MARK: Receive (exec thread)

    private fun process(raw: ByteArray) {
        if (SecureSession.isSealed(raw)) {
            val s = session ?: return
            if (!s.isReady) return
            val plain = s.open(raw) ?: return
            val pkt = Wire.decode(plain) ?: return
            sealedOK = true
            lastRx = now()
            handle(pkt)
        } else {
            val pkt = Wire.decode(raw) ?: return
            if (pkt.type == PacketType.HANDSHAKE) {
                if (session != null && !handshakeDone && !pairingActive && SecureSession.isPairingRequired(pkt.payload)) {
                    restartPairing()
                } else {
                    handleHandshake(pkt.payload)
                }
            } else if (pkt.type == PacketType.PAIR) {
                handlePair(pkt.payload)
            }
        }
    }

    private fun handle(p: Packet) {
        when (p.type) {
            PacketType.WELCOME -> {
                val w = WelcomeInfo.decode(p.payload) ?: return
                if (gotWelcome) return
                gotWelcome = true
                gotFirstFrame = false
                welcomeAt = now()
                needKey = true
                lastSets = emptyList()
                reassembler.reset()

                val wantAudio = (w.caps and WelcomeInfo.CAP_AUDIO) != 0 && (hello?.wantsAudio == true)
                if (wantAudio) {
                    audio.muted = !Prefs.audioOn
                    if (!audioRunning) {
                        audio.start()
                        audioRunning = true
                    }
                } else if (audioRunning) {
                    audio.stop()
                    audioRunning = false
                }

                val t = target
                if (t.address.isNotEmpty()) Prefs.addRecent(RecentHost(t.name, t.address, t.key))

                videoWidth = w.width
                videoHeight = w.height
                hostCaps = w.caps
                hostSourceWidth = w.sourceWidth
                hostSourceHeight = w.sourceHeight
                hostMode = w.mode
                setState(State.STREAMING, "")
                post {
                    for (l in listeners) {
                        l.onVideoSize(w.width, w.height)
                        l.onHostInfo()
                    }
                }
            }
            PacketType.VIDEO -> {
                if (!gotWelcome) return
                val f = reassembler.add(p) ?: return
                handleVideo(f)
            }
            PacketType.AUDIO -> {
                if (audioRunning) audio.push(p.payload)
            }
            PacketType.STATUS -> {
                if (p.payload.isEmpty()) return
                val level = p.payload[0].toInt() and 0xFF
                val text = String(p.payload, 1, p.payload.size - 1, Charsets.UTF_8)
                if (level >= 1) {
                    fail(if (text.isEmpty()) "The computer reported an error" else text)
                } else if (text.isNotEmpty()) {
                    toast(text)
                }
            }
            PacketType.PING -> {
                rttMs = now().toInt() - p.id
            }
            else -> {}
        }
    }

    private fun sameSets(a: List<ByteArray>, b: List<ByteArray>): Boolean {
        if (a.size != b.size) return false
        for (i in a.indices) if (!a[i].contentEquals(b[i])) return false
        return true
    }

    private fun handleVideo(f: Reassembler.Frame) {
        val isKey = (f.flags and 1) != 0
        if (f.gap) needKey = true
        gotFirstFrame = true

        var off = 0
        if (isKey) {
            val r = ByteReader(f.data)
            val sets = ArrayList<ByteArray>()
            val count = r.u8()
            for (i in 0 until count) {
                val l = r.u16()
                val s = r.take(l)
                if (!r.ok) return
                sets.add(s)
            }
            off = f.data.size - r.remaining
            if (sets.isEmpty()) return
            if (!sameSets(sets, lastSets)) {
                decoder.configure(sets, videoWidth, videoHeight, hello?.fps ?: 60)
                lastSets = sets
            }
            needKey = false
        }

        if (needKey) {
            requestKeyframe()
            return
        }
        val len = f.data.size - off
        if (len <= 4) return
        val res = decoder.queueFrame(f.data, off, len, isKey)
        if (res == VideoDecoder.OK) {
            statFrames++
            statBytes += f.data.size
        } else if (res == VideoDecoder.DROPPED) {
            needKey = true
            requestKeyframe()
        }
    }

    private fun requestKeyframe() {
        if (now() - lastKeyRequest < 100) return
        lastKeyRequest = now()
        send(PacketType.KEYFRAME_REQUEST, 0, EMPTY_BYTES)
    }
}
