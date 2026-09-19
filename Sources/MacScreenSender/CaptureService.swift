import Foundation
import CoreMedia
import CoreVideo
import ScreenCaptureKit

/// ScreenCaptureKit 采集指定显示器 → 零拷贝直通 VideoToolbox 硬件编码器
///
/// ponytail: ScreenCaptureKit 原生 GPU 硬件合成光标（showsCursor=true），
/// 视频流全程纯 GPU IOSurface 零拷贝直通，彻底移除 CPU 内存拷贝与软件混合。
final class CaptureService: NSObject, SCStreamOutput, @unchecked Sendable {
    private var stream: SCStream?
    private let frameQueue = DispatchQueue(label: "capture.frames")

    /// 统一编码入口：（原始像素缓冲, PTS）
    var onFrame: ((CVPixelBuffer, CMTime) -> Void)?
    /// 虚拟屏 displayID
    var displayID: CGDirectDisplayID = 0
    /// 是否有消费者（无客户端时暂停合成帧，省 CPU）
    var hasConsumers: () -> Bool = { true }
    /// 合成帧节拍（秒）：画面静止时维持流活性
    var tickRate: () -> Double = { 0.1 }

    private var lastFrame: CVPixelBuffer?
    private var lastLiveTime: CFTimeInterval = 0   // 最近一次实时帧时刻，tick 避让用
    private var lastEmit: CFTimeInterval = 0       // 最近一次合成帧时刻，自适应节流用
    private var cancelled = false                  // start 超时后被放弃：迟到的启动成功要立刻自停
    private var tickTimer: DispatchSourceTimer?
    private var statsTimer: DispatchSourceTimer?

    // 10 秒统计
    private var statLive = 0, statSynthetic = 0

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
        cfg.showsCursor = true   // 原生 GPU 硬件合成光标入画，零 CPU 拷贝直通 VideoToolbox
        cfg.pixelFormat = kCVPixelFormatType_32BGRA

        let s = SCStream(filter: filter, configuration: cfg, delegate: nil)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: frameQueue)
        try await s.startCapture()
        if cancelled {
            try? await s.stopCapture()
            throw AppError.capture("启动已超时取消")
        }
        stream = s
        startTick()
        startStats()
    }

    /// 画面静止时的自适应保底帧（保活流），实时帧到过（33ms 内）则跳过
    private func startTick() {
        let t = DispatchSource.makeTimerSource(queue: frameQueue)
        t.schedule(deadline: .now(), repeating: 0.01)
        t.setEventHandler { [weak self] in
            guard let self, self.hasConsumers(), let last = self.lastFrame else { return }
            let now = CACurrentMediaTime()
            if now - self.lastLiveTime < 0.033 { return }
            if now - self.lastEmit < self.tickRate() { return }
            self.lastEmit = now
            self.statSynthetic += 1
            self.onFrame?(last, CMClockGetTime(CMClockGetHostTimeClock()))
        }
        t.resume()
        tickTimer = t
    }

    private func startStats() {
        let t = DispatchSource.makeTimerSource(queue: frameQueue)
        t.schedule(deadline: .now() + 10, repeating: 10)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            print("📊 10s: 实时帧 \(self.statLive), 保底合成帧 \(self.statSynthetic)")
            self.statLive = 0; self.statSynthetic = 0
        }
        t.resume()
        statsTimer = t
    }

    func stop() {
        cancelled = true
        tickTimer?.cancel(); tickTimer = nil
        statsTimer?.cancel(); statsTimer = nil
        lastFrame = nil
        guard let s = stream else { return }
        stream = nil
        let sem = DispatchSemaphore(value: 0)
        Task {
            try? await s.stopCapture()
            sem.signal()
        }
        _ = sem.wait(timeout: .now() + 3)
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid, let pb = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lastFrame = pb
        lastLiveTime = CACurrentMediaTime()
        statLive += 1
        // SCK 硬件 IOSurface 原生缓冲直通 VideoToolbox，零内存拷贝
        onFrame?(pb, CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
    }
}
