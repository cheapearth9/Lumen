import Foundation

// MARK: - Constants

let lumenPort: UInt16 = 47900
let lumenBonjourType = "_lumen._udp"
let lumenCablePort: UInt16 = 47901   // iOS app listens here for USB (usbmuxd) tunnels

enum PacketType: UInt8 {
    case hello = 1          // client -> host : desired size / fps / bitrate
    case welcome = 2        // host -> client : actual stream size
    case video = 3          // host -> client : one HEVC access unit (fragmented)
    case input = 4          // client -> host : mouse / keyboard event
    case keyframeRequest = 5
    case ping = 6           // echoed by host, used for RTT
    case bye = 7
}

// MARK: - Byte helpers

struct ByteWriter {
    var data = Data()
    mutating func u8(_ v: UInt8) { data.append(v) }
    mutating func u16(_ v: UInt16) { data.append(UInt8(v >> 8)); data.append(UInt8(v & 0xFF)) }
    mutating func u32(_ v: UInt32) {
        data.append(UInt8((v >> 24) & 0xFF)); data.append(UInt8((v >> 16) & 0xFF))
        data.append(UInt8((v >> 8) & 0xFF)); data.append(UInt8(v & 0xFF))
    }
    mutating func i16(_ v: Int16) { u16(UInt16(bitPattern: v)) }
    mutating func bytes(_ d: Data) { data.append(d) }
}

struct ByteReader {
    private let data: Data
    private var pos = 0
    init(_ d: Data) { data = d }
    var remaining: Int { data.count - pos }

    mutating func u8() -> UInt8? {
        guard remaining >= 1 else { return nil }
        let v = data[data.startIndex + pos]; pos += 1; return v
    }
    mutating func u16() -> UInt16? {
        guard let a = u8(), let b = u8() else { return nil }
        return (UInt16(a) << 8) | UInt16(b)
    }
    mutating func u32() -> UInt32? {
        guard let a = u16(), let b = u16() else { return nil }
        return (UInt32(a) << 16) | UInt32(b)
    }
    mutating func i16() -> Int16? {
        guard let v = u16() else { return nil }
        return Int16(bitPattern: v)
    }
    mutating func take(_ n: Int) -> Data? {
        guard n >= 0, remaining >= n else { return nil }
        let s = data.startIndex + pos
        pos += n
        return Data(data[s..<(s + n)])
    }
    mutating func rest() -> Data { take(remaining) ?? Data() }
}

// MARK: - Packet header (16 bytes)

struct PacketHeader {
    var type: PacketType
    var flags: UInt8 = 0          // video: bit0 = keyframe
    var id: UInt32 = 0            // video: frame id, input: sequence number, ping: timestamp
    var fragIndex: UInt16 = 0
    var fragCount: UInt16 = 1
    var timestampMs: UInt32 = 0
}

enum Wire {
    static let magic: UInt16 = 0x4C4D // "LM"
    static let maxPayload = 1180      // keeps datagrams under typical 1280 MTU

    static func encode(_ h: PacketHeader, payload: Data = Data()) -> Data {
        var w = ByteWriter()
        w.u16(magic); w.u8(h.type.rawValue); w.u8(h.flags)
        w.u32(h.id); w.u16(h.fragIndex); w.u16(h.fragCount); w.u32(h.timestampMs)
        w.bytes(payload)
        return w.data
    }

    static func decode(_ d: Data) -> (PacketHeader, Data)? {
        var r = ByteReader(d)
        guard let m = r.u16(), m == magic,
              let t = r.u8(), let type = PacketType(rawValue: t),
              let flags = r.u8(), let id = r.u32(),
              let fi = r.u16(), let fc = r.u16(), let ts = r.u32(),
              fc >= 1, fi < fc else { return nil }
        let h = PacketHeader(type: type, flags: flags, id: id, fragIndex: fi, fragCount: fc, timestampMs: ts)
        return (h, r.rest())
    }

    static func fragment(type: PacketType, flags: UInt8, id: UInt32, timestampMs: UInt32, payload: Data) -> [Data] {
        let count = max(1, (payload.count + maxPayload - 1) / maxPayload)
        var out = [Data]()
        out.reserveCapacity(count)
        for i in 0..<count {
            let s = payload.startIndex + i * maxPayload
            let e = min(payload.endIndex, s + maxPayload)
            let h = PacketHeader(type: type, flags: flags, id: id,
                                 fragIndex: UInt16(i), fragCount: UInt16(count), timestampMs: timestampMs)
            out.append(encode(h, payload: payload.subdata(in: s..<e)))
        }
        return out
    }
}

// MARK: - Reassembly of fragmented frames

final class Reassembler {
    struct Frame {
        let id: UInt32
        let flags: UInt8
        let timestampMs: UInt32
        let data: Data
        let gap: Bool       // true if frames were skipped before this one
    }

    private struct Partial {
        var parts: [Data?]
        var received = 0
        var flags: UInt8
        var ts: UInt32
    }

    private var partials: [UInt32: Partial] = [:]
    private var last: UInt32?

    func reset() { partials.removeAll(); last = nil }

    func add(_ h: PacketHeader, _ payload: Data) -> Frame? {
        if let l = last, Int32(bitPattern: h.id &- l) <= 0 { return nil } // old / duplicate

        var p = partials[h.id] ?? Partial(parts: Array(repeating: nil, count: Int(h.fragCount)),
                                          flags: h.flags, ts: h.timestampMs)
        guard p.parts.count == Int(h.fragCount), Int(h.fragIndex) < p.parts.count else { return nil }
        if p.parts[Int(h.fragIndex)] == nil {
            p.parts[Int(h.fragIndex)] = payload
            p.received += 1
        }

        if p.received == p.parts.count {
            var data = Data()
            for part in p.parts { if let part { data.append(part) } }
            let gap: Bool
            if let l = last { gap = (h.id &- l) != 1 } else { gap = false }
            last = h.id
            let done = h.id
            partials = partials.filter { Int32(bitPattern: $0.key &- done) > 0 }
            return Frame(id: h.id, flags: p.flags, timestampMs: p.ts, data: data, gap: gap)
        }

        partials[h.id] = p
        if partials.count > 32, let oldest = partials.keys.min(by: { Int32(bitPattern: $0 &- $1) < 0 }) {
            partials.removeValue(forKey: oldest)
        }
        return nil
    }
}

// MARK: - Handshake payloads

struct HelloInfo: Equatable {
    var width: UInt16
    var height: UInt16
    var fps: UInt8
    var bitrateMbps: UInt16

    func encode() -> Data {
        var w = ByteWriter()
        w.u16(width); w.u16(height); w.u8(fps); w.u16(bitrateMbps)
        return w.data
    }
    static func decode(_ d: Data) -> HelloInfo? {
        var r = ByteReader(d)
        guard let w = r.u16(), let h = r.u16(), let f = r.u8(), let b = r.u16() else { return nil }
        return HelloInfo(width: w, height: h, fps: f, bitrateMbps: b)
    }
}

struct WelcomeInfo {
    var width: UInt16
    var height: UInt16
    var fps: UInt8

    func encode() -> Data {
        var w = ByteWriter()
        w.u16(width); w.u16(height); w.u8(fps)
        return w.data
    }
    static func decode(_ d: Data) -> WelcomeInfo? {
        var r = ByteReader(d)
        guard let w = r.u16(), let h = r.u16(), let f = r.u8() else { return nil }
        return WelcomeInfo(width: w, height: h, fps: f)
    }
}

// MARK: - Input events

enum InputEvent {
    case mouseRel(dx: Int16, dy: Int16)
    case mouseAbs(x: UInt16, y: UInt16)       // 0...65535 across the video frame
    case button(UInt8, down: Bool)            // 0 left, 1 right, 2 middle
    case scroll(dx: Int16, dy: Int16)
    case key(UInt16, down: Bool)              // macOS virtual key code

    /// Events that must not be lost (sent twice, de-duplicated on the host).
    var isDiscrete: Bool {
        switch self {
        case .button, .key: return true
        default: return false
        }
    }

    func encode() -> Data {
        var w = ByteWriter()
        switch self {
        case .mouseRel(let dx, let dy): w.u8(1); w.i16(dx); w.i16(dy)
        case .mouseAbs(let x, let y):   w.u8(2); w.u16(x); w.u16(y)
        case .button(let b, let down):  w.u8(3); w.u8(b); w.u8(down ? 1 : 0)
        case .scroll(let dx, let dy):   w.u8(4); w.i16(dx); w.i16(dy)
        case .key(let c, let down):     w.u8(5); w.u16(c); w.u8(down ? 1 : 0)
        }
        return w.data
    }

    static func decode(_ d: Data) -> InputEvent? {
        var r = ByteReader(d)
        guard let t = r.u8() else { return nil }
        switch t {
        case 1: if let a = r.i16(), let b = r.i16() { return .mouseRel(dx: a, dy: b) }
        case 2: if let a = r.u16(), let b = r.u16() { return .mouseAbs(x: a, y: b) }
        case 3: if let a = r.u8(), let b = r.u8() { return .button(a, down: b != 0) }
        case 4: if let a = r.i16(), let b = r.i16() { return .scroll(dx: a, dy: b) }
        case 5: if let a = r.u16(), let b = r.u8() { return .key(a, down: b != 0) }
        default: break
        }
        return nil
    }
}

// MARK: - Stream framing (used over USB / TCP, where there are no datagram boundaries)

final class Deframer {
    private var buf = Data()

    /// Wire format on a stream: u32 big-endian length, then one packet.
    static func frame(_ pkt: Data) -> Data {
        var w = ByteWriter()
        w.u32(UInt32(pkt.count))
        w.bytes(pkt)
        return w.data
    }

    func push(_ d: Data) -> [Data] {
        buf.append(d)
        var out = [Data]()
        while buf.count >= 4 {
            let s = buf.startIndex
            let len = (Int(buf[s]) << 24) | (Int(buf[s + 1]) << 16) | (Int(buf[s + 2]) << 8) | Int(buf[s + 3])
            if len < 0 || len > 64 * 1024 * 1024 { buf.removeAll(); break }
            guard buf.count >= 4 + len else { break }
            out.append(Data(buf[(s + 4)..<(s + 4 + len)]))
            buf.removeSubrange(s..<(s + 4 + len))
        }
        return out
    }
}
