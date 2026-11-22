import Foundation
import VideoToolbox
import CoreMedia

enum EncoderError: LocalizedError {
    case create(OSStatus)
    var errorDescription: String? {
        switch self { case .create(let s): return "Hardware HEVC encoder failed to start (\(s))" }
    }
}

/// Low-latency hardware HEVC encoder. No B-frames, no look-ahead, one frame in / one frame out.
final class HEVCEncoder {
    /// (AVCC access unit, isKeyframe, parameter sets [VPS, SPS, PPS] for keyframes)
    var onFrame: ((Data, Bool, [Data]) -> Void)?

    private var session: VTCompressionSession?
    private let lock = NSLock()
    private var forceKey = true

    func configure(width: Int, height: Int, fps: Int, bitrate: Int) throws {
        invalidate()
        let spec: [CFString: Any] = [
            kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true
        ]
        var s: VTCompressionSession?
        let st = VTCompressionSessionCreate(allocator: nil,
                                            width: Int32(width), height: Int32(height),
                                            codecType: kCMVideoCodecType_HEVC,
                                            encoderSpecification: spec as CFDictionary,
                                            imageBufferAttributes: nil,
                                            compressedDataAllocator: nil,
                                            outputCallback: nil, refcon: nil,
                                            compressionSessionOut: &s)
        guard st == noErr, let s else { throw EncoderError.create(st) }

        func set(_ key: CFString, _ value: CFTypeRef) { VTSessionSetProperty(s, key: key, value: value) }
        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_HEVC_Main_AutoLevel)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        set(kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_MaxFrameDelayCount, NSNumber(value: 0))
        set(kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))
        set(kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: bitrate))
        // Cap bursts at 1.5x average over one second.
        let limits = [Int(Double(bitrate) * 1.5 / 8.0), 1] as CFArray
        set(kVTCompressionPropertyKey_DataRateLimits, limits)
        // Keyframes only on demand (client requests them after packet loss) plus a safety net every 10 s.
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: fps * 10))
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, 1NSNumber(value: 0))

        VTCompressionSessionPrepareToEncodeFrames(s)
        lock.lock(); session = s; forceKey = true; lock.unlock()
    }

    func requestKeyframe() {
        lock.lock(); forceKey = true; lock.unlock()
    }

    func invalidate() {
        lock.lock()
        let s = session
        session = nil
        lock.unlock()
        if let s {
            VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(s)
        }
    }

    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        lock.lock()
        let s = session
        let key = forceKey
        forceKey = false
        lock.unlock()
        guard let s else { return }

        var props: CFDictionary?
        if key { props = [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary }

        _ = VTCompressionSessionEncodeFrame(s, imageBuffer: pixelBuffer,
                                            presentationTimeStamp: pts, duration: .invalid,
                                            frameProperties: props, infoFlagsOut: nil) { [weak self] status, _, sb in
            guard status == noErr, let sb, let self else { return }
            self.handle(sb)
        }
    }

    private func handle(_ sb: CMSampleBuffer) {
        guard let bb = CMSampleBufferGetDataBuffer(sb) else { return }

        let atts = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[CFString: Any]]
        let notSync = (atts?.first?[kCMSampleAttachmentKey_NotSync] as? Bool) ?? false
        let isKey = !notSync

        var sets = [Data]()
        if isKey, let fd = CMSampleBufferGetFormatDescription(sb) {
            var count = 0
            CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(fd, parameterSetIndex: 0,
                                                              parameterSetPointerOut: nil, parameterSetSizeOut: nil,
                                                              parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
            for i in 0..<count {
                var ptr: UnsafePointer<UInt8>?
                var size = 0
                let r = CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(fd, parameterSetIndex: i,
                                                                          parameterSetPointerOut: &ptr, parameterSetSizeOut: &size,
                                                                          parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
                if r == noErr, let ptr { sets.append(Data(bytes: ptr, count: size)) }
            }
        }

        let len = CMBlockBufferGetDataLength(bb)
        var data = Data(count: len)
        let ok = data.withUnsafeMutableBytes { raw -> Bool in
            guard let base = raw.baseAddress else { return false }
            return CMBlockBufferCopyDataBytes(bb, atOffset: 0, dataLength: len, destination: base) == kCMBlockBufferNoErr
        }
        guard ok else { return }
        onFrame?(data, isKey, sets)
    }
}
