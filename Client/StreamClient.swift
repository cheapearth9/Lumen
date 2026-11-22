import Foundation
import Network
import CoreMedia
import Combine

/// One computer in the "Computers on your network" list (Bonjour and/or unicast scan, merged).
struct DiscoveredHost: Identifiable, Hashable {
    let id: String
    let name: String
    let platform: UInt8        // 0 unknown (Bonjour only), 1 mac, 2 windows, 3 linux
    let address: String?       // dotted IPv4 when known
    let endpoint: NWEndpoint?  // Bonjour endpoint when known

    var platformLabel: String {
        switch platform {
        case 1: return "Mac"
        case 2: return "Windows"
        case 3: return "Linux"
        default: return ""
        }
    }
}

/// Networking + encryption + reassembly + hand-off to the display layers. All mutable state lives on `queue`.
/// Two transports share one protocol:
///  - Wi-Fi / Ethernet: UDP datagrams to the host (found over Bonjour / subnet scan, or typed IP).
///  - Cable: the app listens on a TCP port; the host reaches it through usbmuxd over USB.
final class StreamClient: ObservableObject {
    enum State: Equatable {
        case idle, connecting, streaming
        case failed(String)
    }

    private struct Target {
        var name: String
        var address: String?
        var key: String        // keychain account (pairing code is stored per host)
    }

    private struct BonjourEntry {
        var name: String
        var endpoint: NWEndpoint
        var address: String?
    }

    @Published var state: State = .idle
    @Published var hosts: [DiscoveredHost] = []
    @Published var statsText = ""
    @Published var videoSize = CGSize(width: 16, height: 9)
    @Published var cablePaused = false
    @Published var hostCaps: UInt8 = 0
    @Published var hostSourceSize = CGSize.zero
    @Published var hostMode: UInt8 = 0            // 0 mirror, 1 extend (what the host really does)
    @Published var toast: String?

    var hostSupportsNativeTouch: Bool { hostCaps & WelcomeInfo.capNativeTouch != 0 }
    var hostSupportsAudio: Bool { hostCaps & WelcomeInfo.capAudio != 0 }
    var hostModeText: String { hostMode == 1 ? "Extended display" : "Mirroring" }

    /// Supplies the resolution/fps/bitrate to request. Called off the main thread; hops to main internally.
    var helloProvider: (() -> HelloInfo)?

    private let queue = DispatchQueue(label: "lumen.client", qos: .userInteractive)
    private var browser: NWBrowser?
    private var cableListener: NWListener?
    private var conn: NWConnection?
    private var connReady = false
    private var isCable = false
    private var suppressCable = false
    private var timer: DispatchSourceTimer?
    private var deframer = Deframer()
    private var epoch = 0

    // Discovery
    private var bonjour: [String: BonjourEntry] = [:]
    private var resolvers: [String: NWConnection] = [:]
    private var scanned: [String: (result: ScanResult, seen: Date)] = [:]
    private let scanner = SubnetScanner()
    private var scanning = false
    private var scanWaiters = [() -> Void]()
    private var scanTimer: DispatchSourceTimer?

    // Session
    private var target = Target(name: "", address: nil, key: "")
    private var pairingKey: Data?
    private var session: SecureSession?
    private var handshakeDone = false
    private var handshakeSentAt = Date.distantPast
    private var handshakeRetried = false
    private var replyAt: Date?
    private var sealedOK = false
    private var awaitingUser = false

    private var reassembler = Reassembler()
    private var formatDesc: CMVideoFormatDescription?
    private var lastSets = [Data]()
    private var needKey = true
    private var lastKeyRequest = Date.distantPast
    private var hello: HelloInfo?
    private var gotWelcome = false
    private var gotFirstFrame = false
    private var connectedAt = Date()
    private var welcomeAt = Date()
    private var lastRx = Date()
    private var tick = 0
    private var inputSeq: UInt32 = 0

    private let audio = AudioPlayer()
    private var audioRunning = false

    private var statFrames = 0, statBytes = 0, rttMs = 0
    private var toastSerial = 0

    // MARK: Start (idempotent; call again when the app becomes active)

    func start() {
        startBrowsing()
        queue.async { self.startCableListener() }
    }

    // MARK: Discovery

    private func startBrowsing() {
        queue.async {
            guard self.browser == nil else { return }
            let b = NWBrowser(for: .bonjour(type: lumenBonjourType, domain: nil), using: NWParameters())
            b.browseResultsChangedHandler = { [weak self] results, _ in
                self?.bonjourChanged(results)
            }
            b.stateUpdateHandler = { [weak self] st in
                if case .failed = st { self?.queue.async { self?.browser?.cancel(); self?.browser = nil } }
            }
            b.start(queue: self.queue)
            self.browser = b
        }
    }

    private func bonjourChanged(_ results: Set<NWBrowser.Result>) {
        var next: [String: BonjourEntry] = [:]
        for r in results {
            if case let .service(name, _, _, _) = r.endpoint {
                next[name] = BonjourEntry(name: name, endpoint: r.endpoint, address: bonjour[name]?.address)
            }
        }
        bonjour = next
        for (name, entry) in next where entry.address == nil && resolvers[name] == nil {
            resolve(name: name, endpoint: entry.endpoint)
        }
        for (name, c) in resolvers where next[name] == nil { c.cancel(); resolvers[name] = nil }
        publishHosts()
    }

    /// Opens a throw-away UDP connection to the Bonjour service just to learn its IPv4 address (for merging).
    private func resolve(name: String, endpoint: NWEndpoint) {
        let c = NWConnection(to: endpoint, using: .udp)
        c.stateUpdateHandler = { [weak self, weak c] st in
            guard let self, let c else { return }
            switch st {
            case .ready:
                if let ep = c.currentPath?.remoteEndpoint, case let .hostPort(host, _) = ep,
                   case let .ipv4(a) = host {
                    let text = a.rawValue.map { String($0) }.joined(separator: ".")
                    if text.split(separator: ".").count == 4 {
                        self.bonjour[name]?.address = text
                        self.publishHosts()
                    }
                }
                c.cancel()
                self.resolvers[name] = nil
            case .failed:
                c.cancel()
                self.resolvers[name] = nil
            default:
                break
            }
        }
        resolvers[name] = c
        c.start(queue: queue)
    }

    private func publishHosts() {
        var byId: [String: DiscoveredHost] = [:]
        var usedScan = Set<String>()
        for b in bonjour.values {
            var match: ScanResult?
            if let a = b.address, let s = scanned[a] {
                match = s.result
            } else {
                match = scanned.values.first { $0.result.name.caseInsensitiveCompare(b.name) == .orderedSame }?.result
            }
            if let m = match {
                usedScan.insert(m.address)
                byId[m.address] = DiscoveredHost(id: m.address, name: m.name, platform: m.platform,
                                                 address: m.address, endpoint: b.endpoint)
            } else {
                let id = b.address ?? ("bonjour:" + b.name)
                byId[id] = DiscoveredHost(id: id, name: b.name, platform: 0, address: b.address, endpoint: b.endpoint)
            }
        }
        for s in scanned.values where !usedScan.contains(s.result.address) {
            byId[s.result.address] = DiscoveredHost(id: s.result.address, name: s.result.name, platform: s.result.platform,
                                                    address: s.result.address, endpoint: nil)
        }
        let list = byId.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        DispatchQueue.main.async { self.hosts = list }
    }

    /// Runs one unicast subnet scan. `completion` is called (on the client queue) when it finished.
    func rescan(completion: (() -> Void)? = nil) {
        queue.async {
            if let completion { self.scanWaiters.append(completion) }
            guard !self.scanning else { return }
            self.scanning = true
            self.scanner.scan(onResult: { [weak self] r in
                self?.queue.async {
                    self?.scanned[r.address] = (r, Date())
                    self?.publishHosts()
                }
            }, completion: { [weak self] in
                self?.queue.async {
                    guard let self else { return }
                    self.scanning = false
                    let cutoff = Date().addingTimeInterval(-12)
                    self.scanned = self.scanned.filter { $0.value.seen > cutoff }
                    self.publishHosts()
                    let waiters = self.scanWaiters
                    self.scanWaiters.removeAll()
                    waiters.forEach { $0() }
                }
            })
        }
    }

    /// For pull-to-refresh.
    func rescanAndWait() async {
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            rescan { cont.resume() }
        }
    }

    /// Scans immediately and then every ~5 s while the connect screen is visible.
    func setDiscoveryActive(_ on: Bool) {
        queue.async {
            self.scanTimer?.cancel()
            self.scanTimer = nil
            guard on else { return }
            let t = DispatchSource.makeTimerSource(queue: self.queue)
            t.schedule(deadline: .now(), repeating: 5.0, leeway: .milliseconds(200))
            t.setEventHandler { [weak self] in
                guard let self, self.conn == nil else { return }
                self.rescan()
            }
            t.resume()
            self.scanTimer = t
        }
    }

    // MARK: Cable listener

    private func startCableListener() {
        guard cableListener == nil,
              let port = NWEndpoint.Port(rawValue: lumenCablePort) else { return }
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let params = NWParameters(tls: nil, tcp: tcp)
        params.allowLocalEndpointReuse = true
        guard let l = try? NWListener(using: params, on: port) else { return }
        l.newConnectionHandler = { [weak self] c in self?.cableAccepted(c) }
        l.stateUpdateHandler = { [weak self, weak l] st in
            switch st {
            case .failed, .cancelled:
                l?.cancel()
                self?.queue.async { if self?.cableListener === l { self?.cableListener = nil } }
            default: break
            }
        }
        l.start(queue: queue)
        cableListener = l
    }

    /// The host connected through the USB tunnel. Auto-connect only when we are idle.
    private func cableAccepted(_ c: NWConnection) {
        guard conn == nil, !suppressCable, let provider = helloProvider else { c.cancel(); return }
        let h = DispatchQueue.main.sync { provider() }
        begin(c, cable: true, hello: h, target: Target(name: "USB cable", address: nil, key: "usb-cable"))
    }

    /// Resume auto-connect after the user disconnected from a cable session.
    func resumeCable() {
        queue.async { self.suppressCable = false }
        DispatchQueue.main.async { self.cablePaused = false }
    }

    // MARK: Wi-Fi connect

    func connect(host: DiscoveredHost, hello: HelloInfo) {
        if let a = host.address {
            connect(address: a, name: host.name, key: host.name, hello: hello)
        } else if let ep = host.endpoint {
            queue.async {
                self.beginUDP(ep, hello: hello, target: Target(name: host.name, address: nil, key: host.name))
            }
        }
    }

    func connect(recent: RecentHost, hello: HelloInfo) {
        connect(address: recent.address, name: recent.name, key: recent.key, hello: hello)
    }

    /// Manual IP entry: the pairing code is stored under the typed address.
    func connect(host: String, hello: HelloInfo) {
        let a = host.trimmingCharacters(in: .whitespaces)
        connect(address: a, name: a, key: a, hello: hello)
    }

    private func connect(address: String, name: String, key: String, hello: HelloInfo) {
        guard let port = NWEndpoint.Port(rawValue: lumenPort) else { return }
        let ep = NWEndpoint.hostPort(host: NWEndpoint.Host(address), port: port)
        queue.async {
            self.beginUDP(ep, hello: hello, target: Target(name: name, address: address, key: key))
        }
    }

    private func beginUDP(_ endpoint: NWEndpoint, hello: HelloInfo, target: Target) {
        let params = NWParameters.udp
        params.serviceClass = .interactiveVideo
        begin(NWConnection(to: endpoint, using: params), cable: false, hello: hello, target: target)
    }

    func disconnect() {
        queue.async {
            self.send(.bye)
            if self.isCable {
                self.suppressCable = true
                DispatchQueue.main.async { self.cablePaused = true }
            }
            self.teardown(notify: true)
        }
    }

    // MARK: Live settings (stream menu)

    /// Mute / unmute. Unmuting a stream that was started without audio asks the host for it with a new hello.
    func setAudio(_ on: Bool) {
        LumenSettings.audioOn = on
        queue.async {
            self.audio.muted = !on
            guard on, var h = self.hello, self.handshakeDone, !h.wantsAudio else { return }
            h.flags |= HelloInfo.flagAudio
            self.switchHello(h)
        }
    }

    /// Mirror / Extend while streaming: a new hello with the new flag (the host restarts the session).
    func setExtend(_ on: Bool) {
        LumenSettings.extend = on
        queue.async {
            guard var h = self.hello, self.handshakeDone, h.wantsExtend != on else { return }
            if on { h.flags |= HelloInfo.flagExtend } else { h.flags &= ~HelloInfo.flagExtend }
            if LumenSettings.audioOn { h.flags |= HelloInfo.flagAudio } else { h.flags &= ~HelloInfo.flagAudio }
            self.switchHello(h)
        }
    }

    private func switchHello(_ h: HelloInfo) {
        hello = h
        gotWelcome = false
        gotFirstFrame = false
        needKey = true
        formatDesc = nil
        lastSets = []
        reassembler.reset()
        welcomeAt = Date()
        lastRx = Date()
    }

    // MARK: Session lifecycle (queue)

    private func begin(_ c: NWConnection, cable: Bool, hello: HelloInfo, target: Target) {
        teardown(notify: false)
        epoch += 1
        self.hello = hello
        self.target = target
        pairingKey = nil
        session = nil
        handshakeDone = false
        handshakeRetried = false
        replyAt = nil
        sealedOK = false
        awaitingUser = false
        gotWelcome = false
        gotFirstFrame = false
        needKey = true
        formatDesc = nil
        lastSets = []
        reassembler.reset()
        deframer = Deframer()
        connectedAt = Date()
        lastRx = Date()
        tick = 0
        isCable = cable
        connReady = false
        audio.muted = !LumenSettings.audioOn
        setState(.connecting)
        DispatchQueue.main.async { self.toast = nil }

        c.stateUpdateHandler = { [weak self, weak c] st in
            guard let self, let c, self.conn === c else { return }
            switch st {
            case .ready:
                self.connReady = true
                if cable { self.receiveStream(c) } else { self.receiveDatagram(c) }
                self.beginHandshake()
            case .failed(let e):
                self.fail("Connection failed: \(e.localizedDescription)")
            default: break
            }
        }
        conn = c
        c.start(queue: queue)
        startTimer()

        if let k = PairingKeychain.load(host: target.key) {
            pairingKey = k
        } else {
            askForCode(note: nil, wrong: false)
        }
    }

    private func teardown(notify: Bool) {
        timer?.cancel(); timer = nil
        conn?.cancel(); conn = nil
        connReady = false
        session = nil
        handshakeDone = false
        awaitingUser = false
        gotWelcome = false
        hello = nil
        isCable = false
        epoch += 1
        if audioRunning { audio.stop(); audioRunning = false }
        if notify { setState(.idle) }
        DispatchQueue.main.async { VideoRenderer.shared.clear() }
    }

    private func fail(_ msg: String) {
        teardown(notify: false)
        setState(.failed(msg))
    }

    private func setState(_ s: State) { DispatchQueue.main.async { self.state = s } }

    private func showToast(_ text: String) {
        DispatchQueue.main.async {
            self.toastSerial += 1
            let serial = self.toastSerial
            self.toast = text
            DispatchQueue.main.asyncAfter(deadline: .now() + 4) {
                if self.toastSerial == serial { self.toast = nil }
            }
        }
    }

    // MARK: Pairing / handshake

    /// Asks for the pairing code (nothing stored yet, or the stored one did not work).
    private func askForCode(note: String?, wrong: Bool) {
        awaitingUser = true
        let myEpoch = epoch
        let name = target.name
        let key = target.key
        DispatchQueue.main.async {
            PairingPrompt.present(host: name, note: note) { [weak self] code in
                self?.queue.async {
                    guard let self, self.epoch == myEpoch, self.conn != nil else { return }
                    self.awaitingUser = false
                    if let code {
                        PairingKeychain.save(code, host: key)
                        self.pairingKey = code
                        self.session = nil
                        self.handshakeDone = false
                        self.handshakeRetried = false
                        self.replyAt = nil
                        self.sealedOK = false
                        self.connectedAt = Date()
                        self.lastRx = Date()
                        self.setState(.connecting)
                        self.beginHandshake()
                    } else if wrong {
                        if self.isCable { self.suppressCable = true; DispatchQueue.main.async { self.cablePaused = true } }
                        self.fail("Wrong pairing code")
                    } else {
                        if self.isCable { self.suppressCable = true; DispatchQueue.main.async { self.cablePaused = true } }
                        self.teardown(notify: true)
                    }
                }
            }
        }
    }

    private func wrongCode() {
        PairingKeychain.delete(host: target.key)
        pairingKey = nil
        session = nil
        handshakeDone = false
        setState(.failed("Wrong pairing code"))
        askForCode(note: "Wrong pairing code.", wrong: true)
    }

    /// Starts (or restarts, with fresh keys) the key exchange. Idempotent while a session already exists.
    private func beginHandshake() {
        guard connReady, !awaitingUser, session == nil, let key = pairingKey else { return }
        let (s, clientHello) = SecureSession.client(pairingKey: key)
        session = s
        handshakeDone = false
        handshakeSentAt = Date()
        rawSend(Wire.encode(PacketHeader(type: .handshake), payload: clientHello))
    }

    private func handleHandshake(_ payload: Data) {
        guard let s = session, !handshakeDone, !s.isReady else { return }
        if s.finish(serverReply: payload) {
            handshakeDone = true
            replyAt = Date()
            lastRx = Date()
        }
    }

    // MARK: Timer (handshake / hello retries, pings, stats, watchdog)

    private func startTimer() {
        timer?.cancel()
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 0.25, repeating: 0.25)
        t.setEventHandler { [weak self] in self?.onTick() }
        t.resume()
        timer = t
    }

    private func onTick() {
        guard conn != nil else { return }
        let now = Date()
        if awaitingUser {
            lastRx = now
            connectedAt = now
            return
        }
        tick += 1

        if connReady && pairingKey != nil {
            if !handshakeDone {
                if session == nil {
                    beginHandshake()
                } else if !isCable && now.timeIntervalSince(handshakeSentAt) > 1.0 {
                    session = nil
                    beginHandshake()
                }
            } else if !gotWelcome, let h = hello {
                send(.hello, payload: h.encode())
                if !sealedOK, let r = replyAt {
                    let waited = now.timeIntervalSince(r)
                    if !handshakeRetried && waited > 2.0 {
                        // One fresh handshake in case a stale reply gave us mismatching keys.
                        handshakeRetried = true
                        session = nil
                        handshakeDone = false
                        beginHandshake()
                        return
                    }
                    if waited > 4.0 {
                        wrongCode()
                        return
                    }
                }
            } else if tick % 4 == 0 {
                let ms = UInt32(truncatingIfNeeded: DispatchTime.now().uptimeNanoseconds / 1_000_000)
                send(.ping, id: ms)
                let fps = statFrames, mbps = Double(statBytes) * 8 / 1e6
                statFrames = 0; statBytes = 0
                let text = String(format: "%@ · %d fps · %.1f Mbps · %d ms", isCable ? "Cable" : "Wi-Fi", fps, mbps, rttMs)
                DispatchQueue.main.async { self.statsText = text }
            }
        }

        // Watchdog: hosts may spend ~10 s probing encoders before the first frame; once streaming be quick.
        if !gotWelcome {
            let since = now.timeIntervalSince(handshakeDone ? max(lastRx, connectedAt) : connectedAt)
            if since > 15 { fail(handshakeDone ? "The host did not answer" : "Could not reach the host") }
        } else if !gotFirstFrame {
            if now.timeIntervalSince(welcomeAt) > 15 { fail("The host did not start streaming") }
        } else if now.timeIntervalSince(lastRx) > 4 {
            fail("Lost connection to the host")
        }
    }

    // MARK: Send

    private func rawSend(_ pkt: Data) {
        guard let c = conn else { return }
        if isCable {
            c.send(content: Deframer.frame(pkt), completion: .contentProcessed({ _ in }))
        } else {
            c.send(content: pkt, completion: .idempotent)
        }
    }

    /// Seals and sends. Dropped silently until the handshake finished.
    private func send(_ type: PacketType, id: UInt32 = 0, payload: Data = Data()) {
        guard let s = session, s.isReady else { return }
        let plain = Wire.encode(PacketHeader(type: type, id: id), payload: payload)
        if let sealed = s.seal(plain) { rawSend(sealed) }
    }

    func sendInput(_ e: InputEvent) {
        queue.async {
            guard self.gotWelcome, let s = self.session, s.isReady else { return }
            self.inputSeq &+= 1
            let plain = Wire.encode(PacketHeader(type: .input, id: self.inputSeq), payload: e.encode())
            guard let first = s.seal(plain) else { return }
            self.rawSend(first)
            if e.isDiscrete && !self.isCable {
                // Over Wi-Fi, key / button events are sent twice (sealed separately) so one lost datagram can't cause a stuck key.
                self.queue.asyncAfter(deadline: .now() + 0.006) {
                    if let again = s.seal(plain) { self.rawSend(again) }
                }
            }
        }
    }

    // MARK: Receive

    private func receiveDatagram(_ c: NWConnection) {
        c.receiveMessage { [weak self, weak c] data, _, _, error in
            guard let self, let c, self.conn === c else { return }
            if let data { self.process(data) }
            if error == nil { self.receiveDatagram(c) } else { self.fail("Connection closed") }
        }
    }

    private func receiveStream(_ c: NWConnection) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self, weak c] data, _, isComplete, error in
            guard let self, let c, self.conn === c else { return }
            if let data, !data.isEmpty {
                for pkt in self.deframer.push(data) { self.process(pkt) }
            }
            if error != nil || isComplete { self.fail("Cable disconnected"); return }
            self.receiveStream(c)
        }
    }

    /// One datagram / one deframed packet. Only sealed packets that authenticate are trusted;
    /// the plain handshake reply is the single other thing we act on.
    private func process(_ raw: Data) {
        if SecureSession.isSealed(raw) {
            guard let s = session, s.isReady, let plain = s.open(raw), let (h, payload) = Wire.decode(plain) else { return }
            sealedOK = true
            lastRx = Date()
            handle(h, payload)
        } else if let (h, payload) = Wire.decode(raw), h.type == .handshake {
            handleHandshake(payload)
        }
    }

    private func handle(_ h: PacketHeader, _ payload: Data) {
        switch h.type {
        case .welcome:
            guard let w = WelcomeInfo.decode(payload), !gotWelcome else { return }
            gotWelcome = true
            gotFirstFrame = false
            welcomeAt = Date()
            needKey = true
            formatDesc = nil
            lastSets = []
            reassembler.reset()

            let wantAudio = (w.caps & WelcomeInfo.capAudio) != 0 && (hello?.wantsAudio ?? false)
            if wantAudio {
                audio.muted = !LumenSettings.audioOn
                if !audioRunning { audio.start(); audioRunning = true }
            } else if audioRunning {
                audio.stop(); audioRunning = false
            }

            let t = target
            if let a = t.address, !a.isEmpty {
                let rec = RecentHost(name: t.name, address: a, key: t.key)
                DispatchQueue.main.async { RecentHosts.add(rec) }
            }
            DispatchQueue.main.async {
                self.videoSize = CGSize(width: Int(w.width), height: Int(w.height))
                self.hostCaps = w.caps
                self.hostSourceSize = CGSize(width: Int(w.sourceWidth), height: Int(w.sourceHeight))
                self.hostMode = w.mode
                self.state = .streaming
            }
        case .video:
            guard gotWelcome, let frame = reassembler.add(h, payload) else { return }
            handleVideo(frame)
        case .audio:
            if audioRunning { audio.push(payload) }
        case .status:
            guard let level = payload.first else { return }
            let text = String(decoding: payload.dropFirst(), as: UTF8.self)
            if level >= 1 {
                if isCable {
                    suppressCable = true
                    DispatchQueue.main.async { self.cablePaused = true }
                }
                fail(text.isEmpty ? "The host reported an error" : text)
            } else if !text.isEmpty {
                showToast(text)
            }
        case .ping:
            let now = UInt32(truncatingIfNeeded: DispatchTime.now().uptimeNanoseconds / 1_000_000)
            rttMs = Int(now &- h.id)
        default:
            break
        }
    }

    private func handleVideo(_ f: Reassembler.Frame) {
        let isKey = (f.flags & 1) != 0
        if f.gap { needKey = true }

        var r = ByteReader(f.data)
        if isKey {
            var sets = [Data]()
            let n = Int(r.u8() ?? 0)
            for _ in 0..<n {
                guard let l = r.u16(), let s = r.take(Int(l)) else { return }
                sets.append(s)
            }
            if sets != lastSets || formatDesc == nil {
                guard let fd = SampleFactory.formatDescription(parameterSets: sets) else { return }
                formatDesc = fd
                lastSets = sets
            }
            needKey = false
        }

        if needKey { requestKeyframe(); return }
        guard let fd = formatDesc, let sb = SampleFactory.sampleBuffer(avcc: r.rest(), format: fd) else { return }
        gotFirstFrame = true
        statFrames += 1
        statBytes += f.data.count
        VideoRenderer.shared.enqueue(sb)
    }

    private func requestKeyframe() {
        guard Date().timeIntervalSince(lastKeyRequest) > 0.1 else { return }
        lastKeyRequest = Date()
        send(.keyframeRequest)
    }
}
