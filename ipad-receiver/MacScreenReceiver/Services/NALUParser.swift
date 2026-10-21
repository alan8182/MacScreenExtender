import Foundation
import CoreMedia
import VideoToolbox
import QuartzCore

/// Annex-B H.264 / HEVC NALU 分包与参数集解析器
public final class NALUParser {
    public enum Codec {
        case h264
        case hevc
    }

    public var codec: Codec = .h264
    private var buffer = Data()

    // H.264 参数集
    private var spsData: Data?
    private var ppsData: Data?

    // HEVC 参数集
    private var vpsData: Data?
    private var hevcSpsData: Data?
    private var hevcPpsData: Data?

    public private(set) var formatDescription: CMVideoFormatDescription?

    public init() {}

    public func reset() {
        buffer.removeAll(keepingCapacity: false)
        spsData = nil
        ppsData = nil
        vpsData = nil
        hevcSpsData = nil
        hevcPpsData = nil
        formatDescription = nil
    }

    /// 追加网络数据流，并返回切分出的所有 NALU
    public func append(data: Data) -> [Data] {
        buffer.append(data)
        var nalus: [Data] = []

        var searchIndex = 0
        while true {
            guard let range = findStartCode(in: buffer, startingAt: searchIndex) else {
                break
            }

            // 找到了一个 start code
            let naluStart = range.upperBound
            // 寻找下一个 start code
            if let nextRange = findStartCode(in: buffer, startingAt: naluStart) {
                let naluData = buffer.subdata(in: naluStart..<nextRange.lowerBound)
                if !naluData.isEmpty {
                    nalus.append(naluData)
                    processNALU(naluData)
                }
                // 移出已处理部分
                buffer.removeSubrange(0..<nextRange.lowerBound)
                searchIndex = 0
            } else {
                // 等待更多数据到达
                break
            }
        }

        return nalus
    }

    private func findStartCode(in data: Data, startingAt offset: Int) -> Range<Data.Index>? {
        guard data.count >= offset + 4 else { return nil }

        let bytes = [UInt8](data)
        let count = bytes.count

        for i in offset..<(count - 3) {
            if bytes[i] == 0 && bytes[i+1] == 0 {
                if bytes[i+2] == 1 {
                    return i..<(i+3)
                } else if bytes[i+2] == 0 && bytes[i+3] == 1 {
                    return i..<(i+4)
                }
            }
        }
        return nil
    }

    private func processNALU(_ nalu: Data) {
        guard let firstByte = nalu.first else { return }

        switch codec {
        case .h264:
            let nalType = firstByte & 0x1F
            if nalType == 7 { // SPS
                spsData = nalu
                createH264FormatDescriptionIfReady()
            } else if nalType == 8 { // PPS
                ppsData = nalu
                createH264FormatDescriptionIfReady()
            }

        case .hevc:
            let nalType = (firstByte >> 1) & 0x3F
            if nalType == 32 { // VPS
                vpsData = nalu
                createHEVCFormatDescriptionIfReady()
            } else if nalType == 33 { // SPS
                hevcSpsData = nalu
                createHEVCFormatDescriptionIfReady()
            } else if nalType == 34 { // PPS
                hevcPpsData = nalu
                createHEVCFormatDescriptionIfReady()
            }
        }
    }

    private func createH264FormatDescriptionIfReady() {
        guard let sps = spsData, let pps = ppsData else { return }

        sps.withUnsafeBytes { spsPtr in
            pps.withUnsafeBytes { ppsPtr in
                guard let spsBase = spsPtr.baseAddress?.assumingMemoryBound(to: UInt8.self),
                      let ppsBase = ppsPtr.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }

                let pointers = [spsBase, ppsBase]
                let sizes = [sps.count, pps.count]

                var desc: CMFormatDescription?
                let status = CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: 2,
                    parameterSetPointers: pointers,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &desc
                )

                if status == noErr, let d = desc {
                    self.formatDescription = d
                }
            }
        }
    }

    private func createHEVCFormatDescriptionIfReady() {
        guard let vps = vpsData, let sps = hevcSpsData, let pps = hevcPpsData else { return }

        vps.withUnsafeBytes { vpsPtr in
            sps.withUnsafeBytes { spsPtr in
                pps.withUnsafeBytes { ppsPtr in
                    guard let vpsBase = vpsPtr.baseAddress?.assumingMemoryBound(to: UInt8.self),
                          let spsBase = spsPtr.baseAddress?.assumingMemoryBound(to: UInt8.self),
                          let ppsBase = ppsPtr.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }

                    let pointers = [vpsBase, spsBase, ppsBase]
                    let sizes = [vps.count, sps.count, pps.count]

                    var desc: CMFormatDescription?
                    let status = CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                        allocator: kCFAllocatorDefault,
                        parameterSetCount: 3,
                        parameterSetPointers: pointers,
                        parameterSetSizes: sizes,
                        nalUnitHeaderLength: 4,
                        extensions: nil,
                        formatDescriptionOut: &desc
                    )

                    if status == noErr, let d = desc {
                        self.formatDescription = d
                    }
                }
            }
        }
    }

    /// 将 NALU 包装为带 AVCC 4 字节大端长度前缀的 CMSampleBuffer，用于喂给 AVSampleBufferDisplayLayer
    public func createSampleBuffer(from nalu: Data) -> CMSampleBuffer? {
        guard let formatDesc = formatDescription else { return nil }

        // 构造 AVCC 格式：4字节大端长度 + 原始载荷
        var length = UInt32(nalu.count).bigEndian
        var avccData = Data(bytes: &length, count: 4)
        avccData.append(nalu)

        var blockBuffer: CMBlockBuffer?
        let memBlock = malloc(avccData.count)
        guard let mem = memBlock else { return nil }
        avccData.copyBytes(to: mem.assumingMemoryBound(to: UInt8.self), count: avccData.count)

        let status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: mem,
            blockLength: avccData.count,
            blockAllocator: kCFAllocatorMalloc,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: avccData.count,
            flags: 0,
            blockBufferOut: &blockBuffer
        )

        guard status == kCMBlockBufferNoErr, let bBuffer = blockBuffer else {
            free(memBlock)
            return nil
        }

        var sampleBuffer: CMSampleBuffer?
        var sampleSize = avccData.count
        var timing = CMSampleTimingInfo(
            duration: CMTime.invalid,
            presentationTimeStamp: CMTime(seconds: CACurrentMediaTime(), preferredTimescale: 1000),
            decodeTimeStamp: CMTime.invalid
        )

        let sStatus = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: bBuffer,
            formatDescription: formatDesc,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )

        guard sStatus == noErr, let sBuf = sampleBuffer else { return nil }

        // 标记为立即上屏（零抖动延迟）
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sBuf, createIfNecessary: true) {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict,
                                 Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }

        return sBuf
    }
}
