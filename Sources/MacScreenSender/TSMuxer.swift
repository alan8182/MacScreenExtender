import Foundation

/// 最小 MPEG-TS 复用器：单路 H.264（video PID 0x0100，PCR 同 PID），PAT/PMT 按需重发
final class TSMuxer: @unchecked Sendable {
    static let videoPID: UInt16 = 0x0100
    static let pmtPID: UInt16 = 0x1000

    private let streamType: UInt8   // 0x1B=H.264, 0x24=HEVC
    private var ccPAT: UInt8 = 0
    private var ccPMT: UInt8 = 0
    private var ccVideo: UInt8 = 0
    private let lock = NSLock()

    init(streamType: UInt8 = 0x1B) { self.streamType = streamType }

    // MARK: - 对外接口

    /// PAT + PMT 两个 TS 包（新客户端接入、每个关键帧前发送）
    func systemTables() -> Data {
        lock.lock(); defer { lock.unlock() }
        return patPacket() + pmtPacket()
    }

    /// 把一帧 Annex-B 装进 TS 包（首包带 PCR；关键帧时调用方需在前面追加 systemTables）
    func videoPackets(annexB: Data, pts90: UInt64, keyframe: Bool) -> Data {
        lock.lock(); defer { lock.unlock() }

        var pes = Data([0x00, 0x00, 0x01, 0xE0])   // PES 起始 + 视频 stream_id
        pes.append(contentsOf: [0x00, 0x00])       // PES_packet_length = 0（视频允许不定量）
        pes.append(0x80)                           // '10' + flags
        pes.append(contentsOf: [0x80, 0x05])       // PTS only，header_data_length=5
        pes.append(contentsOf: Self.ptsBytes(pts90))
        pes.append(annexB)

        var out = Data(capacity: pes.count + pes.count / 170 * 4 + 188)
        var offset = 0
        var first = true
        while offset < pes.count {
            let cap = first ? 176 : 184            // 首包带 8 字节 PCR 适配域
            let end = min(offset + cap, pes.count)
            let chunk = pes.subdata(in: offset..<end)
            out.append(tsPacket(pid: Self.videoPID, pusi: first, cc: &ccVideo,
                                payload: chunk, pcr90: first ? pts90 : nil))
            first = false
            offset = end
        }
        _ = keyframe
        return out
    }

    // MARK: - TS 包

    private func tsPacket(pid: UInt16, pusi: Bool, cc: inout UInt8,
                          payload: Data, pcr90: UInt64? = nil) -> Data {
        var p = Data([0x47])
        p.append((UInt8(pid >> 8) & 0x1F) | (pusi ? 0x40 : 0x00))
        p.append(UInt8(pid & 0xFF))

        var af: Data?
        if let v = pcr90 {
            af = Data([0x07, 0x10]) + Self.pcrBytes(v)
        } else if payload.count < 184 {
            // 纯填充适配域。长度字段值不含自身：4 + [len字节 + len内容] + L = 188
            let afLen = 183 - payload.count
            if afLen == 0 {
                af = Data([0x00])
            } else {
                af = Data([UInt8(afLen), 0x00]) + Data(repeating: 0xFF, count: afLen - 1)
            }
        }

        var afc: UInt8 = 0x10                       // AFC=01 仅载荷
        if af != nil {
            afc = 0x30                              // AFC=11 适配域+载荷
        }
        p.append(afc | (cc & 0x0F))
        if let a = af { p.append(a) }
        cc = (cc &+ 1) & 0x0F
        p.append(payload)
        return p
    }

    // MARK: - PSI

    private func patPacket() -> Data {
        var s = Data([0x00,                          // pointer_field
                      0x00,                          // table_id = PAT
                      0xB0, 0x0D,                    // section_syntax=1, length=13
                      0x00, 0x01,                    // transport_stream_id
                      0xC1,                          // version 0, current_next
                      0x00, 0x00,                    // section/last_section
                      0x00, 0x01,                    // program_number = 1
                      0xF0, 0x00])                   // PMT PID 0x1000
        appendCRC(&s)
        return tsPacket(pid: 0, pusi: true, cc: &ccPAT, payload: s)
    }

    private func pmtPacket() -> Data {
        var s = Data([0x00,
                      0x02,                          // table_id = PMT
                      0xB0, 0x12,                    // length=18
                      0x00, 0x01,                    // program_number
                      0xC1, 0x00, 0x00,
                      0xE1, 0x00,                    // PCR PID 0x0100
                      0xF0, 0x00,                    // program_info_length 0
                      streamType,                   // stream_type: H.264/HEVC
                      0xE1, 0x00,                    // elementary PID 0x0100
                      0xF0, 0x00])                   // ES_info_length 0
        appendCRC(&s)
        return tsPacket(pid: Self.pmtPID, pusi: true, cc: &ccPMT, payload: s)
    }

    private func appendCRC(_ s: inout Data) {
        let crc = Self.crc32MPEG([UInt8](s.dropFirst()))   // CRC 从 table_id 起算（不含 pointer）
        s.append(UInt8(crc >> 24 & 0xFF)); s.append(UInt8(crc >> 16 & 0xFF))
        s.append(UInt8(crc >> 8 & 0xFF)); s.append(UInt8(crc & 0xFF))
    }

    // MARK: - 工具

    /// PTS 编码 '0010' + 33bit + marker，5 字节
    static func ptsBytes(_ pts: UInt64) -> [UInt8] {
        let p = pts & 0x1_FFFF_FFFF
        return [
            0x21 | UInt8((p >> 29) & 0x0E),
            UInt8((p >> 22) & 0xFF),
            0x01 | UInt8((p >> 14) & 0xFE),
            UInt8((p >> 7) & 0xFF),
            0x01 | UInt8((p << 1) & 0xFE),
        ]
    }

    /// PCR：base(33bit, 27MHz) + ext(9bit=0)，6 字节
    static func pcrBytes(_ pts90: UInt64) -> [UInt8] {
        let base = (pts90 &* 300) & 0x1_FFFF_FFFF
        return [
            UInt8((base >> 25) & 0xFF),
            UInt8((base >> 17) & 0xFF),
            UInt8((base >> 9) & 0xFF),
            UInt8((base >> 1) & 0xFF),
            UInt8(((base & 0x01) << 7) | 0x7E),
            0x00,
        ]
    }

    /// CRC32/MPEG-2（poly 0x04C11DB7，无反射）
    static func crc32MPEG(_ bytes: [UInt8]) -> UInt32 {
        var crc: UInt32 = 0xFFFF_FFFF
        for b in bytes {
            crc ^= UInt32(b) << 24
            for _ in 0..<8 {
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1
            }
        }
        return crc
    }
}
