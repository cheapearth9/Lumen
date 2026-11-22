import Foundation
import Network
import CoreMedia
import Combine

struct DiscoveredServer: Identifiable, Hashable {
    let id: String
    let name: String
    let endpoint: NWEndpoint
}

/// Networking + reassembly + hand-off to the display layers. All mutable state lives on `queue`.
/// Two transports share one protocol:
///  - Wi-Fi / Ethernet: UDP datagrams to the Mac found over Bonjour (or typed IP).
///  - Cable: the app listens on a TCP port; the Mac host reaches it through usbmuxd over USB.
final class StreamClient: ObservableObject {
    enum State: Equatable {
        case idle, connecting, streaming
        case failed(String)
    }

    @Published var state: State = .idle
    @Published var servers: [DiscoveredServer] = []
    @Published var statsText = ""
    @Published var videoSize = CGSize(width: 16, height: 9)
    @Published var cablePaused = false

    /// Supplies the resolution/fps/bitrate to request. Called off the main thread; hops to main internally.
    var helloProvider: (() -> HelloInfo)?

    private let queue = DispatchQueue(label: "lumen.client", qos: .userInteractive)
    private var browser: NWBrowser?
    private var cableListener: NWListener?
    private var conn: NWConnection?
    private var isCable = false
    private var suppressCable = false
    private var timer: DispatchSourceTimer?
    private let deframer = Deframer()

    private var reassembler = Reassembler()
    private var formatDesc: CMVideoFormatDescription?
    private var lastSets = [Data]()
    private var needKey = true
    private var lastKeyRequest = Date.distantPast
    private var hello: HelloInfo?
    private var gotWelcome = false
    private var lastRx = Date()
    private var tick = 0
    private var inputSeq: UInt32 = 0

    private var statFrames = 0, statBytes = 0, rttMs = 0

    // MARK: Start (idempotent; call again when the app becomes active)

    func start() {
        startBrowsing()
        queue.async { self.startCableListener() }
    }

    // MARK: Wi-Fi discovery

    private func startBrowsing() {
        queue.async {
            guard self.browser == nil else { return }
            let b = NWBrowser(for: .bonjour(type: lumenBonjourType, domain: nil), using: NWParameters())
            b.browseResultsChangedHandler = { [weak self] results, _ in
                let list: [DiscoveredServer] = results.compactMap { r in
                    if case let .service(name, _, _, _) = r.endpoint {
                        return DiscoveredServer(id: name, name: name, endpoint: r.endpoint)
                    }
                    return nil
                }.sorted { $0.name < $1.name }
                DispatchQueue.main.async { self?.servers = list }
            }
            b.stateUpdateHandler = { [weak self] st in
                if case .failed = st { self?.queue.async { self?.browser?.cancel(); self?.browser = nil } }
            }
            b.start(queue: self.queue)
            self.browser = b
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

    /// The Mac host connected through the USB tunnel. Auto-connect only when we are idle.
    private func cableAccepted(_ c: NWConnection) {
        guard conn == nil, !suppressCable, let provider = helloProvider else { c.cancel(); return }
        let h = DispatchQueue.main.sync { provider() }
        begin(c, cable: true, hello: h)
    }

    /// Resume auto-connect after the user disconnected from a cable session.
    func resumeCable() {
        queue.async { self.suppressCable = false }
        DispatchQueue.main.async { self.cablePaused = false }
    }

    // MARK: Wi-Fi connect

    func connect(host: String, hello: HelloInfo) {
        let ep = NWEndpoint.hostPort(host: NWEndpoint.Host(host.trimmingCharacters(in: .whitespaces)),
                                     port: NWEndpoint.Port(rawValue: lumenPort)!)
        connect(endpoint: ep, hello: hello)
    }

    func connect(endpoint: NWEndpoint, hello: HelloInfo) {
        queue.async {
            let params = NWParameters.udp
            params.serviceClass = .interactiveVideo
            self.begin(NWConnection(to: endpoint, using: params), cable: false, hello: hello)
        }
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

    // MARK: Session lifecycle (queue)

    private func begin(_ c: NWConnection, cable: Bool, hello: HelloInfo) {
        teardown(notify: false)
        self.hello = hello
        gotWelcome = false
        needKey = true
        formatDesc = nil
        lastSets = []
        reassembler.reset()
        lastRx = Date()
        tick = 0
        isCable = cable
        setState(.connecting)

        c.stateUpdateHandler = { [weak self, weak c] st in
            guard let self, let c, self.conn === c else { return }
            switch st {
            case .ready:
                if cable { self.receiveStream(c) } else { self.receiveDatagram(c) }
            case .failed(let e):
                self.fail("Connection failed: \(e.localizedDescription)")
            default: break
            }
        }
        conn = c
        c.start(queue: queue)
        startTimer()
    }

    private func teardown(notify: Bool) {
        timer?.cancel(); timer = nil
        conn?.cancel(); conn = nil
        isCable = false
        if notify { setState(.idle) }
        DispatchQueue.main.async { VideoRenderer.shared.clear() }
    }

    private func fail(_ msg: String) {
        teardown(notify: false)
        setState(.failed(msg))
    }

    private func setState(_ s: State) { DispatchQueue.main.async { self.state = s } }

    // MARK: Timer (hello retries, pings, stats, watchdog)

    private func startTimer() {
        timer?.cancel()
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 0.25, repeating: 0.25)
        t.setEventHandler { [weak self] in self?.onTick() }
        t.resume()
        timer = t
    }

    private func onTick() {
        tick += 1
        if !gotWelcome, let h = hello {
            send(.hello, payload: h.encode())
        } else if tick % 4 == 0 {
            let now = UInt32(truncatingIfNeeded: DispatchTime.now().uptimeNanoseconds / 1_000_000)
            send(.ping, id: now)
            let fps = statFrames, mbps = Double(statBytes) * 8 / 1e6
            statFrames = 0; statBytes = 0
            let text = String(format: "%@ · %d fps · %.1f Mbps · %d ms", isCable ? "Cable" : "Wi-Fi", fps, mbps, rttMs)
            DispatchQueue.main.async { self.statsText = text }
        }
        if Date().timeIntervalSince(lastRx) > 5 { fail("Lost connection to the host") }
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

    private func send(_ type: PacketType, id: UInt32 = 0, payload: Data = Data()) {
        rawSend(Wire.encode(PacketHeader(type: type, id: id), payload: payload))
    }

    func sendInput(_ e: InputEvent) {
        queue.async {
            guard self.gotWelcome else { return }
            self.inputSeq &+= 1
            let data = Wire.encode(PacketHeader(type: .input, id: self.inputSeq), payload: e.encode())
            self.rawSend(data)
            if e.isDiscrete && !self.isCable {
                // Over Wi-Fi, key / button events are sent twice so one lost datagram can't cause a stuck key.
                self.queue.asyncAfter(deadline: .now() + 0.006) { self.rawSend(data) }
            }
        }
    }

    // MARK: Receive

    private func receiveDatagram(_ c: NWConnection) {
        c.receiveMessage { [weak self, weak c] data, _, _, error in
            guard let self, let c, self.conn === c else { return }
            if let data, let (h, payload) = Wire.decode(data) {
                self.lastRx = Date()
                self.handle(h, payload)
            }
            if error == nil { self.receiveDatagram(c) } else { self.fail("Connection closed") }
        }
    }

    private func receiveStream(_ c: NWConnection) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self, weak c] data, _, isComplete, error in
            guard let self, let c, self.conn === c else { return }
            if let data, !data.isEmpty {
                self.lastRx = Date()
                for pkt in self.deframer.push(data) {
                    if let (h, payload) = Wire.decode(pkt) { self.handle(h, payload) }
                }
            }
            if error != nil || isComplete { self.fail("Cable disconnected"); return }
            self.receiveStream(c)
        }
    }

    private func handle(_ h: PacketHeader, _ payload: Data) {
        switch h.type {
        case .welcome:
            guard let w = WelcomeInfo.decode(payload) else { return }
            if !gotWelcome {
                gotWelcome = true
                needKey = true
                DispatchQueue.main.async {
                    self.videoSize = CGSize(width: Int(w.width), height: Int(w.height))
                    self.state = .streaming
                }
            }
        case .video:
            guard gotWelcome, let frame = reassembler.add(h, payload) else { return }
            handleVideo(frame)
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
