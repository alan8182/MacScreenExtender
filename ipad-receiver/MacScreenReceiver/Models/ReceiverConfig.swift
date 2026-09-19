import Foundation

public struct ReceiverConfig: Codable {
    public var lastConnectedIP: String
    public var token: String
    public var scalePercent: Int // 100 or 200 (Retina)
    public var targetFPS: Int    // 60 or 120 (ProMotion)
    public var preferHEVC: Bool
    public var autoConnect: Bool

    public static let `default` = ReceiverConfig(
        lastConnectedIP: "",
        token: "",
        scalePercent: 200, // iPad 默认超清视网膜 2x
        targetFPS: 60,
        preferHEVC: true,
        autoConnect: true
    )

    private static let key = "MacScreenReceiver_Config"

    public static func load() -> ReceiverConfig {
        guard let data = UserDefaults.standard.data(forKey: key),
              let config = try? JSONDecoder().decode(ReceiverConfig.self, from: data) else {
            return .default
        }
        return config
    }

    public func save() {
        if let data = try? JSONEncoder().encode(self) {
            UserDefaults.standard.set(data, forKey: Self.key)
        }
    }
}
