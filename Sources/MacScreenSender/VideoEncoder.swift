import Foundation
import CoreMedia
import CoreVideo
import VideoToolbox

/// VideoToolbox 硬编码器（H.264/HEVC），输出 Annex-B（关键帧前带参数集）
final class VideoEncoder: @unchecked Sendable {
    enum Codec { case h264, hevc }

    private var session: VTCompressionSession?
    private var paramSets: [Data]?     // h264: SPS/PPS；hevc: VPS/SPS/PPS
    private var isHEVC = false
    private var forceKeyframe = true   // 首帧强制 IDR：等待中的新客户端拿到流立刻能解
    private var lastEncodedPTS: CMTime = .invalid
    private var ptsOrigin90: UInt64?   // 流内相对 PTS 的基准（首帧绝对值）
    private let lock = NSLock()

    /// (AnnexB 帧, 是否关键帧, PTS 90kHz)
    var onFrame: ((Data, Bool, UInt64) -> Void)?

    enum EncodeError: Error { case create(OSStatus), prepare(OSStatus), noParams }

    func start(width: Int, height: Int, fps: Int, bitrateBps: Int, codec: Codec = .h264) throws {
        lock.lock(); defer { lock.unlock() }
        guard session == nil else { return }
        ptsOrigin90 = nil
        paramSets = nil
        isHEVC = codec == .hevc

        let bufferAttrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey: width,
            kCVPixelBufferHeightKey: height,
        ]
        var newSession: VTCompressionSession?
        var status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width), height: Int32(height),
            codecType: isHEVC ? kCMVideoCodecType_HEVC : kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: bufferAttrs as CFDictionary,
            compressedDataAllocator: nil,
            outputCallback: encoderOutputCallback,
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &newSession)
        guard status == noErr, let s = newSession else { throw EncodeError.create(status) }

        func setProperty(_ key: CFString, _ value: CFTypeRef) {
            VTSessionSetProperty(s, key: key, value: value)
        }
        setProperty(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        setProperty(kVTCompressionPropertyKey_ProfileLevel,
                    isHEVC ? kVTProfileLevel_HEVC_Main_AutoLevel : kVTProfileLevel_H264_High_AutoLevel)
        setProperty(kVTCompressionPropertyKey_AverageBitRate, bitrateBps as NSNumber)
        setProperty(kVTCompressionPropertyKey_ExpectedFrameRate, fps as NSNumber)
        setProperty(kVTCompressionPropertyKey_MaxKeyFrameInterval, (fps * 4) as NSNumber)       // 4s：静止时减少关键帧突刺
        setProperty(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, 4.0 as NSNumber)
        setProperty(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        setProperty(kVTCompressionPropertyKey_MaxFrameDelayCount, 0 as NSNumber)   // 零缓冲：编码完立刻吐
        if !isHEVC {
            setProperty(kVTCompressionPropertyKey_H264EntropyMode, kVTH264EntropyMode_CABAC)
        }

        status = VTCompressionSessionPrepareToEncodeFrames(s)
        guard status == noErr else { throw EncodeError.prepare(status) }
        session = s
    }

    func encode(sampleBuffer: CMSampleBuffer) {
        lock.lock(); defer { lock.unlock() }
        guard session != nil, let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        encodeLocked(pixelBuffer, pts: pts)
    }

    /// 保底帧路径：直接编码像素缓冲，PTS 用主机时钟保证单调
    func encode(pixelBuffer: CVPixelBuffer, pts: CMTime) {
        lock.lock(); defer { lock.unlock() }
        guard session != nil else { return }
        encodeLocked(pixelBuffer, pts: pts)
    }

    private func encodeLocked(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        var pts = pts
        if lastEncodedPTS.isValid && pts <= lastEncodedPTS {
            pts = CMTime(value: lastEncodedPTS.value + 1, timescale: lastEncodedPTS.timescale)
        }
        lastEncodedPTS = pts
        var props: CFDictionary?
        if forceKeyframe {
            forceKeyframe = false
            props = [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
        }
        VTCompressionSessionEncodeFrame(
            session!, imageBuffer: pixelBuffer,
            presentationTimeStamp: pts, duration: .invalid,
            frameProperties: props, sourceFrameRefcon: nil,
            infoFlagsOut: nil)
    }

    /// 新客户端接入时请求立即出关键帧
    func requestKeyframe() {
        lock.lock(); forceKeyframe = true; lock.unlock()
    }

    /// 运行中调整目标码率（码率自适应）；编码器重建后由调用方用新码率启动
    func updateBitrate(_ bps: Int) {
        lock.lock(); defer { lock.unlock() }
        guard let s = session else { return }
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_AverageBitRate, value: bps as NSNumber)
    }

    func stop() {
        lock.lock(); defer { lock.unlock() }
        guard let s = session else { return }
        session = nil
        VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(s)
    }

    /// 输出回调（C 函数指针入口）
    fileprivate func handleOutput(status: OSStatus, sampleBuffer: CMSampleBuffer?) {
        guard status == noErr, let sb = sampleBuffer, sb.isValid,
              let desc = CMSampleBufferGetFormatDescription(sb) else { return }

        if paramSets == nil {
            var sets: [Data] = []
            var ptr: UnsafePointer<UInt8>?
            var size = 0, count = 0, nalLen: Int32 = 0
            if isHEVC {
                // HEVC：VPS/SPS/PPS 三个参数集
                guard CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(desc, parameterSetIndex: 0,
                            parameterSetPointerOut: &ptr, parameterSetSizeOut: &size,
                            parameterSetCountOut: &count, nalUnitHeaderLengthOut: &nalLen) == noErr else { return }
            } else {
                guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(desc, parameterSetIndex: 0,
                            parameterSetPointerOut: &ptr, parameterSetSizeOut: &size,
                            parameterSetCountOut: &count, nalUnitHeaderLengthOut: &nalLen) == noErr else { return }
            }
            for i in 0..<count {
                let idx = i
                if isHEVC {
                    guard CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(desc, parameterSetIndex: idx,
                                parameterSetPointerOut: &ptr, parameterSetSizeOut: &size,
                                parameterSetCountOut: &count, nalUnitHeaderLengthOut: &nalLen) == noErr,
                          let p = ptr else { return }
                    sets.append(Data(bytes: p, count: size))
                } else {
                    guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(desc, parameterSetIndex: idx,
                                parameterSetPointerOut: &ptr, parameterSetSizeOut: &size,
                                parameterSetCountOut: &count, nalUnitHeaderLengthOut: &nalLen) == noErr,
                          let p = ptr else { return }
                    sets.append(Data(bytes: p, count: size))
                }
            }
            guard !sets.isEmpty else { return }
            paramSets = sets
        }

        let keyframe: Bool
        if let arr = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[CFString: Any]],
           let first = arr.first {
            keyframe = !(first["NotSync" as CFString] as? Bool ?? false)
        } else {
            keyframe = false
        }

        guard let block = CMSampleBufferGetDataBuffer(sb) else { return }
        var totalLen = 0
        var dataPtr: UnsafeMutablePointer<Int8>?
        guard CMBlockBufferGetDataPointer(block, atOffset: 0,
                    lengthAtOffsetOut: nil, totalLengthOut: &totalLen,
                    dataPointerOut: &dataPtr) == noErr, let raw = dataPtr else { return }

        let startCode = Data([0x00, 0x00, 0x00, 0x01])
        var annexB = Data(capacity: totalLen + 16)
        if keyframe, let sets = paramSets {
            for set in sets {
                annexB.append(startCode)
                annexB.append(set)
            }
        }
        // AVCC(4 字节长度前缀) → Annex-B(起始码)。注意长度字段不保证对齐，须按字节读。
        var offset = 0
        let u8 = UnsafeRawPointer(raw).assumingMemoryBound(to: UInt8.self)
        while offset + 4 <= totalLen {
            let b0 = Int(u8[offset]), b1 = Int(u8[offset + 1])
            let b2 = Int(u8[offset + 2]), b3 = Int(u8[offset + 3])
            let naluLen = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3
            offset += 4
            guard naluLen > 0, offset + naluLen <= totalLen else { break }
            annexB.append(startCode)
            annexB.append(contentsOf: UnsafeBufferPointer(start: u8 + offset, count: naluLen))
            offset += naluLen
        }

        let ptsSeconds = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sb))
        // 关键：不能用主机开机时钟的绝对值当 PTS。VLC 的输入时钟要求时间戳离基准 ±9s 内，
        // 实测绝对值 PTS（开机几小时 = 几万秒）会让 VLC 每帧都 Timestamp conversion failed、
        // 只解出第一帧后永久冻结（ffprobe 不做该换算所以一直正常）。改为流内相对时间（首帧=0）。
        let raw90 = UInt64(max(0, ptsSeconds * 90_000))
        if ptsOrigin90 == nil { ptsOrigin90 = raw90 }
        let pts90 = (raw90 - ptsOrigin90!) & 0x1_FFFF_FFFF
        onFrame?(annexB, keyframe, pts90)
    }
}

private let encoderOutputCallback: VTCompressionOutputCallback = { refcon, _, status, _, sampleBuffer in
    guard let refcon else { return }
    Unmanaged<VideoEncoder>.fromOpaque(refcon).takeUnretainedValue()
        .handleOutput(status: status, sampleBuffer: sampleBuffer)
}
