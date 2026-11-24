import Foundation
import CryptoKit
import Security

// End-to-end encryption and device pairing for the Lumen protocol. Must stay byte-compatible with HostCommon/Crypto.cs.
//
// PAIRING (once per device, Sunshine/Apollo style; the 5-digit code is shown on the iPad and typed on the computer):
//   Packets use PacketType.pair, unencrypted; payload = [step u8] + body.
//     1 client -> host : [1][32 commit = SHA256(pkC || nonceC)][device name, UTF-8, up to 40 bytes]
//     2 host -> client : [2][65 host P-256 public key (x9.63)][16 host nonce]
//     3 client -> host : [3][65 client public key][16 client nonce]     (host checks the commit)
//     4 host -> client : [4][result: 0 paired, 1 wrong code, 2 busy / locked out, 3 error]
//   Both sides compute the same 5-digit code (short authentication string):
//        code = (u32 big endian of SHA256("lumen-sas-v1" || pkC || pkH || nonceC || nonceH)[0..<4]) % 100000
//   The client shows it, the user types it into the computer, and the computer only accepts if it matches its own.
//   A man in the middle needs the code to match on both legs while having to fix its own keys before it sees
//   the other leg's, so each attempt succeeds with probability 1/100000 and is visible to the user.
//   Long-term secret = HKDF-SHA256(ECDH raw shared secret, salt nonceC || nonceH, info "lumen-pair-v1") -> 32 bytes.
//   Key id = SHA256("lumen-keyid" || secret)[0..<8]. The host stores (id, name, secret); the client stores the secret.
//
// CONNECTING (every session), handshake packets (PacketType.handshake, unencrypted):
//     client -> host : [0 role][2 version][65 P-256 public key][16 client nonce][8 key id]
//     host -> client : [1 role][2 version][65 public key][16 server nonce]       (key id known)
//                    : [2 role][2 version]                                       (key id unknown -> pairing required)
//   Keys: ikm = ECDH raw secret (32) || long-term secret (32);  salt = client nonce || server nonce;
//         c2s / s2c = HKDF-SHA256(ikm, salt, info "lumen-v1 c2s" / "lumen-v1 s2c") -> 32 bytes each.
//   Sealed packet: [0x4C 0x45][seq u64 BE][ciphertext][16-byte tag], AES-256-GCM,
//   nonce = 4 zero bytes || seq (8 bytes BE), AAD = the first 10 bytes. The plaintext is a normal Lumen packet.

enum PairingCrypto {
    static func sha256(_ parts: Data...) -> Data {
        var h = SHA256()
        for p in parts { h.update(data: p) }
        return Data(h.finalize())
    }

    static func keyId(_ secret: Data) -> Data { sha256(Data("lumen-keyid".utf8), secret).prefix(8) }

    static func random(_ n: Int) -> Data {
        var b = [UInt8](repeating: 0, count: n)
        _ = SecRandomCopyBytes(kSecRandomDefault, n, &b)
        return Data(b)
    }

    static func code(pkC: Data, pkH: Data, nonceC: Data, nonceH: Data) -> String {
        let d = sha256(Data("lumen-sas-v1".utf8), pkC, pkH, nonceC, nonceH)
        let v = d.prefix(4).reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        return String(format: "%05d", v % 100000)
    }

    static func secret(ecdh: Data, nonceC: Data, nonceH: Data) -> Data {
        let k = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ecdh), salt: nonceC + nonceH,
                                       info: Data("lumen-pair-v1".utf8), outputByteCount: 32)
        return k.withUnsafeBytes { Data($0) }
    }

    /// "12345" -> "12345"; accepts digits with any separators. Nil unless exactly 5 digits.
    static func parseCode(_ text: String) -> String? {
        let d = text.filter { $0.isASCII && $0.isNumber }
        return d.count == 5 ? d : nil
    }
}

/// Client side of pairing (the iOS app).
final class PairingClient {
    private let key = P256.KeyAgreement.PrivateKey()
    private let nonce = PairingCrypto.random(16)
    private(set) var code: String?
    private(set) var secret: Data?

    var publicKey: Data { key.publicKey.x963Representation }

    /// Payload of the first pair packet.
    func start(deviceName: String) -> Data {
        var d = Data([1])
        d.append(PairingCrypto.sha256(publicKey, nonce))
        d.append(Data(deviceName.utf8.prefix(40)))
        return d
    }

    /// Process the host's step 2. Returns the step-3 payload; `code` and `secret` are then available.
    func handleStep2(_ payload: Data) -> Data? {
        let b = [UInt8](payload)
        guard b.count == 1 + 65 + 16, b[0] == 2,
              let peer = try? P256.KeyAgreement.PublicKey(x963Representation: Data(b[1..<66])),
              let shared = try? key.sharedSecretFromKeyAgreement(with: peer) else { return nil }
        let pkH = Data(b[1..<66]), nonceH = Data(b[66..<82])
        code = PairingCrypto.code(pkC: publicKey, pkH: pkH, nonceC: nonce, nonceH: nonceH)
        secret = PairingCrypto.secret(ecdh: shared.withUnsafeBytes { Data($0) }, nonceC: nonce, nonceH: nonceH)
        var d = Data([3])
        d.append(publicKey)
        d.append(nonce)
        return d
    }
}

/// Host side of one pairing attempt.
final class PairingHost {
    private let key = P256.KeyAgreement.PrivateKey()
    private let nonce = PairingCrypto.random(16)
    private var commit = Data()
    private(set) var deviceName = ""
    private(set) var code: String?
    private(set) var secret: Data?

    /// Process step 1; returns the step-2 payload.
    func handleStep1(_ payload: Data) -> Data? {
        let b = [UInt8](payload)
        guard b.count >= 1 + 32, b[0] == 1 else { return nil }
        commit = Data(b[1..<33])
        let raw = String(decoding: b[33...], as: UTF8.self)
        deviceName = raw.filter { !$0.isNewline && $0 != "\0" }.isEmpty ? "Device" : String(raw.prefix(40))
        var d = Data([2])
        d.append(key.publicKey.x963Representation)
        d.append(nonce)
        return d
    }

    /// Process step 3. On success `code` (what the user must type) and `secret` are set.
    func handleStep3(_ payload: Data) -> Bool {
        let b = [UInt8](payload)
        guard b.count == 1 + 65 + 16, b[0] == 3 else { return false }
        let pkC = Data(b[1..<66]), nonceC = Data(b[66..<82])
        guard PairingCrypto.sha256(pkC, nonceC) == commit,
              let peer = try? P256.KeyAgreement.PublicKey(x963Representation: pkC),
              let shared = try? key.sharedSecretFromKeyAgreement(with: peer) else { return false }
        let pkH = key.publicKey.x963Representation
        code = PairingCrypto.code(pkC: pkC, pkH: pkH, nonceC: nonceC, nonceH: nonce)
        secret = PairingCrypto.secret(ecdh: shared.withUnsafeBytes { Data($0) }, nonceC: nonceC, nonceH: nonce)
        return true
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
    private static let version: UInt8 = 2
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
        myNonce = PairingCrypto.random(16)
    }

    // MARK: Handshake

    /// Client: start a handshake with the long-term secret from pairing. Send `clientHello` in a handshake packet,
    /// then pass the host's reply to `finish`.
    static func client(pairingKey: Data) -> (session: SecureSession, clientHello: Data) {
        let s = SecureSession(isClient: true, psk: pairingKey)
        var hello = Data([0, version])
        hello.append(s.privateKey.publicKey.x963Representation)
        hello.append(s.myNonce)
        hello.append(PairingCrypto.keyId(pairingKey))
        return (s, hello)
    }

    /// True when a host reply says "this device is not paired" (the stored secret is unknown to the host).
    static func isPairingRequired(_ reply: Data) -> Bool {
        let b = [UInt8](reply)
        return b.count >= 2 && b[0] == 2
    }

    /// Client: process the host's handshake reply. Returns false if it is malformed.
    func finish(serverReply: Data) -> Bool {
        let b = [UInt8](serverReply)
        guard isClient, b.count == 2 + 65 + 16, b[0] == 1, b[1] == SecureSession.version else { return false }
        return derive(peerPublic: Data(b[2..<67]), clientNonce: myNonce, serverNonce: Data(b[67..<83]))
    }

    /// The key id a client hello asks for (so the host can look up the secret). Nil if malformed.
    static func keyId(ofClientHello hello: Data) -> Data? {
        let b = [UInt8](hello)
        guard b.count == 2 + 65 + 16 + 8, b[0] == 0, b[1] == version else { return nil }
        return Data(b[83..<91])
    }

    /// The reply that tells a client it must pair first.
    static var pairingRequiredReply: Data { Data([2, version]) }

    /// Host: accept a client's handshake using the secret that belongs to its key id.
    static func accept(clientHello: Data, secret: Data) -> (session: SecureSession, reply: Data)? {
        let b = [UInt8](clientHello)
        guard b.count == 2 + 65 + 16 + 8, b[0] == 0, b[1] == version else { return nil }
        let s = SecureSession(isClient: false, psk: secret)
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

/// Devices the Mac host has paired with: key id, name, secret. Stored in Application Support/Lumen/paired.json.
struct PairedDevice: Codable { var id: String; var name: String; var secret: String }

enum PairedStore {
    private static let lock = NSLock()

    private static var url: URL {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Lumen")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("paired.json")
    }

    static func all() -> [PairedDevice] {
        lock.lock(); defer { lock.unlock() }
        guard let d = try? Data(contentsOf: url), let list = try? JSONDecoder().decode([PairedDevice].self, from: d) else { return [] }
        return list
    }

    static func secret(forKeyId id: Data) -> Data? {
        let hex = id.map { String(format: "%02x", $0) }.joined()
        guard let dev = all().first(where: { $0.id == hex }) else { return nil }
        return Data(base64Encoded: dev.secret)
    }

    static func add(name: String, secret: Data) {
        var list = all()
        let id = PairingCrypto.keyId(secret).map { String(format: "%02x", $0) }.joined()
        list.removeAll { $0.id == id }
        list.append(PairedDevice(id: id, name: name, secret: secret.base64EncodedString()))
        save(list)
    }

    static func removeAll() { save([]) }

    private static func save(_ list: [PairedDevice]) {
        lock.lock(); defer { lock.unlock() }
        if let d = try? JSONEncoder().encode(list) { try? d.write(to: url, options: .atomic) }
    }
}
