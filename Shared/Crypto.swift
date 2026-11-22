import Foundation
import CryptoKit
import Security

// End-to-end encryption for the Lumen protocol. Must stay byte-compatible with HostCommon/Crypto.cs.
//
//  * Pairing code: 10 random bytes shown on the host as 16 base32 characters (ABCD-EFGH-IJKL-MNOP).
//    The user types it into the iOS app once per host.
//  * Handshake (the only unencrypted packet type besides discover/announce), carried in PacketType.handshake:
//        client -> host : [0 role][1 version][65 P-256 public key (x9.63)][16 client nonce]
//        host -> client : [1 role][1 version][65 P-256 public key (x9.63)][16 server nonce]
//  * Keys: ikm  = ECDH raw shared secret (32 bytes) || pairing key (10 bytes)
//          salt = client nonce || server nonce
//          c2s / s2c = HKDF-SHA256(ikm, salt, info "lumen-v1 c2s" / "lumen-v1 s2c") -> 32 bytes each
//    A peer without the pairing code derives different keys, so nothing it sends authenticates.
//  * Sealed packet: [0x4C 0x45][seq u64 BE][ciphertext][16-byte tag], AES-256-GCM,
//    nonce = 4 zero bytes || seq (8 bytes BE), AAD = the first 10 bytes. The plaintext is a normal Lumen packet.

enum PairingCode {
    private static let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".utf8)

    static func generate() -> Data {
        var bytes = [UInt8](repeating: 0, count: 10)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes)
    }

    /// "ABCD-EFGH-IJKL-MNOP"
    static func format(_ key: Data) -> String {
        var out = [UInt8]()
        var buffer: UInt32 = 0
        var bits = 0
        for b in key {
            buffer = (buffer << 8) | UInt32(b)
            bits += 8
            while bits >= 5 {
                out.append(alphabet[Int((buffer >> UInt32(bits - 5)) & 31)])
                bits -= 5
            }
        }
        if bits > 0 { out.append(alphabet[Int((buffer << UInt32(5 - bits)) & 31)]) }
        let s = String(decoding: out, as: UTF8.self)
        return stride(from: 0, to: s.count, by: 4).map { i -> String in
            let a = s.index(s.startIndex, offsetBy: i)
            let b = s.index(a, offsetBy: min(4, s.count - i))
            return String(s[a..<b])
        }.joined(separator: "-")
    }

    /// Accepts any case, with or without dashes / spaces. Returns nil unless it is exactly 16 valid characters.
    static func parse(_ text: String) -> Data? {
        let chars = text.uppercased().utf8.filter { alphabet.contains($0) }
        guard chars.count == 16 else { return nil }
        var out = [UInt8]()
        var buffer: UInt32 = 0
        var bits = 0
        for c in chars {
            guard let v = alphabet.firstIndex(of: c) else { return nil }
            buffer = (buffer << 5) | UInt32(v)
            bits += 5
            if bits >= 8 {
                out.append(UInt8((buffer >> UInt32(bits - 8)) & 0xFF))
                bits -= 8
            }
        }
        return out.count == 10 ? Data(out) : nil
    }
}

/// Duplicate / replay filter for sealed packets (applied only after a packet authenticated).
struct ReplayWindow {
    private var highest: UInt64 = 0
    private var seen = Set<UInt64>()
    private let span: UInt64 = 1024

    mutating func accept(_ seq: UInt64) -> Bool {
        if seq + span <= highest { return false }
        if seen.contains(seq) { return false }
        seen.insert(seq)
        if seq > highest {
            highest = seq
            if seen.count > 2048 { seen = seen.filter { $0 + span > highest } }
        }
        return true
    }
}

final class SecureSession {
    private static let version: UInt8 = 1
    private static let sealedMagic: [UInt8] = [0x4C, 0x45]

    private let isClient: Bool
    private let psk: Data
    private var privateKey: P256.KeyAgreement.PrivateKey
    private var myNonce: Data
    private var sendKey: SymmetricKey?
    private var recvKey: SymmetricKey?
    private var sendSeq: UInt64 = 0
    private var window = ReplayWindow()
    private let lock = NSLock()

    private(set) var isReady = false

    private init(isClient: Bool, psk: Data) {
        self.isClient = isClient
        self.psk = psk
        privateKey = P256.KeyAgreement.PrivateKey()
        var n = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, n.count, &n)
        myNonce = Data(n)
    }

    // MARK: Handshake

    /// Client: start a handshake. Send `clientHello` in a handshake packet, then pass the host's reply to `finish`.
    static func client(pairingKey: Data) -> (session: SecureSession, clientHello: Data) {
        let s = SecureSession(isClient: true, psk: pairingKey)
        var hello = Data([0, version])
        hello.append(s.privateKey.publicKey.x963Representation)
        hello.append(s.myNonce)
        return (s, hello)
    }

    /// Client: process the host's handshake reply. Returns false if it is malformed.
    func finish(serverReply: Data) -> Bool {
        let b = [UInt8](serverReply)
        guard isClient, b.count == 2 + 65 + 16, b[0] == 1, b[1] == SecureSession.version else { return false }
        return derive(peerPublic: Data(b[2..<67]), clientNonce: myNonce, serverNonce: Data(b[67..<83]))
    }

    /// Host: accept a client's handshake. Returns the session and the reply to send back.
    static func accept(clientHello: Data, pairingKey: Data) -> (session: SecureSession, reply: Data)? {
        let b = [UInt8](clientHello)
        guard b.count == 2 + 65 + 16, b[0] == 0, b[1] == version else { return nil }
        let s = SecureSession(isClient: false, psk: pairingKey)
        guard s.derive(peerPublic: Data(b[2..<67]), clientNonce: Data(b[67..<83]), serverNonce: s.myNonce) else { return nil }
        var reply = Data([1, version])
        reply.append(s.privateKey.publicKey.x963Representation)
        reply.append(s.myNonce)
        return (s, reply)
    }

    private func derive(peerPublic: Data, clientNonce: Data, serverNonce: Data) -> Bool {
        guard let peer = try? P256.KeyAgreement.PublicKey(x963Representation: peerPublic),
              let secret = try? privateKey.sharedSecretFromKeyAgreement(with: peer) else { return false }
        var ikm = secret.withUnsafeBytes { Data($0) }
        ikm.append(psk)
        let base = SymmetricKey(data: ikm)
        let salt = clientNonce + serverNonce
        let c2s = HKDF<SHA256>.deriveKey(inputKeyMaterial: base, salt: salt, info: Data("lumen-v1 c2s".utf8), outputByteCount: 32)
        let s2c = HKDF<SHA256>.deriveKey(inputKeyMaterial: base, salt: salt, info: Data("lumen-v1 s2c".utf8), outputByteCount: 32)
        sendKey = isClient ? c2s : s2c
        recvKey = isClient ? s2c : c2s
        isReady = true
        return true
    }

    // MARK: Sealing

    static func isSealed(_ d: Data) -> Bool {
        d.count >= 2 && d[d.startIndex] == sealedMagic[0] && d[d.startIndex + 1] == sealedMagic[1]
    }

    private static func nonce(for seq: UInt64) -> AES.GCM.Nonce? {
        var n = Data(count: 4)
        n.append(be64(seq))
        return try? AES.GCM.Nonce(data: n)
    }

    private static func be64(_ v: UInt64) -> Data {
        Data((0..<8).map { UInt8((v >> UInt64(56 - 8 * $0)) & 0xFF) })
    }

    /// Encrypts one Lumen packet. Returns nil if the handshake has not completed.
    func seal(_ packet: Data) -> Data? {
        guard isReady, let key = sendKey else { return nil }
        lock.lock(); sendSeq += 1; let seq = sendSeq; lock.unlock()
        var header = Data(SecureSession.sealedMagic)
        header.append(SecureSession.be64(seq))
        guard let nonce = SecureSession.nonce(for: seq),
              let box = try? AES.GCM.seal(packet, using: key, nonce: nonce, authenticating: header) else { return nil }
        var out = header
        out.append(box.ciphertext)
        out.append(box.tag)
        return out
    }

    /// Decrypts and authenticates a sealed packet. Returns nil for forged, corrupted or replayed packets.
    func open(_ sealed: Data) -> Data? {
        let b = [UInt8](sealed)
        guard isReady, let key = recvKey, b.count >= 10 + 16, b[0] == 0x4C, b[1] == 0x45 else { return nil }
        var seq: UInt64 = 0
        for i in 2..<10 { seq = (seq << 8) | UInt64(b[i]) }
        let header = Data(b[0..<10])
        guard let nonce = SecureSession.nonce(for: seq),
              let box = try? AES.GCM.SealedBox(nonce: nonce, ciphertext: Data(b[10..<(b.count - 16)]), tag: Data(b[(b.count - 16)...])),
              let plain = try? AES.GCM.open(box, using: key, authenticating: header) else { return nil }
        lock.lock(); defer { lock.unlock() }
        guard window.accept(seq) else { return nil }
        return plain
    }
}

/// The host's pairing code, persisted in Application Support (created on first launch).
enum PairingStore {
    private static var url: URL {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Lumen")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("pairing.key")
    }

    static func load() -> Data {
        if let text = try? String(contentsOf: url, encoding: .utf8), let key = PairingCode.parse(text) { return key }
        return regenerate()
    }

    @discardableResult
    static func regenerate() -> Data {
        let key = PairingCode.generate()
        try? PairingCode.format(key).write(to: url, atomically: true, encoding: .utf8)
        return key
    }
}
