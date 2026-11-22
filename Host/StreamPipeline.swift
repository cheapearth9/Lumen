import Foundation
import CoreGraphics
import CoreMedia
import IOKit.pwr_mgt

/// Glue: client hello -> capture -> HEVC encode -> UDP, and client input -> injected events.
final class StreamPipeline: @unchecked Sendable {
    let server = HostServer()
    private let encoder = HEVCEncoder()
    private let capture = ScreenCapture()
    private let injector = InputInjector()
    private let usb = UsbMuxWatcher()
    private let ctl = DispatchQueue(label: "lumen.pipeline")

    private var active: HelloInfo?
    private var starting = false
    private var size = (w: 0, h: 0)
    private var sleepAssertion: IOPMAssertionID = 0

    private let frameLock = NSLock()
    private var lastBuffer: CVPixelBuffer?

    private let statLock = NSLock()
    private var statBytes = 0
    private var statFrames = 0

    /// The display the user picked (what Mirror streams). In Extend mode the stream uses a virtual display instead.
    var displayID: CGDirectDisplayID = CGMainDisplayID()
    private var activeDisplayID: CGDirectDisplayID = CGMainDisplayID()
    private var virtualDisplay: VirtualDisplay?
    private var extended = false
    /// Set by the host's Mirror / Extend buttons (nil = follow the client's request).
    private(set) var modeOverride: Bool?
    private var lastClientExtend: Bool?
    var onMode: ((Bool) -> Void)?
    var pairingKey: Data { server.pairingKey }
    var onStatus: ((String) -> Void)?
    var onClient: ((Bool) -> Void)?

    init() {
        server.onHello = { [weak self] h in self?.ctl.async { self?.begin(h) } }
        server.onInput = { [weak self] e in self?.injector.handle(e) }
        server.onKeyframeRequest = { [weak self] in self?.refreshKeyframe() }
        server.onState = { [weak self] s in self?.onStatus?(s) }
        server.onClientChanged = { [weak self] connected in
            guard let self else { return }
            if !connected { self.ctl.async { self.end() } }
            self.onClient?(connected)
        }
        capture.onFrame = { [weak self] pb, pts in
            guard let self else { return }
            self.frameLock.lock(); self.lastBuffer = pb; self.frameLock.unlock()
            self.encoder.encode(pb, pts: pts)
        }
        capture.onAudio = { [weak self] pcm in self?.server.sendAudio(pcm) }
        capture.onStopped = { [weak self] err in
            self?.onStatus?("Capture stopped: \(err?.localizedDescription ?? "unknown")")
        }
        encoder.onFrame = { [weak self] data, key, sets in self?.send(data, key: key, sets: sets) }
    }

    func start(name: String) throws {
        server.serverName = name
        try server.start(name: name)
        usb.wantsTunnel = { [weak server] in server?.wantsTunnel ?? false }
        usb.onTunnel = { [weak server] fd in server?.addTunnel(fd: fd) }
        usb.start()
    }

    func setMode(extend: Bool) {
        ctl.async {
            self.modeOverride = extend
            if let h = self.active { self.active = nil; self.begin(h) } else { self.onMode?(extend) }
        }
    }

    func newPairingKey() -> Data {
        let k = PairingStore.regenerate()
        server.pairingKey = k
        return k
    }

    func stop() {
        usb.stop()
        server.stop()
        ctl.async { self.end() }
    }

    func drainStats() -> (bytes: Int, frames: Int) {
        statLock.lock(); defer { statLock.unlock() }
        let r = (statBytes, statFrames)
        statBytes = 0; statFrames = 0
        return r
    }

    // MARK: - Private (ctl queue)

    private func begin(_ h: HelloInfo) {
        if let a = active, a == h {
            // Client repeated its hello (our welcome was probably lost).
            server.sendWelcome(welcome(w: size.w, h: size.h, fps: Int(h.fps)))
            ctl.asyncAfter(deadline: .now() + 0.15) { self.refreshKeyframe() }
            return
        }
        if starting { return }
        starting = true
        // A client that changes its extend flag overrides the host's button (last writer wins).
        if let prev = lastClientExtend, prev != h.wantsExtend { modeOverride = nil }
        lastClientExtend = h.wantsExtend
        let wantExtend = modeOverride ?? h.wantsExtend

        Task { [self] in
            await capture.stop()
            encoder.invalidate()
            do {
                virtualDisplay?.destroy(); virtualDisplay = nil
                activeDisplayID = displayID
                extended = false
                if wantExtend {
                    if let v = VirtualDisplay.create(width: max(640, Int(h.width)), height: max(480, Int(h.height))) {
                        virtualDisplay = v
                        activeDisplayID = v.displayID
                        extended = true
                        try? await Task.sleep(nanoseconds: 900_000_000)   // let macOS register the new display
                    } else {
                        let note = "Extend isn't available on this Mac (macOS refused to create a virtual display), so the screen is mirrored."
                        onStatus?(note)
                        server.sendStatus(note, error: false)
                    }
                }
                onMode?(extended)
                let s = Self.fit(displayID: activeDisplayID, maxW: Int(h.width), maxH: Int(h.height))
                let fps = max(15, min(Int(h.fps), 120))
                try encoder.configure(width: s.w, height: s.h, fps: fps, bitrate: max(2, Int(h.bitrateMbps)) * 1_000_000)
                injector.configure(displayID: activeDisplayID)
                try await capture.start(displayID: activeDisplayID, width: s.w, height: s.h, fps: fps, audio: h.wantsAudio)
                ctl.async {
                    self.size = s; self.active = h; self.starting = false
                    self.preventSleep(true)
                }
                server.sendWelcome(welcome(w: s.w, h: s.h, fps: fps))
                ctl.asyncAfter(deadline: .now() + 0.15) { self.refreshKeyframe() }
                onStatus?("Streaming \(s.w)×\(s.h) @ \(fps) fps")
            } catch {
                ctl.async { self.starting = false }
                let msg = "Failed: \(error.localizedDescription). Is Screen Recording allowed?"
                onStatus?(msg)
                server.sendStatus(msg, error: true)
            }
        }
    }

    private func welcome(w: Int, h: Int, fps: Int) -> WelcomeInfo {
        var dw = CGDisplayPixelsWide(activeDisplayID), dh = CGDisplayPixelsHigh(activeDisplayID)
        if let mode = CGDisplayCopyDisplayMode(activeDisplayID) { dw = mode.pixelWidth; dh = mode.pixelHeight }
        return WelcomeInfo(width: UInt16(w), height: UInt16(h), fps: UInt8(fps), caps: WelcomeInfo.capAudio,
                           sourceWidth: UInt16(min(dw, 65535)), sourceHeight: UInt16(min(dh, 65535)), mode: extended ? 1 : 0)
    }

    /// ScreenCaptureKit only emits frames when the screen changes, so after a keyframe request
    /// we re-encode the most recent frame; otherwise a static screen would stay black.
    private func refreshKeyframe() {
        encoder.requestKeyframe()
        frameLock.lock(); let pb = lastBuffer; frameLock.unlock()
        if let pb { encoder.encode(pb, pts: CMClockGetTime(CMClockGetHostTimeClock())) }
    }

    private func end() {
        frameLock.lock(); lastBuffer = nil; frameLock.unlock()
        active = nil
        virtualDisplay?.destroy(); virtualDisplay = nil
        extended = false
        injector.releaseAll()
        preventSleep(false)
        Task { [self] in
            await capture.stop()
            encoder.invalidate()
        }
    }

    private func send(_ data: Data, key: Bool, sets: [Data]) {
        var payload = Data()
        if key {
            var w = ByteWriter()
            w.u8(UInt8(sets.count))
            for s in sets { w.u16(UInt16(s.count)); w.bytes(s) }
            payload = w.data
        }
        payload.append(data)
        let ts = UInt32(truncatingIfNeeded: DispatchTime.now().uptimeNanoseconds / 1_000_000)
        server.sendVideo(payload, keyframe: key, timestampMs: ts)
        statLock.lock(); statBytes += payload.count; statFrames += 1; statLock.unlock()
    }

    private func preventSleep(_ on: Bool) {
        if on, sleepAssertion == 0 {
            IOPMAssertionCreateWithName(kIOPMAssertionTypeNoDisplaySleep as CFString,
                                        IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                        "Lumen streaming" as CFString, &sleepAssertion)
        } else if !on, sleepAssertion != 0 {
            IOPMAssertionRelease(sleepAssertion)
            sleepAssertion = 0
        }
    }

    /// Aspect-correct size that fits inside the client's screen (never upscales), even dimensions.
    static func fit(displayID: CGDirectDisplayID, maxW: Int, maxH: Int) -> (w: Int, h: Int) {
        var dw = CGDisplayPixelsWide(displayID)
        var dh = CGDisplayPixelsHigh(displayID)
        if let mode = CGDisplayCopyDisplayMode(displayID) { dw = mode.pixelWidth; dh = mode.pixelHeight }
        let scale = min(1.0, min(Double(maxW) / Double(max(dw, 1)), Double(maxH) / Double(max(dh, 1))))
        func even(_ v: Double) -> Int { max(2, Int(v.rounded()) & ~1) }
        return (even(Double(dw) * scale), even(Double(dh) * scale))
    }
}
