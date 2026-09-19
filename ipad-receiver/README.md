# MacScreenReceiver-iPad — iPad 无线扩展屏客户端

把任意一台 iPad（支持不同 Apple ID、旧款不支持官方随航的机型）变成 Mac 的无线高刷触控扩展屏。

---

## ✨ 核心亮点

- 🍏 **摆脱 Apple ID 绑定限制**：完美解决“公司 Mac（企业账号）+ 个人 iPad”无法开启官方随航（Sidecar）的最大痛点。
- ⚡ **原生全 GPU 零拷贝硬件解码**：基于 iPadOS 原生 `AVSampleBufferDisplayLayer`，硬件直出解码 H.264 / HEVC，延迟低至 5~15ms，CPU 占用低于 2%。
- 👆 **触控与 Apple Pencil 即时映射**：
  - **单指点击 / 拖拽**：即时映射为鼠标左键点击与拖拽；
  - **双指平滑滑动**：即时映射为 Mac 视网膜级平滑垂直/水平滚轮；
  - **Apple Pencil 专属支持**：精细点按与绘画直接同步；
  - **双指双击**：唤起悬浮控制栏与退出全屏操作。
- 📡 **局域网 UDP 广播自动发现**：监听 Mac 端广播，零配置自动列出可用 Mac 设备，支持 4 位安全握手配对。

---

## 🛠️ 在 Xcode 中运行与安装

1. 用数据线将 iPad 连接到 Mac（或直接使用 iPad 模拟器）；
2. 在 Mac 上双击打开项目：
   ```bash
   open ipad-receiver/MacScreenReceiver-iPad.xcodeproj
   ```
3. 在 Xcode 顶部设备栏中选择你的 **iPad**（或 iPad 模拟器，例如 iPad Pro 11-inch）；
4. 在 **Signing & Capabilities** 中选择你的 Apple ID 团队（Personal Team 或 Developer Team）；
5. 按快捷键 <kbd>Cmd</kbd> + <kbd>R</kbd> 直接编译并运行到 iPad 上！

---

## 📱 使用指南

1. **Mac 端**：启动 `MacScreenSender.app`，确保菜单栏正常常驻；
2. **iPad 端**：打开应用，应用将自动发现局域网内的 Mac，点击 **“连接”**；
3. **首次配对**：输入 Mac 菜单栏中显示的 4 位配对码，验证成功后自动开播；
4. **全屏触控**：进入扩展屏全屏后，手指/Apple Pencil 点按即点即控，随时**双指双击**屏幕呼出退出按钮。

---

## 🏗️ 模块架构

```
MacScreenReceiver/
├── MacScreenReceiverApp.swift   # SwiftUI 应用入口
├── Models/
│   ├── DiscoveredMac.swift      # 设备发现模型
│   └── ReceiverConfig.swift     # 本地持久化配置（Token、分辨率、偏好）
├── Services/
│   ├── DiscoveryService.swift   # UDP 8082 广播自动监听发现
│   ├── VideoStreamClient.swift  # /raw 视频流拉取与 CMSampleBuffer 渲染管理
│   ├── NALUParser.swift         # Annex-B H.264/HEVC NALU 分包与 SPS/PPS 参数集解析
│   └── ControlClient.swift      # TCP 8081 手势触控与心跳保活客户端
├── Views/
│   ├── ContentView.swift        # 主状态机（连接面板 ↔ 全屏拓展模式切换）
│   ├── ConnectionView.swift     # 现代卡片连接界面与设备列表
│   ├── DisplayView.swift        # AVSampleBufferDisplayLayer 硬件渲染与手势捕获
│   └── PairSheet.swift          # 4 位配对码验证弹窗
└── Info.plist                   # 局域网权限与横屏设置
```
