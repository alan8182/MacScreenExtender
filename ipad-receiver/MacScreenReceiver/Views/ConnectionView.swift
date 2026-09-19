import SwiftUI

public struct ConnectionView: View {
    @ObservedObject var discoveryService: DiscoveryService
    @Binding var config: ReceiverConfig
    let onConnect: (String) -> Void

    @State private var manualIP: String = ""
    @State private var showManualInput = false

    public init(discoveryService: DiscoveryService, config: Binding<ReceiverConfig>, onConnect: @escaping (String) -> Void) {
        self.discoveryService = discoveryService
        self._config = config
        self.onConnect = onConnect
    }

    public var body: some View {
        ScrollView {
            VStack(spacing: 28) {
                // Header
                VStack(spacing: 8) {
                    HStack(spacing: 12) {
                        Image(systemName: "ipad.landscape")
                            .font(.system(size: 32))
                            .foregroundColor(.blue)
                        Text("Mac 随航无线扩展屏")
                            .font(.system(size: 28, weight: .bold))
                    }

                    HStack(spacing: 6) {
                        Circle()
                            .fill(discoveryService.discoveredMacs.isEmpty ? Color.orange : Color.green)
                            .frame(width: 8, height: 8)
                        Text(discoveryService.discoveredMacs.isEmpty ? "正在搜寻局域网中的 Mac..." : "已发现可连接设备")
                            .font(.subheadline)
                            .foregroundColor(.secondary)
                    }
                }
                .padding(.top, 40)

                // 核心卡片容器
                VStack(spacing: 20) {
                    // 设备列表区
                    VStack(alignment: .leading, spacing: 14) {
                        Text("可用 Mac 设备")
                            .font(.headline)
                            .foregroundColor(.secondary)

                        if discoveryService.discoveredMacs.isEmpty {
                            HStack {
                                Spacer()
                                VStack(spacing: 10) {
                                    ProgressView()
                                    Text("请确保 Mac 端已启动 MacScreenSender\n且处于同一 Wi-Fi 局域网下")
                                        .font(.footnote)
                                        .foregroundColor(.secondary)
                                        .multilineTextAlignment(.center)
                                }
                                .padding(.vertical, 24)
                                Spacer()
                            }
                            .background(Color(uiColor: .secondarySystemGroupedBackground))
                            .cornerRadius(14)
                        } else {
                            ForEach(discoveryService.discoveredMacs) { mac in
                                HStack(spacing: 16) {
                                    Image(systemName: "laptopcomputer")
                                        .font(.system(size: 28))
                                        .foregroundColor(.blue)
                                        .frame(width: 48, height: 48)
                                        .background(Color.blue.opacity(0.1))
                                        .cornerRadius(10)

                                    VStack(alignment: .leading, spacing: 4) {
                                        Text(mac.name)
                                            .font(.headline)
                                        Text("\(mac.ip) · 局域网高速直连")
                                            .font(.subheadline)
                                            .foregroundColor(.secondary)
                                    }

                                    Spacer()

                                    Button(action: { onConnect(mac.ip) }) {
                                        Text("连接")
                                            .font(.system(size: 15, weight: .semibold))
                                            .foregroundColor(.white)
                                            .padding(.horizontal, 20)
                                            .padding(.vertical, 8)
                                            .background(Color.blue)
                                            .cornerRadius(8)
                                    }
                                }
                                .padding(16)
                                .background(Color(uiColor: .secondarySystemGroupedBackground))
                                .cornerRadius(14)
                            }
                        }
                    }

                    // 画质与显示偏好
                    VStack(alignment: .leading, spacing: 14) {
                        Text("画质与显示偏好")
                            .font(.headline)
                            .foregroundColor(.secondary)

                        VStack(spacing: 16) {
                            HStack {
                                Text("显示清晰度")
                                    .font(.subheadline)
                                Spacer()
                                Picker("缩放", selection: $config.scalePercent) {
                                    Text("超清视网膜 (2x)").tag(200)
                                    Text("点对点直出 (1x)").tag(100)
                                }
                                .pickerStyle(.segmented)
                                .frame(width: 240)
                            }

                            Divider()

                            HStack {
                                Text("目标刷新率")
                                    .font(.subheadline)
                                Spacer()
                                Picker("帧率", selection: $config.targetFPS) {
                                    Text("60 FPS").tag(60)
                                    Text("120 FPS (ProMotion)").tag(120)
                                }
                                .pickerStyle(.segmented)
                                .frame(width: 240)
                            }

                            Divider()

                            Toggle("优先使用 HEVC (H.265) 硬件压缩", isOn: $config.preferHEVC)
                                .font(.subheadline)
                        }
                        .padding(16)
                        .background(Color(uiColor: .secondarySystemGroupedBackground))
                        .cornerRadius(14)
                    }

                    // 手动输入 IP 折叠区
                    VStack(alignment: .leading, spacing: 10) {
                        Button(action: { withAnimation { showManualInput.toggle() } }) {
                            HStack {
                                Image(systemName: showManualInput ? "chevron.down" : "chevron.right")
                                Text("手动指定 Mac IP 地址 (跨网段 / VPN 场景)")
                                    .font(.subheadline)
                                Spacer()
                            }
                            .foregroundColor(.blue)
                        }

                        if showManualInput {
                            HStack(spacing: 12) {
                                TextField("例如 192.168.1.100", text: $manualIP)
                                    .font(.system(size: 16))
                                    .padding(.horizontal, 12)
                                    .frame(height: 44)
                                    .background(Color(uiColor: .tertiarySystemGroupedBackground))
                                    .cornerRadius(8)

                                Button("直连") {
                                    if !manualIP.isEmpty {
                                        onConnect(manualIP.trimmingCharacters(in: .whitespacesAndNewlines))
                                    }
                                }
                                .buttonStyle(.borderedProminent)
                                .disabled(manualIP.isEmpty)
                            }
                            .transition(.opacity)
                        }
                    }
                    .padding(16)
                    .background(Color(uiColor: .secondarySystemGroupedBackground))
                    .cornerRadius(14)
                }
                .frame(maxWidth: 620)
                .padding(.horizontal, 20)
            }
        }
        .background(Color(uiColor: .systemGroupedBackground).ignoresSafeArea())
    }
}
