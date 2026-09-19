import Foundation

public struct DiscoveredMac: Identifiable, Hashable, Codable {
    public var id: String { "\(ip):\(port)" }
    public let name: String
    public let ip: String
    public let port: Int
    public let proto: Int
    public var lastSeen: Date

    public init(name: String, ip: String, port: Int, proto: Int, lastSeen: Date = Date()) {
        self.name = name
        self.ip = ip
        self.port = port
        self.proto = proto
        self.lastSeen = lastSeen
    }
}
