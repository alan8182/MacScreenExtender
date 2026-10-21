# android-receiver — 安卓平板无线扩展屏客户端

> 状态：**代码就绪，待真机联调**。与 `windows-receiver/`、`ipad-receiver/` 并列的第三个接收端。
> 本目录是完整的 Gradle 工程（Android Studio 直接打开即可），构建产物 `app/build/outputs/apk/debug/app-debug.apk`。

## 进度（对照 PLAN.md）

- ✅ **M0 协议修正**：按钮字节覆盖 bug 已在 Mac/Windows/iPad 三端修复（Mac 端 `swift build`、iPad 端
  `xcodebuild` 模拟器构建均通过；Windows 端改动为单字节字面量，随下次 Windows 构建验证）。
- ✅ **M1~M4 代码**：发现/配对/视频/控制/触摸/光标/保活/状态机全部实现，Android APK 构建通过。
- ⏳ **真机验收**：PLAN.md 各里程碑的验收标准（配对出画、点击 btn=0 日志、双指滚轮方向、
  断线自愈、2 小时 soak、延迟基线）需在真实平板 + Mac 环境逐项执行——本仓库构建机无模拟器/真机。

## 真机联调快捷路径

1. `./gradlew :app:assembleDebug`（或 Android Studio 直接 Run）安装到平板；
2. Mac 端重新构建运行 `MacScreenSender.app`（M0 修复需生效）；
3. 平板连接同一局域网 → 打开 App → 列表选 Mac → 输入菜单栏 4 位配对码；
4. 验收基准见 PLAN.md §4 各里程碑；联调观测点见 PROTOCOL.md §5（`/status` 页 + `sender.log`）。


## 结论（为什么可行）

**现有 Mac 端协议完全平台无关，安卓端可以零 Mac 侧改动直接接入。** 四层协议全部是标准
TCP/UDP/HTTP，没有任何 Apple 私有依赖；`ipad-receiver` 已在同等链路上验证过完整流程：

| 层 | Mac 端现状 | 安卓对接难度 |
|---|---|---|
| 设备发现 | UDP 8082 JSON 广播（2 秒/次） | 低：`DatagramSocket` + `MulticastLock` |
| 配对鉴权 | `GET /pair?code=` 换 token，IP+token 双放行 | 低：普通 HTTP GET |
| 视频 | HTTP `/raw` 裸 Annex-B H.264（默认）/HEVC 无限流 | 低：**MediaCodec 原生输入就是 Annex-B**，比 iPad 端还省一次 AVCC 转换 |
| 控制 | TCP 8081，16 字节定长包（触摸/滚轮/心跳/光标回报） | 低：一个 Socket + 字节缓冲 |

Mac 端 `Broadcaster` / `ControlServer` 本身就是多客户端设计（连接字典 + 新端接入即发
IDR 关键帧），安卓可以作为第二台设备与 iPad/Windows 同时在线（共享同一块虚拟拓展屏）。

## 文档导航

- **[PLAN.md](PLAN.md)** — 分阶段落地方案：技术选型、模块分解、里程碑与验收标准、风险对策、测试矩阵
- **[PROTOCOL.md](PROTOCOL.md)** — 线协议规格书（安卓开发者视角，逐字节定义，标注 Mac 端源码出处，不需要读 Swift 也能对接）

## 范围边界（MVP 口径，与 iPad 端对齐）

- 安卓端是**同一块虚拟拓展屏**的接收端：多端同时连 = 同屏显示，不是多块独立屏。
  （多块独立拓展屏需要 Mac 端支持多虚拟屏+多路编码，属于 Mac 端独立演进，不在本方案内）
- 触摸为主：单指=鼠标左键/拖拽，双指=滚轮，长按=右键；软键盘→VK 映射为后期可选（M5）。
- MVP 只走 H.264（安卓 OEM 对 HEVC 硬解支持参差），HEVC 做实验开关（M5）。

## 与 ipad-receiver 的关键差异

| 项 | iPad 端做法 | 安卓端做法 |
|---|---|---|
| 解码渲染 | AVSampleBufferDisplayLayer（需 Annex-B→AVCC 转换） | MediaCodec 直接吃 Annex-B NALU → SurfaceView 零拷贝 |
| 流明文 HTTP | 默认放行 | 需 `usesCleartextTraffic` 配置 |
| 保活 | 前台时系统不杀 | 前台服务 + WAKE_LOCK + keepScreenOn，需应对厂商省电 |
| 发现监听 | 直接收 UDP 广播 | 建议 `MulticastLock`，部分 OEM 省电丢广播 → 手动输 IP 兜底 |
| 光标 | type 8 回报 + 本地叠画 | 同思路：SurfaceView 上层叠一个自绘 View |
