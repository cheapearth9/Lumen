import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo
import AudioToolbox

enum CaptureError: LocalizedError {
    case noDisplay
    var errorDescription: String? { "Display not found" }
}

/// Captures one display with ScreenCaptureKit, scaled to the client's resolution,
/// as NV12 (420v) frames that feed straight into the hardware HEVC encoder.
final class ScreenCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    var onFrame: ((CVPixelBuffer, CMTime) -> Void)?
    var onStopped: ((Error?) -> Void)?
    /// 960-byte packets: 240 frames of 48 kHz stereo signed 16-bit little-endian PCM.
    var onAudio: ((Data) -> Void)?
    private var audioBuffer = Data()

    private var stream: SCStream?
    private let queue = DispatchQueue(label: "lumen.capture", qos: .userInteractive)

    func start(displayID: CGDirectDisplayID, width: Int, height: Int, fps: Int, showCursor: Bool = true, audio: Bool = false) async throws {
        await stop()
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first(where: { $0.displayID == displayID }) ?? content.displays.first else {
            throw CaptureError.noDisplay
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.width = width
        cfg.height = height
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        cfg.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        cfg.queueDepth = 5
        cfg.showsCursor = showCursor
        if audio {
            cfg.capturesAudio = true
            cfg.sampleRate = 48000
            cfg.channelCount = 2
            cfg.excludesCurrentProcessAudio = true
        }

        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        if audio { try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
        audioBuffer.removeAll()
        try await s.startCapture()
        stream = s
    }

    func stop() async {
        guard let s = stream else { return }
        stream = nil
        try? await s.stopCapture()
    }

    // MARK: SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        if type == .audio { handleAudio(sb); return }
        guard type == .screen, sb.isValid else { return }
        // Only forward frames that actually contain new content.
        guard let atts = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let raw = atts.first?[.status] as? Int,
              SCFrameStatus(rawValue: raw) == .complete,
              let pb = CMSampleBufferGetImageBuffer(sb) else { return }
        onFrame?(pb, CMSampleBufferGetPresentationTimeStamp(sb))
    }

    private func handleAudio(_ sb: CMSampleBuffer) {
        guard sb.isValid, CMSampleBufferGetNumSamples(sb) > 0,
              let fd = CMSampleBufferGetFormatDescription(sb),
              let asbdPtr = CMAudioFormatDescriptionGetStreamBasicDescription(fd) else { return }
        let asbd = asbdPtr.pointee
        let planar = asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        let isFloat = asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let channels = Int(asbd.mChannelsPerFrame)
        let frames = CMSampleBufferGetNumSamples(sb)
        guard channels >= 1, isFloat, asbd.mBitsPerChannel == 32 else { return }

        try? sb.withAudioBufferList { abl, _ in
            var out = [Int16](repeating: 0, count: frames * 2)
            func clamp(_ v: Float) -> Int16 { Int16(max(-32768, min(32767, Int(v * 32767)))) }
            if planar {
                for c in 0..<min(2, abl.count) {
                    guard let p = abl[c].mData?.assumingMemoryBound(to: Float.self) else { continue }
                    let n = min(frames, Int(abl[c].mDataByteSize) / 4)
                    for i in 0..<n { out[i * 2 + c] = clamp(p[i]) }
                }
                if abl.count == 1 { for i in 0..<frames { out[i * 2 + 1] = out[i * 2] } }
            } else if let p = abl[0].mData?.assumingMemoryBound(to: Float.self) {
                let n = min(frames, Int(abl[0].mDataByteSize) / (4 * channels))
                for i in 0..<n {
                    out[i * 2] = clamp(p[i * channels])
                    out[i * 2 + 1] = clamp(p[i * channels + (channels > 1 ? 1 : 0)])
                }
            }
            out.withUnsafeBytes { audioBuffer.append(contentsOf: $0) }
        }
        while audioBuffer.count >= 960 {
            let chunk = Data(audioBuffer.prefix(960))
            audioBuffer.removeFirst(960)
            onAudio?(chunk)
        }
    }

    // MARK: SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onStopped?(error)
    }
}
