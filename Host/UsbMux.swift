import Foundation
import Darwin

/// Opens TCP tunnels to a USB-connected iPhone/iPad through the system `usbmuxd` daemon
/// (the same mechanism Xcode and iproxy use). The iOS app listens on `lumenCablePort`;
/// this watcher connects to it whenever a device is attached and the host has no client.
final class UsbMuxWatcher {
    var wantsTunnel: () -> Bool = { true }
    var onTunnel: ((Int32) -> Void)?

    private let lock = NSLock()
    private var devices = [Int]()          // DeviceIDs of USB-attached devices
    private var running = false
    private var generation = 0
    private var listenFD: Int32 = -1
    private static let socketPath = "/var/run/usbmuxd"

    func start() {
        lock.lock()
        guard !running else { lock.unlock(); return }
        running = true
        generation += 1
        let gen = generation
        lock.unlock()

        let listen = Thread { [self] in listenLoop(gen) }
        listen.qualityOfService = .utility
        listen.start()

        let connect = Thread { [self] in connectLoop(gen) }
        connect.qualityOfService = .userInitiated
        connect.start()
    }

    func stop() {
        lock.lock()
        running = false
        let fd = listenFD
        lock.unlock()
        if fd >= 0 { shutdown(fd, SHUT_RDWR) }
    }

    private func alive(_ gen: Int) -> Bool { lock.lock(); defer { lock.unlock() }; return running && generation == gen }

    // MARK: Device list

    private func listenLoop(_ gen: Int) {
        while alive(gen) {
            if let fd = Self.openDaemon() {
                lock.lock(); listenFD = fd; lock.unlock()
                if Self.send(fd, ["MessageType": "Listen", "ClientVersionString": "Lumen", "ProgName": "Lumen"], tag: 1) {
                    while alive(gen), let msg = Self.read(fd) {
                        switch msg["MessageType"] as? String {
                        case "Attached":
                            let props = msg["Properties"] as? [String: Any]
                            if let id = msg["DeviceID"] as? Int, (props?["ConnectionType"] as? String) == "USB" {
                                lock.lock(); if !devices.contains(id) { devices.append(id) }; lock.unlock()
                            }
                        case "Detached":
                            if let id = msg["DeviceID"] as? Int {
                                lock.lock(); devices.removeAll { $0 == id }; lock.unlock()
                            }
                        default: break
                        }
                    }
                }
                lock.lock(); listenFD = -1; devices.removeAll(); lock.unlock()
                Darwin.close(fd)
            }
            // usbmuxd missing or connection dropped: retry shortly.
            var waited = 0
            while waited < 20 && alive(gen) { usleep(100_000); waited += 1 }
        }
    }

    // MARK: Tunnels

    private func connectLoop(_ gen: Int) {
        while alive(gen) {
            usleep(1_500_000)
            guard wantsTunnel() else { continue }
            lock.lock(); let ids = devices; lock.unlock()
            for id in ids {
                if let fd = Self.openTunnel(deviceID: id, port: lumenCablePort) {
                    onTunnel?(fd)
                    break
                }
            }
        }
    }

    private static func openTunnel(deviceID: Int, port: UInt16) -> Int32? {
        guard let fd = openDaemon() else { return nil }
        let swapped = Int(port.byteSwapped)    // usbmuxd wants the port in network byte order
        let req: [String: Any] = ["MessageType": "Connect", "ClientVersionString": "Lumen", "ProgName": "Lumen",
                                  "DeviceID": deviceID, "PortNumber": swapped]
        guard send(fd, req, tag: 2), let reply = read(fd),
              (reply["MessageType"] as? String) == "Result",
              (reply["Number"] as? Int) == 0 else {
            Darwin.close(fd)
            return nil
        }
        return fd    // from here on the socket is a raw byte pipe to the device
    }

    // MARK: usbmuxd plist protocol (16-byte little-endian header + XML plist)

    private static func openDaemon() -> Int32? {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let cap = MemoryLayout.size(ofValue: addr.sun_path)
        withUnsafeMutablePointer(to: &addr.sun_path) { p in
            p.withMemoryRebound(to: CChar.self, capacity: cap) { dst in
                _ = socketPath.withCString { strncpy(dst, $0, cap - 1) }
            }
        }
        let len = socklen_t(MemoryLayout<sockaddr_un>.size)
        let r = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, len) }
        }
        if r != 0 { Darwin.close(fd); return nil }
        return fd
    }

    private static func send(_ fd: Int32, _ dict: [String: Any], tag: UInt32) -> Bool {
        guard let body = try? PropertyListSerialization.data(fromPropertyList: dict, format: .xml, options: 0) else { return false }
        var pkt = Data()
        func le(_ v: UInt32) { var x = v.littleEndian; pkt.append(Data(bytes: &x, count: 4)) }
        le(UInt32(16 + body.count)); le(1); le(8); le(tag)
        pkt.append(body)
        return writeAll(fd, pkt)
    }

    private static func read(_ fd: Int32) -> [String: Any]? {
        guard let hdr = readExactly(fd, 16) else { return nil }
        let b = [UInt8](hdr)
        let len = Int(b[0]) | (Int(b[1]) << 8) | (Int(b[2]) << 16) | (Int(b[3]) << 24)
        guard len >= 16, len < (1 << 20) else { return nil }
        guard let body = readExactly(fd, len - 16) else { return nil }
        return (try? PropertyListSerialization.propertyList(from: body, options: [], format: nil)) as? [String: Any]
    }

    private static func readExactly(_ fd: Int32, _ count: Int) -> Data? {
        if count == 0 { return Data() }
        var out = Data(count: count)
        var got = 0
        while got < count {
            let n = out.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress! + got, count - got) }
            if n > 0 { got += n }
            else if n < 0 && errno == EINTR { continue }
            else { return nil }
        }
        return out
    }

    private static func writeAll(_ fd: Int32, _ d: Data) -> Bool {
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
