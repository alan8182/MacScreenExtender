import Foundation
import Network

/// 广播器：把 TS 字节流发给所有已接入的客户端（带在途字节上限，慢客户端直接踢掉重连）
final class Broadcaster: @unchecked Sendable {
    private let q = DispatchQueue(label: "ts.broadcast")
    private var clients: [ObjectIdentifier: (conn: NWConnection, inflight: Int)] = [:]
    static let maxInflight = 8_000_000   // 8MB：AIMD 生效后正常到不了这里，纯兜底

    var count: Int { q.sync { clients.count } }

    /// 所有客户端里最大的在途字节数（码率自适应的拥塞信号）
    var maxInflight: Int {
        q.sync { clients.values.map(\.inflight).max() ?? 0 }
    }

    func add(_ conn: NWConnection) {
        q.sync {
            clients[ObjectIdentifier(conn)] = (conn, 0)
        }
    }

    func remove(_ conn: NWConnection) {
        q.sync {
            removeLocked(conn)
        }
    }

    private func removeLocked(_ conn: NWConnection) {
        if clients.removeValue(forKey: ObjectIdentifier(conn)) != nil {
            conn.cancel()
        }
    }

    /// 定向发送（新客户端的 PAT/PMT）
    func send(to conn: NWConnection, _ data: Data) {
        q.sync {
            guard clients[ObjectIdentifier(conn)] != nil else { return }
            sendLocked(conn, data)
        }
    }

    func broadcast(_ data: Data) {
        q.sync {
            var toKick: [NWConnection] = []
            for (id, var entry) in clients {
                if entry.inflight > Self.maxInflight {
                    toKick.append(entry.conn)
                    continue
                }
                entry.inflight += data.count
                clients[id] = entry
                sendLocked(entry.conn, data)
            }
            toKick.forEach { removeLocked($0) }
        }
    }

    private func sendLocked(_ conn: NWConnection, _ data: Data) {
        let id = ObjectIdentifier(conn)
        conn.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            self.q.sync {
                guard var entry = self.clients[id] else { return }
                entry.inflight -= data.count
                self.clients[id] = entry
                if error != nil {
                    self.removeLocked(conn)
                }
            }
        })
    }
}

/// 极简 HTTP 服务：GET /stream → 无限 MPEG-TS 流；GET / → 状态文本
final class HTTPServer: @unchecked Sendable {
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "http.server")
    /// (连接, 路径, 查询参数) — 由上层注册
    var onRequest: ((NWConnection, String, [String: String]) -> Void)?

    func start(port: UInt16) throws {
        let l = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
        l.newConnectionHandler = { [weak self] conn in
            conn.start(queue: self?.queue ?? .global())
            self?.receiveHead(conn, buf: Data())
        }
        l.stateUpdateHandler = { state in
            if case .failed(let err) = state {
                print("❌ HTTP 服务异常: \(err)")
            }
        }
        l.start(queue: queue)
        listener = l
    }

    private func receiveHead(_ conn: NWConnection, buf: Data) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { [weak self] data, _, _, error in
            guard let self, error == nil else { return }
            var b = buf
            if let d = data { b.append(d) }
            if let range = b.range(of: Data("\r\n\r\n".utf8)) {
                let head = String(data: b[..<range.lowerBound], encoding: .utf8) ?? ""
                self.dispatch(conn: conn, head: head)
                self.drain(conn)   // 继续收包以感知断开
            } else if b.count > 65_536 {
                conn.cancel()
            } else {
                self.receiveHead(conn, buf: b)
            }
        }
    }

    private func drain(_ conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, _, error in
            guard let self, error == nil else { return }
            _ = data
            self.drain(conn)
        }
    }

    private func dispatch(conn: NWConnection, head: String) {
        guard let line = head.split(separator: "\r\n").first.map(String.init) else { return }
        let parts = line.split(separator: " ")
        guard parts.count >= 2 else { return }
        let target = String(parts[1])
        let path: String
        var params: [String: String] = [:]
        if let qIdx = target.firstIndex(of: "?") {
            path = String(target[..<qIdx])
            let query = String(target[target.index(after: qIdx)...])
            for pair in query.split(separator: "&") {
                let kv = pair.split(separator: "=", maxSplits: 1)
                if kv.count == 2 { params[String(kv[0])] = String(kv[1]) }
                else if kv.count == 1 { params[String(kv[0])] = "" }
            }
        } else {
            path = target
        }
        onRequest?(conn, path, params)
    }

    /// 发送无限流式响应头（无 Content-Length，靠连接关闭收尾）
    static func sendStreamHead(_ conn: NWConnection, contentType: String = "video/mp2t") {
        let head = "HTTP/1.1 200 OK\r\nContent-Type: \(contentType)\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"
        conn.send(content: Data(head.utf8), completion: .contentProcessed { _ in })
    }

    static func sendText(_ conn: NWConnection, _ text: String, status: Int = 200) {
        let body = Data(text.utf8)
        let reason = status == 200 ? "OK" : "Forbidden"
        let head = "HTTP/1.1 \(status) \(reason)\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\nContent-Length: \(body.count)\r\n\r\n"
        conn.send(content: Data(head.utf8) + body, completion: .contentProcessed { _ in conn.cancel() })
    }
}
