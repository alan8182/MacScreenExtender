import SwiftUI
import AVFoundation

/// 扩展屏全屏硬件渲染视图，集成手势与 Apple Pencil 触控映射
public struct DisplayView: View {
    @ObservedObject var streamClient: VideoStreamClient
    @ObservedObject var controlClient: ControlClient
    let onExit: () -> Void

    @State private var showControlsOverlay = false
    @State private var overlayDismissWork: DispatchWorkItem?

    public init(streamClient: VideoStreamClient, controlClient: ControlClient, onExit: @escaping () -> Void) {
        self.streamClient = streamClient
        self.controlClient = controlClient
        self.onExit = onExit
    }

    public var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            SampleBufferDisplayViewRepresentable(
                streamClient: streamClient,
                controlClient: controlClient,
                onTwoFingerDoubleTap: {
                    withAnimation {
                        showControlsOverlay.toggle()
                        scheduleOverlayAutoDismiss()
                    }
                }
            )
            .ignoresSafeArea()

            // 悬浮状态与退出 OSD
            if showControlsOverlay {
                VStack {
                    HStack {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Mac 随航拓展屏")
                                .font(.headline)
                                .foregroundColor(.white)
                            Text("已连接 · \(streamClient.currentFPS) FPS · 硬件直解")
                                .font(.caption)
                                .foregroundColor(.green)
                        }
                        .padding(.horizontal, 16)
                        .padding(.vertical, 8)
                        .background(.ultraThinMaterial)
                        .cornerRadius(12)

                        Spacer()

                        Button(action: onExit) {
                            HStack(spacing: 6) {
                                Image(systemName: "xmark.circle.fill")
                                Text("退出全屏")
                            }
                            .font(.system(size: 15, weight: .semibold))
                            .foregroundColor(.white)
                            .padding(.horizontal, 16)
                            .padding(.vertical, 10)
                            .background(Color.red.opacity(0.85))
                            .cornerRadius(12)
                        }
                    }
                    .padding(.top, 24)
                    .padding(.horizontal, 24)

                    Spacer()

                    Text("双指双击屏幕任意位置即可显隐本控制栏")
                        .font(.caption2)
                        .foregroundColor(.white.opacity(0.6))
                        .padding(.bottom, 16)
                }
                .transition(.opacity)
            }
        }
        .statusBar(hidden: true)
        .onAppear {
            scheduleOverlayAutoDismiss()
        }
    }

    private func scheduleOverlayAutoDismiss() {
        overlayDismissWork?.cancel()
        let work = DispatchWorkItem {
            withAnimation {
                self.showControlsOverlay = false
            }
        }
        overlayDismissWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 4.0, execute: work)
    }
}

// MARK: - UIKit AVSampleBufferDisplayLayer 宿主与手势拦截

final class VideoLayerContainerView: UIView, UIGestureRecognizerDelegate {
    private let displayLayer = AVSampleBufferDisplayLayer()
    private weak var controlClient: ControlClient?
    var onTwoFingerDoubleTap: (() -> Void)?

    init(controlClient: ControlClient) {
        self.controlClient = controlClient
        super.init(frame: .zero)
        backgroundColor = .black

        displayLayer.videoGravity = .resizeAspect
        layer.addSublayer(displayLayer)

        setupGestures()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layoutSubviews() {
        super.layoutSubviews()
        displayLayer.frame = bounds
    }

    func getDisplayLayer() -> AVSampleBufferDisplayLayer {
        return displayLayer
    }

    private func setupGestures() {
        isMultipleTouchEnabled = true

        // 1. 双指双击唤醒菜单
        let twoFingerDoubleTap = UITapGestureRecognizer(target: self, action: #selector(handleTwoFingerDoubleTap))
        twoFingerDoubleTap.numberOfTouchesRequired = 2
        twoFingerDoubleTap.numberOfTapsRequired = 2
        addGestureRecognizer(twoFingerDoubleTap)

        // 2. 单指点击/拖拽映射鼠标
        let singleTap = UITapGestureRecognizer(target: self, action: #selector(handleSingleTap(_:)))
        singleTap.numberOfTouchesRequired = 1
        singleTap.require(toFail: twoFingerDoubleTap)
        addGestureRecognizer(singleTap)

        let panGesture = UIPanGestureRecognizer(target: self, action: #selector(handlePan(_:)))
        panGesture.maximumNumberOfTouches = 2
        panGesture.delegate = self
        addGestureRecognizer(panGesture)
    }

    @objc private func handleTwoFingerDoubleTap() {
        onTwoFingerDoubleTap?()
    }

    @objc private func handleSingleTap(_ gesture: UITapGestureRecognizer) {
        let pt = gesture.location(in: self)
        let nx = Float(pt.x / bounds.width)
        let ny = Float(pt.y / bounds.height)

        controlClient?.sendMove(nx: nx, ny: ny)
        controlClient?.sendButton(0, down: true, nx: nx, ny: ny)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
            self?.controlClient?.sendButton(0, down: false, nx: nx, ny: ny)
        }
    }

    @objc private func handlePan(_ gesture: UIPanGestureRecognizer) {
        if gesture.numberOfTouches == 2 {
            // 双指平滑滚轮
            let velocity = gesture.velocity(in: self)
            let dx = Float(-velocity.x / 500.0)
            let dy = Float(velocity.y / 500.0)
            controlClient?.sendScroll(dx: dx, dy: dy)
            return
        }

        // 单指移动与拖拽
        let pt = gesture.location(in: self)
        let nx = Float(pt.x / bounds.width)
        let ny = Float(pt.y / bounds.height)

        switch gesture.state {
        case .began:
            controlClient?.sendMove(nx: nx, ny: ny)
            controlClient?.sendButton(0, down: true, nx: nx, ny: ny)
        case .changed:
            controlClient?.sendMove(nx: nx, ny: ny)
        case .ended, .cancelled:
            controlClient?.sendButton(0, down: false, nx: nx, ny: ny)
        default:
            break
        }
    }

    // 处理 Apple Pencil 触控
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesBegan(touches, with: event)
        guard let touch = touches.first, touch.type == .pencil else { return }
        let pt = touch.location(in: self)
        let nx = Float(pt.x / bounds.width)
        let ny = Float(pt.y / bounds.height)
        controlClient?.sendMove(nx: nx, ny: ny)
        controlClient?.sendButton(0, down: true, nx: nx, ny: ny)
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesMoved(touches, with: event)
        guard let touch = touches.first, touch.type == .pencil else { return }
        let pt = touch.location(in: self)
        let nx = Float(pt.x / bounds.width)
        let ny = Float(pt.y / bounds.height)
        controlClient?.sendMove(nx: nx, ny: ny)
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesEnded(touches, with: event)
        guard let touch = touches.first, touch.type == .pencil else { return }
        let pt = touch.location(in: self)
        let nx = Float(pt.x / bounds.width)
        let ny = Float(pt.y / bounds.height)
        controlClient?.sendButton(0, down: false, nx: nx, ny: ny)
    }
}

struct SampleBufferDisplayViewRepresentable: UIViewRepresentable {
    let streamClient: VideoStreamClient
    let controlClient: ControlClient
    let onTwoFingerDoubleTap: () -> Void

    func makeUIView(context: Context) -> VideoLayerContainerView {
        let view = VideoLayerContainerView(controlClient: controlClient)
        view.onTwoFingerDoubleTap = onTwoFingerDoubleTap
        streamClient.setDisplayLayer(view.getDisplayLayer())
        return view
    }

    func updateUIView(_ uiView: VideoLayerContainerView, context: Context) {}
}
