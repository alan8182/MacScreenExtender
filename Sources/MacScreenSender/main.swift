// MacScreenSender：Mac → Windows 局域网扩展屏（发送端）
// 菜单栏 App：虚拟显示器 → ScreenCaptureKit 采集 → VideoToolbox H.264/HEVC → 裸流 HTTP
import Foundation
import CoreGraphics
import Network
import AppKit
import ServiceManagement
import VirtualDisplayLib

/// 双写日志（stdout + 文件），远程排障用：%APPDATA% 对应的 ~/Library/Application Support/MacScreenExtender/sender.log
enum Log {
    static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("MacScreenExtender/sender.log")
    private static let lock = NSLock()

    static func write(_ msg: String) {
        print(msg)
        lock.lock()
        defer { lock.unlock() }
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            var data = (try? Data(contentsOf: url)) ?? Data()
            if data.count > 512 * 1024 { data = Data() }
            data.append(Data((msg + "\n").utf8))
            try data.write(to: url)
        } catch { }
    }
}

enum AppError: Error {
    case capture(String)
    case display(String)
}

final class App: @unchecked Sendable {
    let port: UInt16
    let defaultWidth: Int
    let defaultHeight: Int
    let fps: Int
    let bitrateBps: Int
    let hevc: Bool
    let headless: Bool
    private(set) var paused = false
    private var adaptiveBitrate: Int          // AIMD 当前目标码率（≤ bitrateBps）
    private var congestionTimer: DispatchSourceTimer?
    private var lastPrintedBitrate = 0
    private var menuCoordinator: MenuCoordinator?   // NSMenu.delegate 是弱引用，必须强持有

    // MARK: 配对与发现
    private(set) var pairingCode = ""
    private var pairedIPs: Set<String> = []
    private var pairedTokens: Set<String> = []
    private var discoveryTimer: DispatchSourceTimer?
    private var pairStoreURL = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("MacScreenExtender/paired.json")

    private struct PairStore: Codable {
        var code: String? = nil   // 旧版文件无此字段：解码失败才生成新码，避免清空已有配对
        var ips: [String] = []
        var tokens: [String] = []
    }

    /// 配对码持久化：生成一次终身不变（变化的码只会让两端配对状态混乱）
    private func loadPairStore() {
        var store = (try? JSONDecoder().decode(PairStore.self, from: Data(contentsOf: pairStoreURL) ?? Data())) ?? nil
        if store == nil { store = PairStore() }
        if store!.code == nil || store!.code!.count != 4 {
            store!.code = String(format: "%04d", Int.random(in: 1000...9999))
        }
        pairingCode = store!.code!
        lock.lock()
        pairedIPs = Set(store!.ips)
        pairedTokens = Set(store!.tokens)
        lock.unlock()
        if let data = try? JSONEncoder().encode(store!) { try? data.write(to: pairStoreURL) }
        Log.write("🔑 配对码: \(pairingCode)（已配对设备 \(store!.ips.count) 台）")
    }

    private func savePairStore() {
        lock.lock()
        let store = PairStore(code: pairingCode, ips: Array(pairedIPs), tokens: Array(pairedTokens))
        lock.unlock()
        do {
            try FileManager.default.createDirectory(at: pairStoreURL.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try JSONEncoder().encode(store).write(to: pairStoreURL)
        } catch { print("配对信息保存失败: \(error)") }
    }

    /// 清空已配对设备（码不变）；Windows 端旧 token 全部失效，重新配对即可
    func resetPairing() {
        lock.lock()
        pairedIPs.removeAll(); pairedTokens.removeAll()
        lock.unlock()
        savePairStore()
        Log.write("🧹 配对已重置")
    }

    // MARK: - 屏幕排列记忆
    private var layoutStoreURL = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("MacScreenExtender/layout.json")

    struct LayoutStore: Codable, Equatable {
        var relativeX: Double
        var relativeY: Double
        var displayW: Int?
        var displayH: Int?
        var scalePercent: Int?
    }

    private var savedLayout: LayoutStore?
    private(set) var userScalePercent: Int = 100
    private var isConfiguringDisplay = false
    private var lastConfiguredOrigin: CGPoint?

    private func loadLayoutStore() {
        guard let data = try? Data(contentsOf: layoutStoreURL),
              let store = try? JSONDecoder().decode(LayoutStore.self, from: data) else { return }
        lock.lock()
        savedLayout = store
        if let sp = store.scalePercent, [100, 125, 150, 175, 200].contains(sp) {
            userScalePercent = sp
        }
        lock.unlock()
        Log.write(String(format: "📐 加载屏幕记忆: 相对主屏 (%.0f, %.0f), 缩放 %d%%", store.relativeX, store.relativeY, userScalePercent))
    }

    private func saveLayoutStore(_ store: LayoutStore) {
        lock.lock()
        savedLayout = store
        lock.unlock()
        do {
            try FileManager.default.createDirectory(at: layoutStoreURL.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            try JSONEncoder().encode(store).write(to: layoutStoreURL)
        } catch { print("屏幕记忆保存失败: \(error)") }
    }

    /// 切换 UI 缩放比例（100%、125%、150%、175%、200%），以超采样高清下采样直出
    func setScalePercent(_ percent: Int) {
        lock.lock()
        guard userScalePercent != percent else { lock.unlock(); return }
        userScalePercent = percent
        if var store = savedLayout {
            store.scalePercent = percent
            savedLayout = store
            lock.unlock()
            saveLayoutStore(store)
        } else {
            let store = LayoutStore(relativeX: 0, relativeY: 0, displayW: displayW, displayH: displayH, scalePercent: percent)
            savedLayout = store
            lock.unlock()
            saveLayoutStore(store)
        }
        Log.write("🔍 切换显示缩放: \(percent)%")
        lock.lock()
        let pw = displayW
        let ph = displayH
        let hiDPI = displayHiDPI
        lock.unlock()
        if pw > 0 && ph > 0 {
            ensureConfig(w: pw, h: ph, hiDPI: hiDPI, scale: percent)
        }
    }

    /// 重置屏幕排列为默认（清空记忆并复位至右侧居中）
    func resetLayout() {
        lock.lock()
        savedLayout = nil
        lastConfiguredOrigin = nil
        lock.unlock()
        try? FileManager.default.removeItem(at: layoutStoreURL)
        Log.write("🧹 屏幕排列记忆已清除")
        lock.lock()
        let id = displayID
        lock.unlock()
        if id != 0 {
            let mainID = getAnchorDisplayID(virtualID: id)
            let mb = CGDisplayBounds(mainID)
            let db = CGDisplayBounds(id)
            let origin = Self.calculateOrigin(mainBounds: mb, virtualSize: db.size, saved: nil)
            var config: CGDisplayConfigRef?
            isConfiguringDisplay = true
            defer { isConfiguringDisplay = false }
            if CGBeginDisplayConfiguration(&config) == .success {
                CGConfigureDisplayOrigin(config, id, Int32(origin.x), Int32(origin.y))
                CGCompleteDisplayConfiguration(config, .permanently)
                lastConfiguredOrigin = origin
            }
        }
    }

    private func getAnchorDisplayID(virtualID: CGDirectDisplayID) -> CGDirectDisplayID {
        var mainID = CGMainDisplayID()
        if mainID == virtualID, let builtin = Self.CGGetActiveDisplayList_safe().first(where: { CGDisplayIsBuiltin($0) != 0 }) {
            mainID = builtin
        }
        return mainID
    }

    /// ponytail: 纯函数计算虚拟屏布局坐标。若记录了贴边特征则按边缘吸附自适应新分辨率，否则直取相对偏移；无记录回退右侧居中。
    static func calculateOrigin(mainBounds mb: CGRect,
                                virtualSize dbSize: CGSize,
                                saved: LayoutStore?) -> CGPoint {
        guard let saved else {
            return CGPoint(x: mb.maxX, y: mb.midY - dbSize.height / 2)
        }

        var x = mb.origin.x + saved.relativeX
        var y = mb.origin.y + saved.relativeY

        // 若记录了旧尺寸，检查是否紧密贴靠在主屏边缘（容差 10 像素内），自适应新尺寸贴边避免产生空隙或重叠
        if let oldW = saved.displayW, let oldH = saved.displayH {
            let leftEdgeDist = abs((saved.relativeX + Double(oldW)) - 0)
            let rightEdgeDist = abs(saved.relativeX - mb.width)
            let topEdgeDist = abs((saved.relativeY + Double(oldH)) - 0)
            let bottomEdgeDist = abs(saved.relativeY - mb.height)

            if leftEdgeDist <= 10 {
                x = mb.minX - dbSize.width
            } else if rightEdgeDist <= 10 {
                x = mb.maxX
            }

            if topEdgeDist <= 10 {
                y = mb.minY - dbSize.height
            } else if bottomEdgeDist <= 10 {
                y = mb.maxY
            }
        }

        // 防呆保护：如果计算出的坐标异常偏离（如插拔屏幕导致坐标漂移超过 20000 像素），安全回退默认位置
        if abs(x - mb.origin.x) > 20000 || abs(y - mb.origin.y) > 20000 {
            return CGPoint(x: mb.maxX, y: mb.midY - dbSize.height / 2)
        }

        return CGPoint(x: x, y: y)
    }

    private func saveCurrentLayout() {
        guard !isConfiguringDisplay else { return }
        lock.lock()
        let id = displayID
        let lastOrigin = lastConfiguredOrigin
        lock.unlock()
        guard id != 0 else { return }

        // 检查虚拟屏是否仍然活跃在系统列表中
        guard Self.CGGetActiveDisplayList_safe().contains(id) else { return }
        let mainID = getAnchorDisplayID(virtualID: id)
        guard mainID != id else { return }

        let mb = CGDisplayBounds(mainID)
        let db = CGDisplayBounds(id)
        guard db.width > 0 && db.height > 0 && mb.width > 0 && mb.height > 0 else { return }

        // 避免刚配置生效时的回执通知重复处理
        if let last = lastOrigin, abs(db.origin.x - last.x) < 1 && abs(db.origin.y - last.y) < 1 {
            return
        }

        let logW = Int(db.width)
        let logH = Int(db.height)

        let relX = db.origin.x - mb.origin.x
        let relY = db.origin.y - mb.origin.y

        lock.lock()
        let old = savedLayout
        lock.unlock()

        if old == nil || abs(old!.relativeX - relX) >= 1 || abs(old!.relativeY - relY) >= 1 || old!.displayW != logW || old!.displayH != logH || old!.scalePercent != userScalePercent {
            let newStore = LayoutStore(relativeX: relX, relativeY: relY, displayW: logW, displayH: logH, scalePercent: userScalePercent)
            saveLayoutStore(newStore)
            lastConfiguredOrigin = db.origin
            Log.write(String(format: "📐 已记住显示器排列: 相对主屏 (%.0f, %.0f) [逻辑 %dx%d, 缩放 %d%%]", relX, relY, logW, logH, userScalePercent))
        }
    }

    /// ponytail: 启动自检，确保布局计算逻辑符合预期
    static func selfCheckLayout() {
        let mb = CGRect(x: 0, y: 0, width: 1920, height: 1080)
        let vs = CGSize(width: 1920, height: 1080)

        // 1. 无记录回退
        let def = calculateOrigin(mainBounds: mb, virtualSize: vs, saved: nil)
        assert(def.x == 1920 && def.y == 0, "Default layout calculation failed")

        // 2. 左侧吸附
        let leftSaved = LayoutStore(relativeX: -1920, relativeY: 0, displayW: 1920, displayH: 1080)
        let leftRes = calculateOrigin(mainBounds: mb, virtualSize: CGSize(width: 2560, height: 1440), saved: leftSaved)
        assert(leftRes.x == -2560, "Left docking adaptation failed")

        // 3. 右侧吸附
        let rightSaved = LayoutStore(relativeX: 1920, relativeY: 0, displayW: 1920, displayH: 1080)
        let rightRes = calculateOrigin(mainBounds: mb, virtualSize: CGSize(width: 2560, height: 1440), saved: rightSaved)
        assert(rightRes.x == 1920, "Right docking adaptation failed")

        // 4. 自定义相对偏移
        let customSaved = LayoutStore(relativeX: -500, relativeY: 200, displayW: nil, displayH: nil)
        let customRes = calculateOrigin(mainBounds: mb, virtualSize: vs, saved: customSaved)
        assert(customRes.x == -500 && customRes.y == 200, "Custom relative layout failed")
    }

    /// ponytail: 纯函数计算显示器渲染参数（逻辑排版尺寸、视网膜像素尺寸与开启标志）
    static func calculateDisplayDimensions(physicalW: Int, physicalH: Int, scalePercent: Int)
        -> (logicalW: Int, logicalH: Int, backingW: Int, backingH: Int, useHiDPI: Bool, ppi: Int) {
        let safeScale = max(100, min(300, scalePercent))
        if safeScale == 100 {
            return (physicalW, physicalH, physicalW, physicalH, false, 102)
        }
        let scale = Double(safeScale) / 100.0
        var logW = Int(round(Double(physicalW) / scale))
        var logH = Int(round(Double(physicalH) / scale))
        if (logW & 1) != 0 { logW -= 1 }
        if (logH & 1) != 0 { logH -= 1 }
        logW = max(640, logW)
        logH = max(480, logH)

        // 视网膜 2x 超采样渲染尺寸 (pixel backing store)
        let backW = logW * 2
        let backH = logH * 2
        return (logW, logH, backW, backH, true, 220)
    }

    /// ponytail: 自检显示缩放与超采样计算
    static func selfCheckScalingCalculation() {
        // 1080p 100%
        let s100 = calculateDisplayDimensions(physicalW: 1920, physicalH: 1080, scalePercent: 100)
        assert(s100.logicalW == 1920 && s100.logicalH == 1080 && !s100.useHiDPI, "100% scale failed")

        // 1080p 125%
        let s125 = calculateDisplayDimensions(physicalW: 1920, physicalH: 1080, scalePercent: 125)
        assert(s125.logicalW == 1536 && s125.logicalH == 864 && s125.backingW == 3072 && s125.useHiDPI && s125.ppi == 220, "125% scale failed")

        // 1080p 150%
        let s150 = calculateDisplayDimensions(physicalW: 1920, physicalH: 1080, scalePercent: 150)
        assert(s150.logicalW == 1280 && s150.logicalH == 720 && s150.backingW == 2560 && s150.useHiDPI && s150.ppi == 220, "150% scale failed")

        // 2K 1440p 150%
        let s2k = calculateDisplayDimensions(physicalW: 2560, physicalH: 1440, scalePercent: 150)
        assert(s2k.logicalW == 1706 && s2k.logicalH == 960 && s2k.backingW == 3412 && s2k.useHiDPI, "2K 150% scale failed")

        // 2.5K 1600p 150% (用户实机)
        let s1600 = calculateDisplayDimensions(physicalW: 2560, physicalH: 1600, scalePercent: 150)
        assert(s1600.logicalW == 1706 && s1600.logicalH == 1066 && s1600.backingW == 3412 && s1600.useHiDPI, "1600p 150% scale failed")
    }

    /// 首次配对：凭 4 位码换长期 token
    private func pairWith(code: String, ip: String) -> String? {
        guard code == pairingCode else { return nil }
        let token = UUID().uuidString
        lock.lock()
        pairedIPs.insert(ip)
        pairedTokens.insert(token)
        lock.unlock()
        savePairStore()
        return token
    }

    /// 鉴权：token 或已配对 IP；token 命中顺便记住其新 IP（DHCP 换址后控制通道仍可用）
    private func authorize(ip: String, token: String?) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if let t = token, pairedTokens.contains(t) {
            pairedIPs.insert(ip)
            return true
        }
        return pairedIPs.contains(ip)
    }

    /// 局域网 UDP 广播发现包（Windows 端免输 IP）
    private func startDiscovery() {
        let t = DispatchSource.makeTimerSource(queue: DispatchQueue.global())
        t.schedule(deadline: .now(), repeating: 2)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            let name = (Host.current().localizedName ?? "Mac")
                .replacingOccurrences(of: "\\", with: "")
                .replacingOccurrences(of: "\"", with: "")
            let msg = "{\"name\":\"\(name)\",\"ip\":\"\(Self.localIPs().first ?? "")\"," +
                      "\"port\":\(self.port),\"proto\":1}"
            Self.broadcastUDP(msg, port: 8082)
            // 子网定向广播：比 255.255.255.255 更可靠（本机自测也能收到）
            if let ip = Self.localIPs().first, let dot3 = ip.lastIndex(of: ".") {
                Self.broadcastUDP(msg, port: 8082, to: String(ip[..<dot3]) + ".255")
            }
        }
        t.resume()
        discoveryTimer = t
    }

    static func broadcastUDP(_ msg: String, port: UInt16, to host: String = "255.255.255.255") {
        let fd = socket(AF_INET, SOCK_DGRAM, 0)
        guard fd >= 0 else { return }
        defer { close(fd) }
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &one, socklen_t(MemoryLayout<Int32>.size))
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = port.bigEndian
        if inet_pton(AF_INET, host, &addr.sin_addr) != 1 { return }
        let payload = Array(msg.utf8)
        _ = payload.withUnsafeBytes { ptr in
            withUnsafePointer(to: &addr) { ap in
                sendto(fd, ptr.baseAddress, ptr.count, 0,
                       ap.withMemoryRebound(to: sockaddr.self, capacity: 1) { $0 },
                       socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
    }

    static func hostOf(_ conn: NWConnection) -> String {
        if case let .hostPort(host, _) = conn.endpoint { return "\(host)" }
        return "\(conn.endpoint)"
    }

    private let lock = NSLock()
    private let configQueue = DispatchQueue(label: "config.serial")
    private var displayID: CGDirectDisplayID = 0
    private var displayW = 0
    private var displayH = 0
    private var displayHiDPI = false
    private var displayScale = 100
    private var capture: CaptureService?
    private var encoder: VideoEncoder?
    private lazy var muxer = TSMuxer(streamType: hevc ? 0x24 : 0x1B)
    private let broadcaster = Broadcaster()
    private let rawBroadcaster = Broadcaster()   // 裸 Annex-B（/raw，h264/hevc 均可，ffmpeg 系解码）
    private lazy var injector: InputInjector = {
        let i = InputInjector()
        i.screenSize = { [weak self] in
            guard let self else { return (1920, 1080) }
            self.lock.lock(); let id = self.displayID; self.lock.unlock()
            guard id != 0 else { return (1920, 1080) }
            let b = CGDisplayBounds(id)
            return (max(1, Int(b.width)), max(1, Int(b.height)))
        }
        i.displayOrigin = { [weak self] in
            guard let self else { return .zero }
            self.lock.lock(); let id = self.displayID; self.lock.unlock()
            guard id != 0 else { return .zero }
            return CGDisplayBounds(id).origin   // 全局坐标，虚拟屏通常在主屏右侧
        }
        return i
    }()
    private lazy var controlServer = ControlServer(injector: injector)

    init(port: UInt16, width: Int, height: Int, fps: Int, bitrateMbps: Int, hevc: Bool, headless: Bool = false) {
        self.port = port
        self.defaultWidth = width
        self.defaultHeight = height
        self.fps = fps
        self.bitrateBps = bitrateMbps * 1_000_000
        self.adaptiveBitrate = bitrateMbps * 1_000_000
        self.hevc = hevc
        self.headless = headless
    }

    // MARK: - 启动

    func run() {
        print("MacScreenSender 启动 v3-keepalive")
        print("  默认虚拟屏: \(defaultWidth)x\(defaultHeight)@\(fps)fps, 码率 \(bitrateBps / 1_000_000)Mbps")
        print("  本机地址:")
        for ip in Self.localIPs() {
            print("    http://\(ip):\(port)/stream")
        }

        let server = HTTPServer()
        server.onRequest = { [weak self] conn, path, params in
            self?.handleRequest(conn: conn, path: path, params: params)
        }
        do {
            try server.start(port: port)
            try controlServer.start(port: port &+ 1)   // 控制通道
        } catch {
            Log.write("❌ 服务启动失败: \(error)")
            exit(1)
        }
        startCongestionControl()

        if !InputInjector.hasAccessibility() {
            print("⚠️ 未授予辅助功能权限：Windows 端的键鼠无法控制 Mac")
            print("   系统设置 → 隐私与安全性 → 辅助功能 → 勾选本程序所在宿主（终端/App）")
        }

        // 控制通道光标回报：归一化坐标 + 是否在虚拟屏内
        controlServer.cursorProvider = { [weak self] in
            guard let self else { return (0.5, 0.5, false) }
            self.lock.lock(); let id = self.displayID; self.lock.unlock()
            guard id != 0 else { return (0, 0, false) }
            let b = CGDisplayBounds(id)
            guard b != .zero, let loc = CGEvent(source: nil)?.location, b.contains(loc) else {
                return (0, 0, false)
            }
            return (Float((loc.x - b.minX) / b.width), Float((loc.y - b.minY) / b.height), true)
        }

        loadPairStore()
        loadLayoutStore()
        Self.selfCheckLayout()
        Self.selfCheckScalingCalculation()
        startDiscovery()
        controlServer.isAuthorized = { [weak self] ip in
            self?.authorize(ip: ip, token: nil) ?? false
        }
        controlServer.onRequestKeyframe = { [weak self] in
            self?.encoder?.requestKeyframe()
        }

        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            self?.saveCurrentLayout()
        }

        ensureConfig(w: defaultWidth, h: defaultHeight)
        installSignalHandler()
        print("🔑 配对码: \(pairingCode)")
        setupMenuBar()
    }

    // MARK: - 菜单栏

    var clientCount: Int {
        broadcaster.count + rawBroadcaster.count
    }

    var currentResolution: (w: Int, h: Int) {
        lock.lock(); defer { lock.unlock() }
        return (displayW, displayH)
    }

    var currentBitrateMbps: Int {
        adaptiveBitrate / 1_000_000
    }

    func statusLine() -> String {
        let (w, h) = currentResolution
        let clients = clientCount
        let scaleText = userScalePercent > 100 ? " [缩放 \(userScalePercent)%]" : ""
        let state = paused ? "⏸ 已暂停"
            : (w == 0 ? "启动中…"
            : String(format: "虚拟屏 %d×%d%@@%dfps", w, h, scaleText, fps))
        return String(format: "%@ · 客户端 %d · 码率 %dMbps · %@",
                      state, clients, currentBitrateMbps, hevc ? "HEVC" : "H.264")
    }

    private func setupMenuBar() {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)   // 无 Dock 图标，纯菜单栏
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            if #available(macOS 11.0, *), let img = NSImage(systemSymbolName: "display.2", accessibilityDescription: "MacScreenSender") {
                img.isTemplate = true
                button.image = img
            } else {
                button.title = "🖥"
            }
        }
        let menu = NSMenu()
        menu.autoenablesItems = false
        let coordinator = MenuCoordinator(app: self)
        menuCoordinator = coordinator   // 强持有：否则委托被释放，菜单永远是空的
        menu.delegate = coordinator
        coordinator.rebuild(menu)       // 预填一次，委托时序问题也不至于空菜单
        item.menu = menu
        if headless {
            // SSH/脚本场景：无菜单栏，保持旧行为
            dispatchMain()
        }
        app.run()
    }

    func togglePause() {
        paused.toggle()
        let pausing = paused
        lock.lock(); let id = displayID; let w = displayW; let h = displayH; lock.unlock()
        if pausing {
            configQueue.async { [weak self] in
                guard let self else { return }
                self.lock.lock()
                let cap = self.capture, enc = self.encoder
                self.capture = nil; self.encoder = nil
                self.lock.unlock()
                cap?.stop(); enc?.stop()
                destroyVirtualDisplay(id)
                self.lock.lock(); self.displayID = 0; self.lock.unlock()
                print("⏸ 投屏已暂停，虚拟屏已回收")
            }
        } else {
            ensureConfig(w: max(w, defaultWidth), h: max(h, defaultHeight))
            print("▶️ 投屏恢复")
        }
    }

    func toggleAutostart() {
        if #available(macOS 13.0, *) {
            do {
                switch SMAppService.mainApp.status {
                case .enabled: try SMAppService.mainApp.unregister()
                default: try SMAppService.mainApp.register()
                }
            } catch {
                print("开机自启切换失败: \(error)")
            }
        }
    }

    func autostartEnabled() -> Bool {
        guard #available(macOS 13.0, *) else { return false }
        return SMAppService.mainApp.status == .enabled
    }

    func shutdownAndExit() {
        print("退出，清理虚拟屏…")
        lock.lock()
        let cap = capture, enc = encoder
        capture = nil; encoder = nil
        lock.unlock()
        cap?.stop(); enc?.stop()
        lock.lock(); let id = displayID; displayID = 0; lock.unlock()
        destroyVirtualDisplay(id)
        exit(0)
    }

    /// 码率自适应（AIMD）：在途字节积压 → 快速降码率；链路空闲 → 缓慢回升至上限。
    /// 250ms 一调，拖动/动画的网络抖动能在半秒内被压住
    private func startCongestionControl() {
        let t = DispatchSource.makeTimerSource(queue: configQueue)
        t.schedule(deadline: .now() + 2, repeating: 0.25)
        t.setEventHandler { [weak self] in
            guard let self, let enc = self.encoder else { return }
            let inflight = max(self.rawBroadcaster.maxInflight, self.broadcaster.maxInflight)
            let rttBad = self.controlServer.rttElevated   // 缓冲膨胀：积压未起、延迟先涨
            let old = self.adaptiveBitrate
            var next = old
            if inflight > 1_500_000 || rttBad {
                next = max(10_000_000, old * 8 / 10)                        // 拥塞：-20%
            } else if inflight < 200_000 && !rttBad {
                next = min(self.bitrateBps, old + max(500_000, old / 20))   // 空闲：+5%
            }
            if next != old {
                self.adaptiveBitrate = next
                enc.updateBitrate(next)
                if abs(next - lastPrintedBitrate) > 3_000_000 || next == bitrateBps || next == 10_000_000 {
                    lastPrintedBitrate = next
                    print("📶 码率自适应 →\(next / 1_000_000)Mbps（在途 \(inflight / 1024)KB）")
                }
            }
        }
        t.resume()
        congestionTimer = t
    }

    // MARK: - 请求处理

    private func handleRequest(conn: NWConnection, path: String, params: [String: String]) {
        if path == "/" || path == "/status" {
            let lockW = { () -> Int in lock.lock(); defer { lock.unlock() }; return displayW }()
            let lockH = { () -> Int in lock.lock(); defer { lock.unlock() }; return displayH }()
            HTTPServer.sendText(conn, """
            MacScreenSender 运行中
            虚拟屏: \(lockW)x\(lockH)@\(fps)fps  码率: \(bitrateBps / 1_000_000)Mbps
            客户端数: \(broadcaster.count)
            流地址: /stream?w=宽&h=高（可选参数，Windows 端应传自己屏幕的分辨率）
            """)
            return
        }
        let peer = Self.hostOf(conn)
        let isRaw = (path == "/raw")   // 裸 Annex-B：ffmpeg 系解码器直接吃，绕过 TS/VLC 整个兼容性泥潭

        // 配对引导（免鉴权）：凭菜单栏 4 位码换长期 token
        if path == "/pair" {
            if let token = pairWith(code: params["code"] ?? "", ip: peer) {
                Log.write("🔑 设备 \(peer) 配对成功")
                HTTPServer.sendText(conn, token)
            } else {
                Log.write("🚫 设备 \(peer) 配对码错误")
                HTTPServer.sendText(conn, "ERR 配对码不正确", status: 403)
            }
            return
        }

        guard path == "/stream" || isRaw || path == "/pair-check" else {
            HTTPServer.sendText(conn, "404\n")
            return
        }

        // 鉴权闸门：token 或已配对 IP
        guard authorize(ip: peer, token: params["token"]) else {
            Log.write("🚫 未配对设备 \(peer) 请求 \(path) 已拒绝")
            HTTPServer.sendText(conn, "NEED-PAIR", status: 403)
            return
        }
        if path == "/pair-check" {
            HTTPServer.sendText(conn, "OK")
            return
        }

        // 分辨率与缩放参数：Windows 客户端传物理尺寸与缩放需求
        var w = defaultWidth, h = defaultHeight
        if let v = params["pw"] ?? params["outw"] ?? params["w"], let x = Int(v), x >= 640, x <= 7680 { w = x }
        if let v = params["ph"] ?? params["outh"] ?? params["h"], let x = Int(v), x >= 480, x <= 4320 { h = x }
        let hiDPI = (params["hidpi"] == "1" || params["retina"] == "1")

        var reqScale: Int? = nil
        if let v = params["scale"], let s = Int(v), s >= 100 && s <= 300 {
            reqScale = s
            self.lock.lock()
            self.userScalePercent = s
            self.lock.unlock()
        }

        // 先完成（可能的）重建，再发响应头/入组/关键帧。否则客户端会先收到旧分辨率的流，
        // 再遭遇 mid-stream SPS 变更——部分解码器会冻结在旧帧上
        configQueue.async { [weak self] in
            guard let self else { return }
            self.ensureConfigSync(w: w, h: h, hiDPI: hiDPI, scale: reqScale)
            if isRaw {
                HTTPServer.sendStreamHead(conn, contentType: self.hevc ? "video/hevc" : "video/h264")
                self.rawBroadcaster.add(conn)
            } else {
                HTTPServer.sendStreamHead(conn)
                self.broadcaster.add(conn)
                self.broadcaster.send(to: conn, self.muxer.systemTables())
            }
            self.encoder?.requestKeyframe()   // 新客户端立即出关键帧
            let scaleInfo = self.userScalePercent > 100 ? " [缩放 \(self.userScalePercent)%]" : ""
            print("客户端接入: \(conn.endpoint) → \(w)x\(h)\(scaleInfo)\(hiDPI ? " [HiDPI]" : "")\(isRaw ? " [raw]" : "")（当前 \(self.broadcaster.count+self.rawBroadcaster.count) 个）")
        }
    }

    // MARK: - 配置（虚拟屏 + 采集 + 编码）

    private func ensureConfig(w: Int, h: Int, hiDPI: Bool = false, scale: Int? = nil) {
        // 全部配置操作串行化，避免并发重建交错（旧屏销毁/新屏采集竞态）
        configQueue.async { [weak self] in self?.ensureConfigSync(w: w, h: h, hiDPI: hiDPI, scale: scale) }
    }

    private func ensureConfigSync(w: Int, h: Int, hiDPI: Bool = false, scale: Int? = nil) {
        guard !paused else { return }   // 用户已暂停投屏：不重建虚拟屏
        let effScale = scale ?? userScalePercent
        lock.lock()
        if w == displayW && h == displayH && hiDPI == displayHiDPI && effScale == displayScale && displayID != 0 {
            lock.unlock()
            return
        }
        let needRecreate = displayID != 0
        let oldDisplayID = displayID
        let oldCapture = capture
        let oldEncoder = encoder
        capture = nil
        encoder = nil
        lock.unlock()

        // 替换时先完全拆掉旧屏（同 serial 的两块屏无法共存），再创建新屏
        if needRecreate {
            oldCapture?.stop()
            oldEncoder?.stop()
            destroyVirtualDisplay(oldDisplayID)
            print("已销毁旧虚拟屏 \(displayW)x\(displayH)")
        }

        // 物理流输出尺寸（保证 Windows 接收端 1:1 点对点呈现，杜绝位图拉伸模糊）
        let streamW = w
        let streamH = h

        // 视网膜超采样与逻辑排版尺寸
        let dims = Self.calculateDisplayDimensions(physicalW: streamW, physicalH: streamH, scalePercent: effScale)
        let useHiDPI = dims.useHiDPI || hiDPI
        let ppi = dims.ppi

        print("创建虚拟屏 逻辑\(dims.logicalW)x\(dims.logicalH), 像素\(dims.backingW)x\(dims.backingH) (缩放 \(effScale)%, 输出流 \(streamW)x\(streamH), HiDPI=\(useHiDPI), PPI=\(ppi))...")
        let newID = createVirtualDisplay(Int32(dims.logicalW), Int32(dims.logicalH),
                                         Int32(dims.backingW), Int32(dims.backingH),
                                         Int32(ppi), useHiDPI,
                                         "MacScreenExtender",
                                         Self.stableSerial("windows-receiver-\(effScale)"), Int32(fps))
        guard newID != 0 else {
            Log.write("❌ 虚拟屏创建失败")
            return
        }
        lock.lock()
        displayID = newID
        displayW = streamW
        displayH = streamH
        displayHiDPI = hiDPI
        displayScale = effScale
        lock.unlock()

        // ponytail: 读取已保存的相对排列；无记录时回退默认（主屏正右侧垂直居中）
        let mainID = getAnchorDisplayID(virtualID: newID)
        let mb = CGDisplayBounds(mainID)
        let db = CGDisplayBounds(newID)
        lock.lock()
        let saved = savedLayout
        lock.unlock()
        let origin = Self.calculateOrigin(mainBounds: mb, virtualSize: db.size, saved: saved)

        isConfiguringDisplay = true
        var config: CGDisplayConfigRef?
        if CGBeginDisplayConfiguration(&config) == .success {
            CGConfigureDisplayOrigin(config, newID, Int32(origin.x), Int32(origin.y))
            CGCompleteDisplayConfiguration(config, .permanently)
            lastConfiguredOrigin = origin
        }
        isConfiguringDisplay = false

        // WindowServer 注册新屏有延迟，枚举到再继续
        guard Self.waitForDisplay(newID, timeout: 3.0) else {
            print("❌ 虚拟屏 \(newID) 未能在 3 秒内注册")
            return
        }

        // 采集管线（8s 超时）；失败清理半启动的流后，用全新实例整体重试一次
        var attempt = 0
        while attempt < 2 {
            attempt += 1
            let cap = CaptureService()
            let enc = VideoEncoder()
            do {
                try enc.start(width: streamW, height: streamH, fps: fps, bitrateBps: adaptiveBitrate,
                              codec: hevc ? .hevc : .h264)
            } catch {
                print("❌ 编码器启动失败: \(error)")
                return
            }
            cap.onFrame = { [weak enc] pb, pts in enc?.encode(pixelBuffer: pb, pts: pts) }
            cap.hasConsumers = { [weak self] in
                guard let self else { return false }
                return self.broadcaster.count + self.rawBroadcaster.count > 0
            }
            // 合成帧只做保活：有客户端 10fps，无客户端 5fps
            cap.tickRate = { [weak self] in
                guard let self else { return 0.2 }
                return self.controlServer.hasClients ? 0.1 : 0.2
            }
            cap.displayID = newID   // 光标叠加换算用（每帧实时读 bounds）
            enc.onFrame = { [weak self] annexB, keyframe, pts90 in
                guard let self else { return }
                self.rawBroadcaster.broadcast(annexB)   // 裸流通道（无客户端时 broadcast 内部直接返回）
                var ts = Data()
                if keyframe { ts += self.muxer.systemTables() }
                ts += self.muxer.videoPackets(annexB: annexB, pts90: pts90, keyframe: keyframe)
                self.broadcaster.broadcast(ts)
            }

            let sem = DispatchSemaphore(value: 0)
            var startError: Error?
            Task {
                do { try await cap.start(displayID: newID, width: streamW, height: streamH, fps: fps) }
                catch { startError = error }
                sem.signal()
            }
            if sem.wait(timeout: .now() + 8) == .timedOut {
                Log.write("❌ 采集启动超时(8s)\(attempt < 2 ? "，清理后重试" : "，重试仍失败")")
                cap.stop()
                continue
            }
            if let e = startError {
                print("❌ 采集启动失败: \(e)\(attempt < 2 ? "，清理后重试" : "，重试仍失败")")
                cap.stop()
                continue
            }
            lock.lock()
            capture = cap
            encoder = enc
            lock.unlock()
            Log.write("✅ 采集+编码已就绪 displayID=\(newID)（物理 \(streamW)x\(streamH)，逻辑 \(dims.logicalW)x\(dims.logicalH)，像素 \(dims.backingW)x\(dims.backingH)，缩放 \(effScale)%\(useHiDPI ? " [Retina 2x]" : "")）")
            return
        }
    }

    /// 轮询等待显示器出现在系统活动列表
    static func waitForDisplay(_ id: CGDirectDisplayID, timeout: Double) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            var ids = [CGDirectDisplayID](repeating: 0, count: 16)
            var n: UInt32 = 0
            CGGetActiveDisplayList(16, &ids, &n)
            if ids.prefix(Int(n)).contains(id) { return true }
            usleep(100_000)
        }
        return false
    }

    static func CGGetActiveDisplayList_safe() -> [CGDirectDisplayID] {
        var ids = [CGDirectDisplayID](repeating: 0, count: 16)
        var n: UInt32 = 0
        CGGetActiveDisplayList(16, &ids, &n)
        return Array(ids.prefix(Int(n)))
    }

    // MARK: - 工具

    /// 与接收端设备绑定的稳定序列号（FNV-1a），显示器排列位置可记忆
    static func stableSerial(_ key: String) -> UInt32 {
        var h: UInt32 = 2_166_136_261
        for b in key.utf8 { h ^= UInt32(b); h = h &* 1_677_7619 }
        return h == 0 ? 1 : h
    }

    static func localIPs() -> [String] {
        var result: [String] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return result }
        defer { freeifaddrs(ifaddr) }
        var ptr: UnsafeMutablePointer<ifaddrs>? = first
        while let p = ptr {
            let ifa = p.pointee
            if let sa = ifa.ifa_addr, (sa.pointee.sa_family == UInt8(AF_INET)),
               let cName = ifa.ifa_name {
                let name = String(cString: cName)
                if name.hasPrefix("en") || name.hasPrefix("bridge") {
                    var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                    if getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count),
                                   nil, 0, NI_NUMERICHOST) == 0 {
                        result.append(String(cString: host))
                    }
                }
            }
            ptr = p.pointee.ifa_next
        }
        return result
    }

    private func installSignalHandler() {
        // Ctrl+C 直接退出；虚拟屏由系统随进程回收（已验证无残留）
        let handler: @convention(c) (Int32) -> Void = { sig in
            print("\n收到信号 \(sig)，退出")
            exit(0)
        }
        signal(SIGINT, handler)
        signal(SIGTERM, handler)
    }
}

// MARK: - 参数解析与入口

setbuf(stdout, nil)   // 日志实时可见

let args = CommandLine.arguments
func argValue(_ names: [String], _ fallback: String) -> String {
    for i in 0..<(args.count - 1) where names.contains(args[i]) {
        return args[i + 1]
    }
    return fallback
}
let port = UInt16(argValue(["-p", "--port"], "8080")) ?? 8080
let width = Int(argValue(["-w", "--width"], "1920")) ?? 1920
let height = Int(argValue(["-H", "--height"], "1080")) ?? 1080
let fps = Int(argValue(["--fps"], "60")) ?? 60
let bitrate = Int(argValue(["-b", "--bitrate"], "80")) ?? 80   // Mbps（文字场景 1440p 建议 80+）
let useHEVC = argValue(["--codec"], "h264").lowercased() == "hevc"
let headless = args.contains("--headless")

App(port: port, width: width, height: height, fps: fps, bitrateMbps: bitrate,
    hevc: useHEVC, headless: headless).run()


/// 菜单栏菜单的构建与动作（每次展开时刷新状态）
final class MenuCoordinator: NSObject, NSMenuDelegate {
    private weak var app: App?

    init(app: App) {
        self.app = app
        super.init()
    }

    func rebuild(_ menu: NSMenu) { fill(menu) }

    func menuNeedsUpdate(_ menu: NSMenu) {
        fill(menu)
    }

    private func fill(_ menu: NSMenu) {
        menu.removeAllItems()
        guard let app else { return }

        let clients = app.clientCount
        let res = app.currentResolution
        let paused = app.paused

        // 1. 状态指示区（带原生指示小圆点与清晰信息）
        let statusTitle: String
        let subtitle: String?
        if paused {
            statusTitle = "⏸  投屏已暂停"
            subtitle = "点击下方「恢复投屏」继续"
        } else if clients > 0 {
            statusTitle = "🟢  正在扩展 · \(clients) 台设备已连接"
            subtitle = "\(res.w)×\(res.h) · \(app.fps)fps · \(app.currentBitrateMbps)Mbps \(app.hevc ? "HEVC" : "H.264")"
        } else {
            statusTitle = "🟡  等待连接 · 待机中"
            subtitle = res.w > 0 ? "虚拟屏 \(res.w)×\(res.h) 就绪" : "正在初始化虚拟屏…"
        }

        let statusItem = NSMenuItem(title: statusTitle, action: nil, keyEquivalent: "")
        statusItem.isEnabled = false
        menu.addItem(statusItem)

        if let sub = subtitle {
            let subItem = NSMenuItem(title: "    " + sub, action: nil, keyEquivalent: "")
            subItem.isEnabled = false
            menu.addItem(subItem)
        }

        menu.addItem(.separator())

        // 2. 核心配对与连接区（可直接点击复制到剪贴板）
        let pairItem = NSMenuItem(title: "配对码: \(app.pairingCode)  (点击复制)", action: #selector(copyPairingCode), keyEquivalent: "")
        pairItem.target = self
        menu.addItem(pairItem)

        let ips = App.localIPs()
        if let primaryIP = ips.first {
            let addrItem = NSMenuItem(title: "本机地址: \(primaryIP):\(app.port)  (点击复制)", action: #selector(copyAddress), keyEquivalent: "")
            addrItem.target = self
            menu.addItem(addrItem)
        }

        menu.addItem(.separator())

        // 3. 常用控制区
        let pause = NSMenuItem(title: paused ? "恢复投屏" : "暂停投屏",
                               action: #selector(togglePause), keyEquivalent: "")
        pause.target = self
        menu.addItem(pause)

        // 缩放控制：Mac 侧直接切换视网膜超采样比例
        let scaleItem = NSMenuItem(title: "显示缩放（文字大小）", action: nil, keyEquivalent: "")
        let scaleSubMenu = NSMenu()
        scaleSubMenu.autoenablesItems = false
        let currentScale = app.userScalePercent
        let scales: [(Int, String)] = [
            (100, "100% (点对点原生标准)"),
            (125, "125% (清晰大字 · 视网膜超采样)"),
            (150, "150% (舒适大字 · 视网膜超采样 · 推荐)"),
            (175, "175% (特大字号 · 视网膜超采样)"),
            (200, "200% (视网膜 2x · 超大)")
        ]
        for (sc, label) in scales {
            let item = NSMenuItem(title: label, action: #selector(changeScale(_:)), keyEquivalent: "")
            item.target = self
            item.tag = sc
            item.state = (currentScale == sc) ? .on : .off
            scaleSubMenu.addItem(item)
        }
        scaleItem.submenu = scaleSubMenu
        menu.addItem(scaleItem)

        if #available(macOS 13.0, *) {
            let auto = NSMenuItem(title: "开机自启", action: #selector(toggleAutostart), keyEquivalent: "")
            auto.target = self
            auto.state = app.autostartEnabled() ? .on : .off
            menu.addItem(auto)
        }

        menu.addItem(.separator())

        // 4. 高级选项（收纳低频/排障功能）
        let advancedItem = NSMenuItem(title: "高级选项", action: nil, keyEquivalent: "")
        let advMenu = NSMenu()
        advMenu.autoenablesItems = false

        let reset = NSMenuItem(title: "重置配对授权（清空已配对设备）", action: #selector(resetPairing), keyEquivalent: "")
        reset.target = self
        advMenu.addItem(reset)

        let resetLayoutItem = NSMenuItem(title: "重置屏幕排列（恢复默认右侧居中）", action: #selector(resetLayout), keyEquivalent: "")
        resetLayoutItem.target = self
        advMenu.addItem(resetLayoutItem)

        advMenu.addItem(.separator())

        let showLog = NSMenuItem(title: "在访达中查看运行日志...", action: #selector(showLogInFinder), keyEquivalent: "")
        showLog.target = self
        advMenu.addItem(showLog)

        if ips.count > 1 {
            advMenu.addItem(.separator())
            let otherIPsHeader = NSMenuItem(title: "所有可用网络地址 (点击复制):", action: nil, keyEquivalent: "")
            otherIPsHeader.isEnabled = false
            advMenu.addItem(otherIPsHeader)
            for ip in ips {
                let item = NSMenuItem(title: "  http://\(ip):\(app.port)", action: #selector(copyArbitraryAddress(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = "http://\(ip):\(app.port)"
                advMenu.addItem(item)
            }
        }

        advancedItem.submenu = advMenu
        menu.addItem(advancedItem)

        menu.addItem(.separator())

        // 5. 退出
        let quit = NSMenuItem(title: "退出 MacScreenSender", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
    }

    @objc private func copyPairingCode() {
        guard let app else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(app.pairingCode, forType: .string)
        Log.write("📋 已复制配对码到剪贴板: \(app.pairingCode)")
    }

    @objc private func copyAddress() {
        guard let app, let ip = App.localIPs().first else { return }
        let url = "http://\(ip):\(app.port)"
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(url, forType: .string)
        Log.write("📋 已复制服务地址到剪贴板: \(url)")
    }

    @objc private func copyArbitraryAddress(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? String else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(url, forType: .string)
        Log.write("📋 已复制服务地址: \(url)")
    }

    @objc private func showLogInFinder() {
        let url = Log.url
        if FileManager.default.fileExists(atPath: url.path) {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            NSWorkspace.shared.open(url.deletingLastPathComponent())
        }
    }

    @objc private func togglePause() { app?.togglePause() }
    @objc private func resetPairing() { app?.resetPairing() }
    @objc private func resetLayout() { app?.resetLayout() }
    @objc private func changeScale(_ sender: NSMenuItem) { app?.setScalePercent(sender.tag) }
    @objc private func toggleAutostart() { app?.toggleAutostart() }
    @objc private func quit() { app?.shutdownAndExit() }
}
