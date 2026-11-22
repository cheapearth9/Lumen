import Foundation
import Network
import Darwin

// MARK: - Links (one per transport)

protocol HostLink: AnyObject {
    /// Sends already-built packets. Droppable packets (video) may be discarded under back-pressure.
    func send(_ packets: [Data], droppable: Bool)
    func close()
}

/// Wi-Fi / Ethernet: one datagram per packet.
final class UDPLink: HostLink {
    let conn: NWConnection
    init(_ c: NWConnection) { conn = c }

    func send(_ packets: [Data], droppable: Bool) {
        let c = conn
        c.batch { for p in packets { c.send(content: p, completion: .idempotent) } }
    }
    func close() { conn.cancel() }
}

/// Cable: reliable byte stream through a usbmuxd tunnel, length-prefixed packets.
final class TCPLink: HostLink {
    private let fd: Int32
    private let writeQueue = DispatchQueue(label: "lumen.usb.write", qos: .userInteractive)
    private let lock = NSLock()
    private var pending = 0
    private var closed = false
    private let deframer = Deframer()
    let created = Date()

    var onPacket: ((Data) -> Void)?
    var onClosed: (() -> Void)?

    init(fd: Int32) {
        self.fd = fd
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
    }
    deinit { Darwin.close(fd) }

    func start() {
        let t = Thread { [self] in
            var buf = [UInt8](repeating: 0, count: 65536)
            while true {
                let n = buf.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, $0.count) }
                if n > 0 {
                    for p in deframer.push(Data(buf[0..<n])) { onPacket?(p) }
                } else if n < 0 && errno == EINTR {
                    continue
                } else {
                    break
                }
            }
            lock.lock(); closed = true; lock.unlock()
            onClosed?()
        }
        t.qualityOfService = .userInteractive
        t.start()
    }

    func send(_ packets: [Data], droppable: Bool) {
        let size = packets.reduce(0) { $0 + $1.count + 4 }
        lock.lock()
        // Back-pressure: if the device can't keep up, skip whole video frames. The client sees the gap
        // in frame ids and asks for a keyframe.
        if closed || (droppable && pending > 1_500_000) { lock.unlock(); return }
        pending += size
        lock.unlock()

        writeQueue.async { [self] in
            var out = Data(capacity: size)
            for p in packets { out.append(Deframer.frame(p)) }
            let ok = writeAll(out)
            lock.lock(); pending -= size; lock.unlock()
            if !ok { close() }
        }
    }

    func close() {
        lock.lock(); closed = true; lock.unlock()
        shutdown(fd, SHUT_RDWR)   // wakes the reader, which then reports onClosed
    }

    private func writeAll(_ d: Data) -> Bool {
        var off = 0
        return d.withUnsafeBytes { raw -> Bool in
            guard let base = raw.baseAddress else { return true }
            while off < raw.count {
                let n = Darwin.write(fd, base + off, raw.count - off)
                if n > 0 { off += n }
                else if n < 0 && errno == EINTR { continue }
                else { return false }
            }
            return true
        }
    }
}

// MARK: - Server

/// Serves a single client at a time, over UDP (Bonjour-advertised) or a USB tunnel.
/// A link only becomes the active client once it sends a valid hello.
final class HostServer {
    private let queue = DispatchQueue(label: "lumen.server", qos: .userInteractive)
    private var listener: NWListener?
    private var client: HostLink?
    private var tunnels = [TCPLink]()
    private var running = false
    private var frameCounter: UInt32 = 0
    private var lastHeard = Date()
    private var watchdog: DispatchSourceTimer?
    private var recent = Set<UInt32>()
    private var recentOrder = [UInt32]()

    private let flag = NSLock()
    private var wants = false
    /// True while no client is connected and no tunnel is waiting for its hello.
    var wantsTunnel: Bool { flag.lock(); defer { flag.unlock() }; return wants }

    // All callbacks are invoked on the server queue.
    var onHello: ((HelloInfo) -> Void)?
    var onInput: ((InputEvent) -> Void)?
    var onKeyframeRequest: (() -> Void)?
    var onClientChanged: ((Bool) -> Void)?
    var onState: ((String) -> Void)?

    func start(name: String) throws {
        stop()
        let params = NWParameters.udp
        params.serviceClass = .interactiveVideo
        guard let port = NWEndpoint.Port(rawValue: lumenPort) else { return }
        let l = try NWListener(using: params, on: port)
        l.service = NWListener.Service(name: name, type: lumenBonjourType)
        l.stateUpdateHandler = { [weak self] st in
            switch st {
            case .ready: self?.onState?("Waiting for a client (Wi-Fi or cable)…")
            case .failed(let e): self?.onState?("Listener failed: \(e)")
            default: break
            }
        }
        l.newConnectionHandler = { [weak self] c in self?.accept(c) }
        l.start(queue: queue)
        listener = l

        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 1, repeating: 1)
        t.setEventHandler { [weak self] in self?.tick() }
        t.resume()
        watchdog = t

        queue.async { self.running = true; self.updateWants() }
    }

    func stop() {
        watchdog?.cancel(); watchdog = nil
        listener?.cancel(); listener = nil
        queue.async {
            self.running = false
            self.tunnels.forEach { $0.close() }
            self.tunnels.removeAll()
            if let c = self.client { self.dropClient(c) }
            self.updateWants()
        }
    }

    private func tick() {
        if let c = client, Date().timeIntervalSince(lastHeard) > 5 { dropClient(c) }
        // A tunnel that never says hello (app busy / not ready) is closed so the next attempt starts clean.
        for t in tunnels where Date().timeIntervalSince(t.created) > 4 { t.close() }
    }

    private func updateWants() {
        flag.lock(); wants = running && client == nil && tunnels.isEmpty; flag.unlock()
    }

    // MARK: Wi-Fi

    private func accept(_ c: NWConnection) {
        let link = UDPLink(c)
        c.stateUpdateHandler = { [weak self] st in
            switch st {
            case .failed, .cancelled: self?.linkClosed(link)
            default: break
            }
        }
        c.start(queue: queue)
        receive(link)
    }

    private func receive(_ link: UDPLink) {
        link.conn.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data { self.packet(data, from: link) }
            if error == nil { self.receive(link) } else { self.linkClosed(link) }
        }
    }

    // MARK: Cable

    /// Called by the usbmux watcher with a connected tunnel socket.
    func addTunnel(fd: Int32) {
        let link = TCPLink(fd: fd)
        link.onPacket = { [weak self, weak link] d in
            guard let link else { return }
            self?.queue.async { self?.packet(d, from: link) }
        }
        link.onClosed = { [weak self, weak link] in
            guard let link else { return }
            self?.queue.async { self?.linkClosed(link) }
        }
        queue.async {
            self.tunnels.append(link)
            self.updateWants()
            link.start()
        }
    }

    // MARK: Client management

    private func adopt(_ link: HostLink) {
        let hadClient = client != nil
        if let old = client, old !== link { old.close() }
        client = link
        lastHeard = Date()
        recent.removeAll(); recentOrder.removeAll()
        if let t = link as? TCPLink { tunnels.removeAll { $0 === t } }
        updateWants()
        if !hadClient { onClientChanged?(true) }
    }

    private func dropClient(_ link: HostLink) {
        guard client === link else { return }
        client = nil
        link.close()
        updateWants()
        onClientChanged?(false)
    }

    private func linkClosed(_ link: HostLink) {
        if let t = link as? TCPLink { tunnels.removeAll { $0 === t } }
        if client === link { dropClient(link) } else { updateWants() }
    }

    private func packet(_ data: Data, from link: HostLink) {
        guard let (h, payload) = Wire.decode(data) else { return }
        if client !== link {
            guard h.type == .hello else { return }
            adopt(link)
        }
        lastHeard = Date()

        switch h.type {
        case .hello:
            if let info = HelloInfo.decode(payload) { onHello?(info) }
        case .input:
            if isNew(h.id), let e = InputEvent.decode(payload) { onInput?(e) }
        case .keyframeRequest:
            onKeyframeRequest?()
        case .ping:
            link.send([Wire.encode(PacketHeader(type: .ping, id: h.id))], droppable: false)
        case .bye:
            dropClient(link)
        default:
            break
        }
    }

    /// Input events are sometimes sent twice over Wi-Fi; ignore sequence numbers we have already seen.
    private func isNew(_ seq: UInt32) -> Bool {
        if recent.contains(seq) { return false }
        recent.insert(seq); recentOrder.append(seq)
        if recentOrder.count > 512 { recent.remove(recentOrder.removeFirst()) }
        return true
    }

    // MARK: Sending

    func sendWelcome(_ w: WelcomeInfo) {
        queue.async {
            self.client?.send([Wire.encode(PacketHeader(type: .welcome), payload: w.encode())], droppable: false)
        }
    }

    func sendVideo(_ payload: Data, keyframe: Bool, timestampMs: UInt32) {
        queue.async {
            guard let c = self.client else { return }
            self.frameCounter &+= 1
            let frags = Wire.fragment(type: .video, flags: keyframe ? 1 : 0,
                                      id: self.frameCounter, timestampMs: timestampMs, payload: payload)
            c.send(frags, droppable: true)
        }
    }
}
