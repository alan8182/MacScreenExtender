import Foundation
import CoreMedia
import CoreVideo
import ScreenCaptureKit

/// ScreenCaptureKit 采集指定显示器 → 拷贝帧 → 逐像素叠加软光标 → onFrame 回调
///
/// 两个实测出来的坑，别改回去：
/// 1. SCK 对虚拟屏不会合成系统光标（showsCursor 无效），必须软件叠加；
/// 2. 对 SCK 的 IOSurface 背书缓冲用 CGContext 绘制会被静默丢弃（字节直写则可见，
///    已用 CURSOR_MARK 白块实验对照验证），所以光标走"预渲染位图 + 手写 alpha 混合"。
final class CaptureService: NSObject, SCStreamOutput, @unchecked Sendable {
    private var stream: SCStream?
    private let frameQueue = DispatchQueue(label: "capture.frames")

    /// 统一编码入口：（已叠加光标的像素缓冲, PTS）。缓冲来自内部池，回调返回后可能被复用
    var onFrame: ((CVPixelBuffer, CMTime) -> Void)?
    /// 虚拟屏 displayID（每帧实时读 bounds，macOS 可能自动挪动排布）
    var displayID: CGDirectDisplayID = 0
    /// 是否有消费者（无客户端时暂停合成帧，省 CPU）
    var hasConsumers: () -> Bool = { true }
    /// 是否把光标画进视频（客户端接管光标时不画，避免双重光标）
    var shouldStampCursor: () -> Bool = { true }
    /// 合成帧节拍（秒）。光标在虚拟屏内 40fps 跟手；离开后 5fps 省电省带宽
    var tickRate: () -> Double = { 0.025 }

    private var lastFrame: CVPixelBuffer?          // SCK 原始帧，永不画光标（画了会拖尾）
    private var lastLiveTime: CFTimeInterval = 0   // 最近一次实时帧时刻，tick 避让用
    private var lastEmit: CFTimeInterval = 0       // 最近一次合成帧时刻，自适应节流用
    private var cancelled = false                  // start 超时后被放弃：迟到的启动成功要立刻自停
    private var tickTimer: DispatchSourceTimer?
    private var statsTimer: DispatchSourceTimer?
    private var pool: CVPixelBufferPool?
    private var poolW = 0, poolH = 0

    // 10 秒统计
    private var statLive = 0, statSynthetic = 0, statCursorIn = 0

    func start(displayID: CGDirectDisplayID, width: Int, height: Int, fps: Int) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            throw AppError.capture("采集失败：显示器列表里找不到 displayID=\(displayID)，可用: \(content.displays.map { $0.displayID })")
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.width = width
        cfg.height = height
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        cfg.queueDepth = 3
        cfg.showsCursor = false   // 虚拟屏上不生效，软件叠加
        cfg.pixelFormat = kCVPixelFormatType_32BGRA

        let s = SCStream(filter: filter, configuration: cfg, delegate: nil)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: frameQueue)
        try await s.startCapture()
        if cancelled {   // 启动超时后迟到的成功：立即自停，不留僵尸流
            try? await s.stopCapture()
            throw AppError.capture("启动已超时取消")
        }
        stream = s
        makePool(width: width, height: height)
        startTick()
        startStats()
    }

    private func makePool(width: Int, height: Int) {
        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey: width,
            kCVPixelBufferHeightKey: height,
            kCVPixelBufferPoolMinimumBufferCountKey: 4,
        ]
        var p: CVPixelBufferPool?
        CVPixelBufferPoolCreate(kCFAllocatorDefault, nil, attrs as CFDictionary, &p)
        pool = p
        poolW = width
        poolH = height
    }

    /// 自适应保底帧：光标已由客户端本地绘制，合成帧只剩保活作用——10fps 足够
    /// （客户端离线时 5fps）。实时帧刚到过（33ms 内）则跳过本次，避免双重编码
    private func startTick() {
        let t = DispatchSource.makeTimerSource(queue: frameQueue)
        t.schedule(deadline: .now(), repeating: 0.01)
        t.setEventHandler { [weak self] in
            guard let self, self.hasConsumers(), let last = self.lastFrame else { return }
            let now = CACurrentMediaTime()
            if now - self.lastLiveTime < 0.033 { return }
            if now - self.lastEmit < self.tickRate() { return }
            self.lastEmit = now
            self.emitCopy(of: last, pts: CMClockGetTime(CMClockGetHostTimeClock()), synthetic: true)
        }
        t.resume()
        tickTimer = t
    }

    private func startStats() {
        let t = DispatchSource.makeTimerSource(queue: frameQueue)
        t.schedule(deadline: .now() + 10, repeating: 10)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            print("📊 10s: 实时帧 \(self.statLive), 合成帧 \(self.statSynthetic), 光标在虚拟屏内 \(self.statCursorIn) 帧")
            self.statLive = 0; self.statSynthetic = 0; self.statCursorIn = 0
        }
        t.resume()
        statsTimer = t
    }

    func stop() {
        cancelled = true
        tickTimer?.cancel(); tickTimer = nil
        statsTimer?.cancel(); statsTimer = nil
        lastFrame = nil
        pool = nil
        guard let s = stream else { return }
        stream = nil
        let sem = DispatchSemaphore(value: 0)
        Task {
            try? await s.stopCapture()
            sem.signal()
        }
        _ = sem.wait(timeout: .now() + 3)   // 有界等待：stopCapture 卡死也不能拖垮配置队列
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid, let pb = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lastFrame = pb
        lastLiveTime = CACurrentMediaTime()
        emitCopy(of: pb, pts: CMSampleBufferGetPresentationTimeStamp(sampleBuffer), synthetic: false)
    }

    /// 从池里取缓冲 → 整帧拷贝 → 叠加光标 → 回调编码
    private func emitCopy(of src: CVPixelBuffer, pts: CMTime, synthetic: Bool) {
        guard let pool else { return }
        var tmp: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &tmp)
        guard let dst = tmp else { return }

        var cursorIn = false
        CVPixelBufferLockBaseAddress(src, [.readOnly])
        CVPixelBufferLockBaseAddress(dst, [])
        if let srcBase = CVPixelBufferGetBaseAddress(src), let dstBase = CVPixelBufferGetBaseAddress(dst) {
            let w4 = min(CVPixelBufferGetWidth(src), poolW) * 4
            let srcBPR = CVPixelBufferGetBytesPerRow(src)
            let dstBPR = CVPixelBufferGetBytesPerRow(dst)
            let rows = min(CVPixelBufferGetHeight(src), poolH)
            for r in 0..<rows {
                memcpy(dstBase + r * dstBPR, srcBase + r * srcBPR, w4)
            }
            cursorIn = Self.stampCursor(on: dst, displayBounds: CGDisplayBounds(displayID),
                                        enabled: shouldStampCursor())
        }
        CVPixelBufferUnlockBaseAddress(dst, [])
        CVPixelBufferUnlockBaseAddress(src, [.readOnly])

        if synthetic { statSynthetic += 1 } else { statLive += 1 }
        if cursorIn { statCursorIn += 1 }
        onFrame?(dst, pts)
    }

    // MARK: - 软件光标（字节直写 + alpha 混合）

    /// 在（已锁定的）目标缓冲上叠加光标，返回光标是否落在虚拟屏内
    static func stampCursor(on pb: CVPixelBuffer, displayBounds: CGRect, enabled: Bool = true) -> Bool {
        let w = CVPixelBufferGetWidth(pb), h = CVPixelBufferGetHeight(pb)
        guard let base = CVPixelBufferGetBaseAddress(pb) else { return false }
        let bpr = CVPixelBufferGetBytesPerRow(pb)
        let px = base.assumingMemoryBound(to: UInt8.self)

        // 诊断开关：CURSOR_MARK=1 时左上角画浅色块，验证"字节直写→编码→解码"通路
        if ProcessInfo.processInfo.environment["CURSOR_MARK"] == "1" {
            fillRect(px, bpr, 10, 10, 24, 24, r: 230, g: 230, b: 230)
        }

        guard enabled, displayBounds != .zero,
              let loc = CGEvent(source: nil)?.location,
              displayBounds.contains(loc) else { return false }
        let scaleX = CGFloat(w) / displayBounds.width
        let scaleY = CGFloat(h) / displayBounds.height
        let x0 = Int((loc.x - displayBounds.minX) * scaleX)
        let y0 = Int((loc.y - displayBounds.minY) * scaleY)
        blendSprite(px, bpr, w, h, x0, y0)
        return true
    }

    /// 目标是 BGRA（little-endian 内存序 B,G,R,A）；sprite 是预乘 RGBA
    private static func blendSprite(_ px: UnsafeMutablePointer<UInt8>, _ bpr: Int,
                                    _ w: Int, _ h: Int, _ x0: Int, _ y0: Int) {
        let (sw, sh, sp) = cursorSprite
        let sBPR = sw * 4
        for row in 0..<sh {
            let dy = y0 + row
            guard dy >= 0, dy < h else { continue }
            for col in 0..<sw {
                let dx = x0 + col
                guard dx >= 0, dx < w else { continue }
                let s = (row * sBPR) + col * 4
                let sa = Int(sp[s + 3])
                guard sa > 0 else { continue }
                let d = dy * bpr + dx * 4
                let inv = 255 - sa
                px[d + 0] = UInt8(min(255, Int(sp[s + 2]) + Int(px[d + 0]) * inv / 255))  // B
                px[d + 1] = UInt8(min(255, Int(sp[s + 1]) + Int(px[d + 1]) * inv / 255))  // G
                px[d + 2] = UInt8(min(255, Int(sp[s + 0]) + Int(px[d + 2]) * inv / 255))  // R
                px[d + 3] = 255                                                          // A
            }
        }
    }

    private static func fillRect(_ px: UnsafeMutablePointer<UInt8>, _ bpr: Int,
                                 _ x: Int, _ y: Int, _ w: Int, _ h: Int, r: Int, g: Int, b: Int) {
        for row in y..<(y + h) {
            let p = px + row * bpr + x * 4
            for col in 0..<w {
                p[col * 4 + 0] = UInt8(b)
                p[col * 4 + 1] = UInt8(g)
                p[col * 4 + 2] = UInt8(r)
                p[col * 4 + 3] = 255
            }
        }
    }

    /// 黑色箭头 + 白描边，tip 在左上角。在普通内存（非 IOSurface）上用 CGContext 渲染一次，
    /// 存成预乘 RGBA 位图，之后逐像素混合
    private static let cursorSprite: (w: Int, h: Int, px: [UInt8]) = {
        let w = 20, h = 28
        var data = [UInt8](repeating: 0, count: w * h * 4)
        data.withUnsafeMutableBytes { buf in
            guard let base = buf.baseAddress,
                  let ctx = CGContext(data: base, width: w, height: h,
                                      bitsPerComponent: 8, bytesPerRow: w * 4,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
            ctx.translateBy(x: 0, y: CGFloat(h))   // 翻成 y 向下，path 按"屏幕坐标"写
            ctx.scaleBy(x: 1, y: -1)
            ctx.setFillColor(CGColor(gray: 0.05, alpha: 1))
            ctx.setStrokeColor(CGColor(gray: 1, alpha: 1))
            ctx.setLineWidth(1.5)
            ctx.setLineJoin(.round)
            let p = CGMutablePath()
            p.move(to: CGPoint(x: 1, y: 1))
            p.addLine(to: CGPoint(x: 1, y: 21))
            p.addLine(to: CGPoint(x: 5.5, y: 16.5))
            p.addLine(to: CGPoint(x: 8.5, y: 23.5))
            p.addLine(to: CGPoint(x: 11.5, y: 22.3))
            p.addLine(to: CGPoint(x: 8.5, y: 15.5))
            p.addLine(to: CGPoint(x: 14, y: 15.5))
            p.closeSubpath()
            ctx.addPath(p)
            ctx.drawPath(using: .fillStroke)
        }
        return (w, h, data)
    }()
}
