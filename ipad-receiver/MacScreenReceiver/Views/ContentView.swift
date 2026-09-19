import SwiftUI

public struct ContentView: View {
    @StateObject private var discoveryService = DiscoveryService()
    @StateObject private var streamClient = VideoStreamClient()
    @StateObject private var controlClient = ControlClient()

    @State private var config = ReceiverConfig.load()
    @State private var isStreaming = false
    @State private var targetIP: String = ""
    @State private var showPairSheet = false
    @State private var alertMessage: String?
    @State private var showAlert = false

    public init() {}

    public var body: some View {
        Group {
            if isStreaming {
                DisplayView(
                    streamClient: streamClient,
                    controlClient: controlClient,
                    onExit: {
                        exitSession()
                    }
                )
            } else {
                ConnectionView(
                    discoveryService: discoveryService,
                    config: $config,
                    onConnect: { ip in
                        startConnectWorkflow(ip: ip)
                    }
                )
            }
        }
        .sheet(isPresented: $showPairSheet) {
            PairSheet(
                macIP: targetIP,
                onPaired: { token in
                    config.token = token
                    config.lastConnectedIP = targetIP
                    config.save()
                    showPairSheet = false
                    launchStream(to: targetIP)
                },
                onCancel: {
                    showPairSheet = false
                }
            )
        }
        .alert(isPresented: $showAlert) {
            Alert(title: Text("连接提示"), message: Text(alertMessage ?? ""), dismissButton: .default(Text("好的")))
        }
        .onAppear {
            discoveryService.startListening()
        }
        .onDisappear {
            discoveryService.stopListening()
        }
    }

    private func startConnectWorkflow(ip: String) {
        targetIP = ip

        // 1. 验证已有 Token 是否有效
        let checkUrlStr = "http://\(ip):8080/pair-check?token=\(config.token.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? "")"
        guard let checkUrl = URL(string: checkUrlStr) else { return }

        URLSession.shared.dataTask(with: checkUrl) { _, response, error in
            DispatchQueue.main.async {
                if let http = response as? HTTPURLResponse {
                    if http.statusCode == 200 {
                        // Token 仍然有效，直接开播
                        self.launchStream(to: ip)
                    } else if http.statusCode == 403 {
                        // 需要重新配对，拉起 4 位码弹窗
                        self.showPairSheet = true
                    } else {
                        self.alertMessage = "无法连接 Mac 端服务 (HTTP \(http.statusCode))"
                        self.showAlert = true
                    }
                } else {
                    self.alertMessage = "连接失败，请确认 Mac 正在运行发送端且在同一局域网下: \(error?.localizedDescription ?? "超时")"
                    self.showAlert = true
                }
            }
        }.resume()
    }

    private func launchStream(to ip: String) {
        config.lastConnectedIP = ip
        config.save()

        // 取得 iPad 真实物理分辨率
        let bounds = UIScreen.main.nativeBounds
        let pw = Int(max(bounds.width, bounds.height))
        let ph = Int(min(bounds.width, bounds.height))

        streamClient.startStream(
            macIP: ip,
            token: config.token,
            physicalWidth: pw,
            physicalHeight: ph,
            scalePercent: config.scalePercent,
            preferHEVC: config.preferHEVC
        )

        controlClient.connect(to: ip)

        withAnimation {
            self.isStreaming = true
        }
    }

    private func exitSession() {
        streamClient.stopStream()
        controlClient.disconnect()
        withAnimation {
            self.isStreaming = false
        }
    }
}
