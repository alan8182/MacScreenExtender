import Foundation
import Network

/// TCP 8081 控制通道客户端：负责手势触控回传、心跳回显、IDR 关键帧请求
public final class ControlClient: ObservableObject {
    @Published public private(set) var isConnected: Bool = false

    private var connection: NWConnection?
    private let queue = DispatchQueue(label: "com.macscreen.control", qos: .userInteractive)
    private var isRunning = false

    public init() {}

    public func connect(to ip: String, port: UInt16 = 8081) {
        disconnect()

        let host = NWEndpoint.Host(ip)
        let portEndpoint = NWEndpoint.Port(rawValue: port) ?? 8081
        let conn = NWConnection(host: host, port: portEndpoint, using: .tcp)

        conn.stateUpdateHandler = { [weak self] state in
            DispatchQueue.main.async {
                switch state {
                case .ready:
                    self?.isConnected = true
                    self?.requestKeyframe() // 连上立刻请求关键帧，实现首帧零等待
                case .failed, .cancelled:
                    self?.isConnected = false
                default:
                    break
                }
            }
        }

        conn.start(queue: queue)
        self.connection = conn
        self.isRunning = true
        receiveLoop()
    }

    public func disconnect() {
        isRunning = false
        connection?.cancel()
        connection = nil
        DispatchQueue.main.async {
            self.isConnected = false
        }
    }

    // MARK: - 发送控制包（16字节定长）

    /// 归一化移动鼠标（0.0 ~ 1.0）
    public func sendMove(nx: Float, ny: Float) {
        var p = [UInt8](repeating: 0, count: 16)
        p[0] = 1
        writeFloat(nx, to: &p, offset: 4)
        writeFloat(ny, to: &p, offset: 8)
        sendPacket(p)
    }

    /// 点击鼠标按键（btn: 0=左键, 1=右键）
    public func sendButton(_ btn: UInt8, down: Bool, nx: Float, ny: Float) {
        var p = [UInt8](repeating: 0, count: 16)
        p[0] = down ? 2 : 3
        p[4] = btn
        writeFloat(nx, to: &p, offset: 4) // 协议兼容：同时在 4 写入坐标
        writeFloat(ny, to: &p, offset: 8)
        sendPacket(p)
    }

    /// 平滑滚动（双指滑动换算为滚轮增量）
    public func sendScroll(dx: Float, dy: Float) {
        var p = [UInt8](repeating: 0, count: 16)
        p[0] = 4
        writeFloat(dx, to: &p, offset: 4)
        writeFloat(dy, to: &p, offset: 8)
        sendPacket(p)
    }

    /// 立即向 Mac 请求 IDR 关键帧（type 9）
    public func requestKeyframe() {
        var p = [UInt8](repeating: 0, count: 16)
        p[0] = 9
        sendPacket(p)
    }

    private func sendPacket(_ packet: [UInt8]) {
        guard let conn = connection, isConnected else { return }
        conn.send(content: Data(packet), completion: .contentProcessed { _ in })
    }

    private func writeFloat(_ value: Float, to array: inout [UInt8], offset: Int) {
        var bits = value.bitPattern.littleEndian
        withUnsafeBytes(of: &bits) { ptr in
            for i in 0..<4 {
                array[offset + i] = ptr[i]
            }
        }
    }

    // MARK: - 接收循环（处理 Mac 心跳并即时回传）

    private func receiveLoop() {
        guard isRunning, let conn = connection else { return }
        conn.receive(minimumIncompleteLength: 16, maximumLength: 64) { [weak self] data, _, isComplete, error in
            guard let self = self, self.isRunning, error == nil, let d = data, d.count >= 16 else {
                return
            }

            let bytes = [UInt8](d)
            if bytes[0] == 7 { // 心跳包：原样回送序号
                self.sendPacket(Array(bytes.prefix(16)))
            }

            self.receiveLoop()
        }
    }
}
