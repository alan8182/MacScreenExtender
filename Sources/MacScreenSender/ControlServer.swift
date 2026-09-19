import Foundation
import Network
import AppKit
import QuartzCore

/// 控制通道：TCP 8081，16 字节定长包
/// byte0 类型：1=move(nx f32@4, ny f32@8) 2=down(btn@4) 3=up(btn@4)
///            4=wheel(dx f32@4, dy f32@8) 5=key(down@4, code u16@6)
///            7=心跳 8=光标回报(nx f32@4, ny f32@8, visible u8@12)（Mac→客户端）
final class ControlServer: @unchecked Sendable {
    private var listener: NWListener?
    private let injector: InputInjector
    private let queue = DispatchQueue(label: "control.server")
    private var conns: [ObjectIdentifier: NWConnection] = [:]

    /// 当前光标状态（归一化 + 是否在虚拟屏内），App 注入
    var cursorProvider: (() -> (nx: Float, ny: Float, inside: Bool))?
    /// 控制通道鉴权（按 IP），App 注入
    var isAuthorized: ((String) -> Bool)?
    /// 是否有控制客户端在线（在线时视频里不再叠光标，由客户端本地画）
    var hasClients: Bool { queue.sync { !conns.isEmpty } }

    init(injector: InputInjector) {
        self.injector = injector
    }

    func start(port: UInt16) throws {
        let l = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
        l.newConnectionHandler = { [weak self] conn in
            guard let self else { return }
            if let check = self.isAuthorized {
                let s = "\(conn.endpoint)"
                let ip = s.contains(":") ? String(s[..<s.lastIndex(of: ":")!]) : s
                if !check(ip) {
                    print("🚫 未配对设备 \(s) 控制通道拒绝")
                    conn.cancel()
                    return
                }
            }
            print("🔗 控制通道接入: \(conn.endpoint)")
            conn.start(queue: self.queue)
            self.conns[ObjectIdentifier(conn)] = conn
            self.cursorLast = (-1, -1, false)   // 让新客户端立刻收到当前光标状态
            self.receive(conn, buf: Data())
            self.startKeepalive(conn)
        }
        l.stateUpdateHandler = { _ in }
        l.start(queue: queue)
        listener = l
        startCursorTimer()
    }

    /// 60Hz 光标回报：只在坐标变化时发包，客户端本地绘制（0ms 感知延迟的关键）
    private func startCursorTimer() {
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: 0.008)
        t.setEventHandler { [weak self] in
            guard let self, !self.conns.isEmpty, let p = self.cursorProvider else { return }
            let c = p()
            guard c.nx != self.cursorLast.0 || c.ny != self.cursorLast.1 || c.inside != self.cursorLast.2 else { return }
            self.cursorLast = (c.nx, c.ny, c.inside)
            var packet = [UInt8](repeating: 0, count: 16)
            packet[0] = 8
            var f = c.nx.bitPattern.littleEndian
            withUnsafeBytes(of: &f) { packet.replaceSubrange(4..<8, with: $0) }
            f = c.ny.bitPattern.littleEndian
            withUnsafeBytes(of: &f) { packet.replaceSubrange(8..<12, with: $0) }
            packet[12] = c.inside ? 1 : 0
            let data = Data(packet)
            for conn in self.conns.values {
                conn.send(content: data, completion: .contentProcessed { [weak self] error in
                    if error != nil { self?.drop(conn) }
                })
            }
        }
        t.resume()
        cursorTimer = t
    }

    private func drop(_ conn: NWConnection) {
        queue.async { [weak self] in
            guard let self else { return }
            if self.conns.removeValue(forKey: ObjectIdentifier(conn)) != nil {
                self.keepaliveTimers.removeValue(forKey: ObjectIdentifier(conn))
                conn.cancel()
            }
        }
    }

    /// 每 2 秒向客户端发心跳（type 7，带递增序号），接收端原样回传 → Mac 测得 RTT
    private func startKeepalive(_ conn: NWConnection) {
        let t = DispatchSource.makeTimerSource(queue: queue)
        keepaliveTimers[ObjectIdentifier(conn)] = t   // 强持有，防止定时器被释放后心跳静默失效
        t.schedule(deadline: .now() + 2, repeating: 2)   // 没有 schedule 定时器永不触发，接收端会 6 秒一断
        t.setEventHandler { [weak self, weak t] in
            guard let self else { return }
            self.keepaliveSeq &+= 1
            let seq = self.keepaliveSeq
            var packet = [UInt8](repeating: 0, count: 16)
            packet[0] = 7
            packet[4] = UInt8(seq & 0xFF)
            packet[5] = UInt8((seq >> 8) & 0xFF)
            packet[6] = UInt8((seq >> 16) & 0xFF)
            packet[7] = UInt8((seq >> 24) & 0xFF)
            self.recordSentSeq(seq)
            conn.send(content: Data(packet), completion: .contentProcessed { [weak self] error in
                guard let self else { return }
                if let error {
                    print("❌ 心跳发送失败: \(error)")
                    t?.cancel()
                    self.keepaliveTimers.removeValue(forKey: ObjectIdentifier(conn))
                    conn.cancel()
                } else {
                    self.keepaliveSent += 1
                    if self.keepaliveSent % 5 == 1 {
                        print("💓 心跳已发送 \(self.keepaliveSent) 次")
                    }
                }
            })
        }
        t.resume()
    }

    private func receive(_ conn: NWConnection, buf: Data) {
        conn.receive(minimumIncompleteLength: 16, maximumLength: 64 * 1024) { [weak self] data, _, _, error in
            guard let self, error == nil else { return }
            var b = buf
            if let d = data { b.append(d) }
            while b.count >= 16 {
                self.handlePacket(b.prefix(16))
                b.removeFirst(16)
            }
            self.receive(conn, buf: b)
        }
    }

    private var packetCount = 0
    private var keepaliveSent = 0
    private var keepaliveTimers: [ObjectIdentifier: DispatchSourceTimer] = [:]
    private var cursorTimer: DispatchSourceTimer?
    private var cursorLast: (Float, Float, Bool) = (-1, -1, false)

    // RTT 统计（心跳回传测得）
    private let rttLock = NSLock()
    private var keepaliveSeq: UInt32 = 0
    private var _lastInjectTime: Double = -100

    /// 距最近一次 Windows 注入的毫秒数（光标所有权判定用）
    var msSinceLastInject: Int64 { Int64((ProcessInfo.processInfo.systemUptime - _lastInjectTime) * 1000) }
    private var pendingSeqs: [UInt32: CFTimeInterval] = [:]
    private var rttEwmaMs: Double = 0
    private var rttMinMs: Double = 0

    /// RTT 是否高于基准（缓冲膨胀信号，AIMD 用）
    var rttElevated: Bool {
        rttLock.lock(); defer { rttLock.unlock() }
        return rttEwmaMs > 0 && rttEwmaMs > rttMinMs * 2 + 50
    }

    private func recordSentSeq(_ seq: UInt32) {
        rttLock.lock()
        pendingSeqs[seq] = CACurrentMediaTime()
        if pendingSeqs.count > 64 {   // 淘汰最旧
            if let oldest = pendingSeqs.keys.min() { pendingSeqs.removeValue(forKey: oldest) }
        }
        rttLock.unlock()
    }

    private func recordEcho(_ seq: UInt32) {
        let now = CACurrentMediaTime()
        rttLock.lock()
        defer { rttLock.unlock() }
        guard let sent = pendingSeqs.removeValue(forKey: seq) else { return }
        let ms = (now - sent) * 1000
        if rttEwmaMs == 0 { rttEwmaMs = ms; rttMinMs = ms }
        else {
            rttEwmaMs = rttEwmaMs * 0.7 + ms * 0.3
            rttMinMs = min(ms, rttMinMs * 1.0005 + 0.01)   // 缓慢遗忘历史最低，适应网络改善
        }
    }

    private func handlePacket(_ p: Data) {
        let d = [UInt8](p)
        let type = d[0]
        packetCount += 1
        if packetCount % 200 == 1 {
            print("🎮 控制事件已收到 \(packetCount) 个（类型序列正常）")
        }
        func f32(_ off: Int) -> Float {
            Float(bitPattern: UInt32(d[off]) | UInt32(d[off+1]) << 8 | UInt32(d[off+2]) << 16 | UInt32(d[off+3]) << 24)
        }
        switch type {
        case 1:
            _lastInjectTime = ProcessInfo.processInfo.systemUptime
            injector.move(nx: f32(4), ny: f32(8))
        case 2, 3:
            _lastInjectTime = ProcessInfo.processInfo.systemUptime
            injector.button(d[4], isDown: type == 2, nx: f32(4), ny: f32(8))
        case 4:
            injector.scroll(dx: f32(4), dy: f32(8))
        case 5:
            let down = d[4] == 1
            let code = UInt16(d[6]) | UInt16(d[7]) << 8
            injector.key(code, isDown: down)
        case 7:   // 心跳回传 → RTT
            let seq = UInt32(d[4]) | UInt32(d[5]) << 8 | UInt32(d[6]) << 16 | UInt32(d[7]) << 24
            let first = (rttEwmaMs == 0)
            recordEcho(seq)
            if first, rttEwmaMs > 0 {
                print(String(format: "🔁 控制通道 RTT %.0fms", rttEwmaMs))
            }
        default:
            break
        }
    }
}
