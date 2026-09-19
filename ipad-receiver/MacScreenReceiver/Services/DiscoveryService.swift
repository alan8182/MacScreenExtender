import Foundation
import Network
import Combine

/// 局域网 UDP 8082 广播监听器，实现零配置自动发现在线 Mac
public final class DiscoveryService: ObservableObject {
    @Published public private(set) var discoveredMacs: [DiscoveredMac] = []
    @Published public private(set) var isListening: Bool = false

    private var listener: NWListener?
    private let queue = DispatchQueue(label: "com.macscreen.discovery", qos: .utility)
    private var cleanupTimer: Timer?

    public init() {}

    public func startListening() {
        guard listener == nil else { return }

        do {
            let params = NWParameters.udp
            params.allowLocalEndpointReuse = true
            let l = try NWListener(using: params, on: 8082)

            l.stateUpdateHandler = { [weak self] state in
                DispatchQueue.main.async {
                    switch state {
                    case .ready:
                        self?.isListening = true
                    case .failed(let err):
                        print("UDP 监听失败: \(err)")
                        self?.isListening = false
                    case .cancelled:
                        self?.isListening = false
                    default:
                        break
                    }
                }
            }

            l.newConnectionHandler = { [weak self] connection in
                connection.start(queue: self?.queue ?? .global())
                self?.receiveMessage(from: connection)
            }

            l.start(queue: queue)
            self.listener = l

            DispatchQueue.main.async {
                self.cleanupTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
                    self?.pruneStaleDevices()
                }
            }
        } catch {
            print("初始化 UDP 8082 监听器异常: \(error)")
        }
    }

    public func stopListening() {
        cleanupTimer?.invalidate()
        cleanupTimer = nil
        listener?.cancel()
        listener = nil
        DispatchQueue.main.async {
            self.isListening = false
        }
    }

    private func receiveMessage(from connection: NWConnection) {
        connection.receiveMessage { [weak self] content, _, isComplete, error in
            defer { connection.cancel() }
            guard let self = self, let data = content, error == nil else { return }

            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let name = json["name"] as? String,
               let ip = json["ip"] as? String,
               let port = json["port"] as? Int {

                let proto = json["proto"] as? Int ?? 1
                let mac = DiscoveredMac(name: name, ip: ip, port: port, proto: proto, lastSeen: Date())

                DispatchQueue.main.async {
                    if let idx = self.discoveredMacs.firstIndex(where: { $0.ip == ip }) {
                        self.discoveredMacs[idx] = mac
                    } else {
                        self.discoveredMacs.append(mac)
                    }
                }
            }
        }
    }

    private func pruneStaleDevices() {
        let cutoff = Date().addingTimeInterval(-10)
        discoveredMacs.removeAll { $0.lastSeen < cutoff }
    }
}
