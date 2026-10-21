# 安卓接收端分阶段落地方案（PLAN）

> 目标：把任意安卓平板（2019 年后、支持 5GHz WiFi 优先）变成 Mac 的无线触控扩展屏。
> 协议可行性评估与线规格见 [README.md](README.md) / [PROTOCOL.md](PROTOCOL.md)。
> 工期口径：单人全职；MVP = M0~M4。

## 1. 范围

**MVP 做**：发现 + 配对 + H.264 视频 + 触摸控制（单指/双指/长按）+ 本地光标 + 断线自愈 + 保活。
**MVP 不做**：软键盘（M5）、HEVC（M5 实验开关）、多块独立拓展屏（Mac 端独立演进，不在安卓侧）、
音视频加密、Google Play 上架合规（侧载分发自用）。

## 2. 技术选型

| 项 | 选型 | 理由 |
|---|---|---|
| 语言/平台 | Kotlin 2.x，minSdk 26（Android 8.0），targetSdk 35 | 覆盖主流平板；API 30+ 特性（`KEY_LOW_LATENCY`）代码内判版本 |
| UI | Compose（连接/配对页）+ `SurfaceView`（视频层）+ 自绘光标 View | SurfaceView 走硬件合成、比 TextureView 少一帧合成开销，是低延迟关键 |
| 网络 | OkHttp 4.x（/raw 无限 chunked 流）+ 系统库 `DatagramSocket`/`Socket`（发现/控制） | OkHttp 对无长度流式 body 读取稳定；控制协议太简单不值得引库 |
| 解码 | `MediaCodec` 异步模式直出 Surface | 唯一能控延迟的路径；**不用 ExoPlayer/Media3/VLC**——内部缓冲不可控，违背零缓冲设计（与 Mac 端弃 VLC 同理，见 ARCHITECTURE.md §7） |
| 并发 | Coroutines + `Dispatchers.IO`；解码回调自带线程 | — |
| 存储 | SharedPreferences（token、host、偏好） | 单字段场景，不引 DataStore |
| 方向 | 横屏锁定（`sensorLandscape`） | 拓展屏无竖屏场景 |

## 3. 工程结构（Gradle 项目根 = 本目录）

```
android-receiver/
├── docs（本文档）
├── settings.gradle.kts / build.gradle.kts / gradle ...
└── app/src/main/
    ├── AndroidManifest.xml            # usesCleartextTraffic、前台服务、横屏锁、权限
    └── java/com/macscreextender/receiver/
        ├── MainActivity.kt            # 单 Activity；连接页 ↔ 全屏拓展模式状态机（对齐 ContentView.swift）
        ├── ui/ConnectionScreen.kt     # 设备列表 + 手动 IP + 配对码弹窗（对齐 ConnectionView/PairSheet）
        ├── ui/FullscreenHost.kt       # SurfaceView + 光标叠加层 + 退出手势宿主
        ├── net/DiscoveryService.kt    # UDP 8082 监听 + MulticastLock + 6s 老化（对齐 DiscoveryService.swift）
        ├── net/PairingClient.kt       # /pair、/pair-check、NEED-PAIR 处理（对齐 ReceiverConfig.swift）
        ├── net/VideoStreamClient.kt   # OkHttp 读 /raw 字节流 → NALU 切分（对齐 VideoStreamClient.swift）
        ├── media/NaluParser.kt        # Annex-B start-code 扫描 + SPS/PPS 提取与变化检测（移植 NALUParser.swift，去掉 AVCC 转换）
        ├── media/VideoDecoder.kt      # MediaCodec 异步 → Surface；重配自愈（iPad 无对应物，参考 Windows VideoDecoder.cs 思路）
        ├── net/ControlClient.kt       # TCP 8081 16 字节帧收发 + 心跳回传 + 断线检测（对齐 ControlClient.swift）
        ├── input/TouchMapper.kt       # MotionEvent → type 1/2/3/4；手势定义见 §5.3
        ├── input/CursorOverlayView.kt # type 8 → 本地光标绘制（对齐 Windows 系统光标/iPad 本地画指针）
        ├── service/StreamService.kt   # 前台服务 + WAKE_LOCK + keepScreenOn
        └── store/ConfigStore.kt       # token/host 持久化
```

## 4. 里程碑

### M0 协议修正（前置，0.5~1 天）——不修则安卓点击必坏

修复 PROTOCOL.md §4.4 所述按钮字节被坐标覆盖的现网缺陷（实测 86% 点击被注入成中键，
即 ISSUES.md P4「点击偶尔失灵」的确定性根因）。共四处小改：

| 文件 | 改动 |
|---|---|
| `Sources/MacScreenSender/ControlServer.swift`（case 2,3） | `d[4]` → `d[5]` 读按钮码 |
| `windows-receiver/InputForwarder.cs:134` | `p[4] = b` → `p[5] = b`（nx 改写 @4 不再冲突） |
| `ipad-receiver/.../ControlClient.swift:65` | `p[4] = btn` → `p[5] = btn`，更新注释 |
| `ISSUES.md` P4 | 根因更正为字节覆盖，注明验证方法 |

**验收**：iPad（或 Windows）连接后在屏幕四角/中央各点 5 次，`sender.log` 中
`🖱 点击 btn=0` 占 100%（右键长按场景 `btn=1`）；点击/拖拽在任意位置可用。

### M1 工程脚手架 + 发现 + 配对（1~1.5 天）

- 建工程、依赖、Manifest（`INTERNET`、`ACCESS_NETWORK_STATE`、`FOREGROUND_SERVICE`、
  `WAKE_LOCK`、`POST_NOTIFICATIONS`；`usesCleartextTraffic=true`）。
- `DiscoveryService`（UDP 8082 + MulticastLock + 去重老化）、设备列表 UI、手动 IP 输入。
- `PairingClient`：配对码弹窗 → `/pair` → token 存 SharedPreferences → `/pair-check` 验证；
  全局 403 NEED-PAIR → 自动弹配对。

**验收**：Mac 在列表 3 秒内出现；输码配对成功；杀 App 重开免重配；改 Mac 端配对码后
token 失效路径（清空配对）能重新配对。

### M2 视频链路出画（2~3 天）——技术核心段

- `VideoStreamClient`：OkHttp 流式读 `/raw?token=&w=&h=`（w/h 传平板物理分辨率）。
- `NaluParser`：滚动缓冲 + start code（`000001`/`00000001`）扫描；H.264 取 nalType 7/8
  为 SPS/PPS；记录最近参数集字节用于**变化检测**。
- `VideoDecoder`：首个 SPS/PPS → `MediaFormat`（`csd-0`=SPS，`csd-1`=PPS）configure；
  异步回调；输出帧立即 `releaseOutputBuffer(index, true)`；API 30+ 设 `KEY_LOW_LATENCY`；
  `KEY_MAX_INPUT_SIZE` 给足（≥2MB，80Mbps 下 IDR 帧不小）。SPS 字节变化 → 重建解码器
  （应对多端并发时的虚拟屏重建，PROTOCOL.md §3.3）。
- 断流看门狗：6 秒无 NALU → 重拉 `/raw`；连拉失败 → 控制通道 type 9 请求 IDR。

**验收**：接入后 ≤1 秒出画；`/status` 客户数 +1；持续 60fps 无花屏；
（配合 iPad 在线、先低后高分辨率二次接入）安卓端自动重配不出黑屏；
未配对直连 `/raw` 时收到 403 并引导配对。

### M3 控制通道 + 触摸（2 天）

- `ControlClient`：`Socket(TCP_NODELAY)`，读循环按 16 字节切帧；type 7 心跳**逐字节回传**；
  type 8 → 光标层；≥6 秒无任何下行 → 重连。
- `TouchMapper`（手势定义见 §5.3）：MotionEvent → type 1/2/3/4；复用 16 字节缓冲零分配。
- `CursorOverlayView`：触摸活跃期（末次注入后 ~300ms 内）且 `visible=1` 时按 type 8 画
  光标，静默期隐藏（避免与入画的 Mac 指针双显——ARCHITECTURE.md §1 所有权机制）。

**验收**：单指点按/拖拽、长按右键、双指滚轮全链路可用；`sender.log` 见
`🖱 点击 btn=0/1` 且坐标正确；无双光标；开关飞行模式 6 秒内自动恢复画面与控制。

### M4 稳定性 + 保活 + 延迟验收（2~3 天）

- `StreamService` 前台服务（`mediaPlayback` 类型）+ WAKE_LOCK + `keepScreenOn`；
  通知常驻；厂商省电白名单引导页（小米/华为/三星差异各一条文案）。
- 全屏沉浸（sticky immersive）+ 双指双击呼出退出栏（与 iPad 端交互对齐）。
- 延迟实测：高速摄影 240fps 拍"Mac 上触发鼠标移动 → 平板画面响应"，或用
  https://www.testufo.com 跨屏对照法；记录点击/滚动端到端延迟基线。
- 2 小时 soak（画面常动 + 间歇触摸）。

**验收（MVP 完成线）**：soak 零崩溃零断流；5GHz 同网下触控点击端到端延迟中位数
≤80ms（对齐 Windows 端可用标准）；滚轮方向正确、无双向抖动；掉线重连无需人工干预。

### M5 可选增强（不占 MVP 工期，按需排）

软键盘→VK 映射表（对齐 `InputInjector.swift` vkToMac 语义，Ctrl→Cmd 等修饰键策略）、
HEVC 实验开关（`MediaCodecList` 能力检测后启用）、双指双击灵敏度、音量/亮度快捷条、
USB 网络共享有线链路实验（见 §6 第 9 条）。

## 5. 核心实现要点

### 5.1 解码喂包策略

每个完整 NALU（含 start code）单独 queueInputBuffer，时间戳用单调自增微秒
（流内无 PTS，PROTOCOL.md §3.2）。IDR 前导的 SPS/PPS 已在 csd 配置过，仍照常喂入
（带内参数集各厂解码器均兼容）。**不要**按帧攒批再喂——增加不必要的缓冲即增加延迟。

### 5.2 视频线程模型（对齐 Windows 端教训，ARCHITECTURE.md §3）

网络读 + NALU 切分在 IO 协程；MediaCodec 解码回调线程只做"取帧→渲染"；
两段之间用无锁单帧槽（最新帧覆盖旧帧）衔接。禁止在解码回调里做任何 UI/锁重操作。

### 5.3 手势映射表（M3 冻结，后续只调参数不换语义）

| 手势 | 映射 | 备注 |
|---|---|---|
| 单指点按（<200ms，未超 slop） | 左键 down+up（type 2/3） | down/up 坐标相同 |
| 单指按下拖动 | 按下即 left down；移动发 type 1（Mac 端自动变拖拽） | 拖拽由 Mac 端 leftDown 状态合成，无需专门事件 |
| 长按（≥500ms） | 右键 down，抬起 up | 触发时轻微震动反馈 |
| 双指滑动 | type 4，`dy` 按像素增量×灵敏度（初始 0.002 行/px） | 合并到 ≤60Hz 发送；两指都按住期间不发 move |
| 双指双击 | 呼出退出/断开悬浮栏（本地 UI） | 与 iPad 端一致 |

## 6. 延迟优化清单（按投入产出排序）

1. `SurfaceView` 直出（已定）；禁用 `TextureView`。
2. MediaCodec 异步 + 输出即渲，不 etc/vsync 门控。
3. API 30+ `KEY_LOW_LATENCY`；`KEY_PRIORITY=0`。
4. 两个 socket 均 `TCP_NODELAY`。
5. 触摸事件即到即发（Android 系统已做批量，App 层不再攒批；仅滚轮合并）。
6. WiFi：5GHz 同 AP；干扰大时改用 **Mac 开热点、平板直连**（少一跳路由，jitter 显著降低）。
7. 平板开最高刷新率（120Hz 机型），开发者选项关动画。
8. 码率与分辨率匹配：WiFi 常规环境默认 80Mbps 上限够用；卡顿场景优先降 scale 而非降帧。
9. （实验）Android **USB 网络共享**（USB tethering）建立 USB NIC 直连：Mac 与平板间走
   有线网络栈，理论延迟/抖动接近有线显示器；需实测该链路上 8080/8081 的可达性，M5 验证。

## 7. 风险与对策

| 风险 | 等级 | 对策 |
|---|---|---|
| OEM 省电杀后台/断 socket | 高 | 前台服务 + WAKE_LOCK + keepScreenOn；厂商白名单引导；重连看门狗兜底 |
| UDP 广播被 OEM 丢包 | 中 | 手动 IP 兜底（已设计）；MulticastLock |
| 老平板硬解 High Profile+CABAC 不力 | 中 | `MediaCodecList` 能力自检，不支持时引导提示；minSdk 档位上仍以 2019+ 硬件为建议门槛 |
| 多端并发时虚拟屏分辨率被对方改走 → 双端互相踢重配 | 中 | 行为已知（PROTOCOL.md §3.3）：解码器重配自愈 + M4 双端并发用例；长期解法是 Mac 端"首个客户端定分辨率"策略（另立 Mac 端 issue，不在本方案） |
| HEVC 各厂解码器行为参差 | 低 | MVP 锁 H.264，HEVC 仅 M5 实验开关 |
| 延迟不达预期（低端平板） | 中 | M4 延迟基线是验收门；降级路径：§6 第 6/8/9 条 |
| M0 协议修正需三端同步重建 | 低 | 旧端回退行为可接受（旧端点击本来就是坏的）；本仓库统一发版 |

## 8. 测试矩阵

| 维度 | 覆盖 |
|---|---|
| 平板 | 小米 Pad 5/6（120Hz）、华为 MatePad（无 GMS）、三星 Tab S 系、一台 2019 前低端机（下限验证） |
| 系统 | Android 10 / 13 / 14+ |
| 网络 | 同 AP 5GHz、Mac 热点直连、隔一堵墙、WiFi↔热点切换中恢复 |
| 并发 | iPad 同时在线（同分辨率 / 不同分辨率两个用例）；双端同时触摸 |
| Mac | 当前主力机；睡眠唤醒后自愈 |

## 9. 工期汇总

| 里程碑 | 内容 | 工期 |
|---|---|---|
| M0 | 协议修正（跨端四处小改 + 验证） | 0.5~1 天 |
| M1 | 脚手架 + 发现 + 配对 | 1~1.5 天 |
| M2 | 视频链路出画 | 2~3 天 |
| M3 | 控制通道 + 触摸 | 2 天 |
| M4 | 稳定性 + 保活 + 延迟验收 | 2~3 天 |
| **MVP 合计** | | **8~10.5 个工作日**（M5 另计） |
