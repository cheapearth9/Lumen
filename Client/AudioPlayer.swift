import Foundation
import AVFoundation
import os

/// Small lock-protected jitter buffer of interleaved stereo s16le frames (48 kHz).
/// The render thread pops, the network queue pushes.
final class AudioRing {
    static let sampleRate = 48000.0
    private let capacity = 24000                 // frames (500 ms)
    private let targetFrames = 2880              // ~60 ms jitter buffer
    private let maxFrames = 7200                 // ~150 ms: beyond this the oldest data is dropped

    private let buf: UnsafeMutablePointer<Int16>
    private let lockPtr: UnsafeMutablePointer<os_unfair_lock>
    private var readPos = 0
    private var count = 0
    private var primed = false
    private var mutedFlag = false

    init() {
        buf = UnsafeMutablePointer<Int16>.allocate(capacity: capacity * 2)
        buf.initialize(repeating: 0, count: capacity * 2)
        lockPtr = UnsafeMutablePointer<os_unfair_lock>.allocate(capacity: 1)
        lockPtr.initialize(to: os_unfair_lock())
    }

    deinit {
        buf.deallocate()
        lockPtr.deinitialize(count: 1)
        lockPtr.deallocate()
    }

    var muted: Bool {
        get { os_unfair_lock_lock(lockPtr); defer { os_unfair_lock_unlock(lockPtr) }; return mutedFlag }
        set {
            os_unfair_lock_lock(lockPtr)
            mutedFlag = newValue
            if newValue { count = 0; readPos = 0; primed = false }
            os_unfair_lock_unlock(lockPtr)
        }
    }

    func reset() {
        os_unfair_lock_lock(lockPtr)
        count = 0; readPos = 0; primed = false
        os_unfair_lock_unlock(lockPtr)
    }

    /// Appends PCM (4 bytes per frame: L16 R16 little endian).
    func push(_ data: Data) {
        let frames = min(data.count / 4, capacity / 2)
        guard frames > 0 else { return }
        os_unfair_lock_lock(lockPtr)
        defer { os_unfair_lock_unlock(lockPtr) }
        if mutedFlag { return }
        if count + frames > maxFrames {
            let drop = min(count, count + frames - targetFrames)
            if drop > 0 { readPos = (readPos + drop) % capacity; count -= drop }
        }
        data.withUnsafeBytes { raw in
            let p = raw.bindMemory(to: UInt8.self)
            var w = (readPos + count) % capacity
            for f in 0..<frames {
                let o = f * 4
                buf[w * 2] = Int16(bitPattern: UInt16(p[o]) | (UInt16(p[o + 1]) << 8))
                buf[w * 2 + 1] = Int16(bitPattern: UInt16(p[o + 2]) | (UInt16(p[o + 3]) << 8))
                w += 1
                if w == capacity { w = 0 }
            }
        }
        count += frames
    }

    /// Fills planar float buffers. Outputs silence while (re)buffering or on underrun.
    func pop(left: UnsafeMutablePointer<Float>, right: UnsafeMutablePointer<Float>, frames: Int) {
        os_unfair_lock_lock(lockPtr)
        defer { os_unfair_lock_unlock(lockPtr) }
        if !primed {
            if count >= targetFrames { primed = true }
            else {
                for i in 0..<frames { left[i] = 0; right[i] = 0 }
                return
            }
        }
        let n = min(frames, count)
        var r = readPos
        let scale: Float = 1.0 / 32768.0
        for i in 0..<n {
            left[i] = Float(buf[r * 2]) * scale
            right[i] = Float(buf[r * 2 + 1]) * scale
            r += 1
            if r == capacity { r = 0 }
        }
        readPos = r
        count -= n
        if n < frames {
            for i in n..<frames { left[i] = 0; right[i] = 0 }
            primed = false   // underrun: refill the jitter buffer before resuming
        }
    }
}

/// Plays the host's audio stream with AVAudioEngine.
final class AudioPlayer {
    private let ring = AudioRing()
    private let queue = DispatchQueue(label: "lumen.audio")
    private var engine: AVAudioEngine?
    private var observer: NSObjectProtocol?

    var muted: Bool {
        get { ring.muted }
        set { ring.muted = newValue }
    }

    func push(_ pcm: Data) { ring.push(pcm) }

    func start() {
        queue.async { self.startNow() }
    }

    func stop() {
        queue.async { self.stopNow() }
    }

    private func startNow() {
        guard engine == nil else { return }
        let session = AVAudioSession.sharedInstance()
        do {
            try session.setCategory(.playback, mode: .default, options: [])
            try? session.setPreferredIOBufferDuration(0.01)
            try session.setActive(true)
        } catch {
            return
        }
        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioRing.sampleRate,
                                         channels: 2, interleaved: false) else { return }
        let ring = self.ring
        let node = AVAudioSourceNode(format: format) { _, _, frameCount, abl -> OSStatus in
            let list = UnsafeMutableAudioBufferListPointer(abl)
            guard list.count >= 2,
                  let l = list[0].mData?.assumingMemoryBound(to: Float.self),
                  let r = list[1].mData?.assumingMemoryBound(to: Float.self) else { return noErr }
            ring.pop(left: l, right: r, frames: Int(frameCount))
            return noErr
        }
        let eng = AVAudioEngine()
        eng.attach(node)
        eng.connect(node, to: eng.mainMixerNode, format: format)
        ring.reset()
        do { try eng.start() } catch { return }
        engine = eng
        observer = NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification,
                                                          object: nil, queue: nil) { [weak self] n in
            guard let self,
                  let raw = n.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
                  AVAudioSession.InterruptionType(rawValue: raw) == .ended else { return }
            self.queue.async {
                guard let e = self.engine else { return }
                try? AVAudioSession.sharedInstance().setActive(true)
                if !e.isRunning { try? e.start() }
            }
        }
    }

    private func stopNow() {
        if let o = observer { NotificationCenter.default.removeObserver(o); observer = nil }
        guard let e = engine else { return }
        e.stop()
        engine = nil
        ring.reset()
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }
}
