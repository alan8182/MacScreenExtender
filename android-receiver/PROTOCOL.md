# 线协议规格（MacScreenExtender 安卓接收端对接用）

> 本文是安卓开发者视角的**独立**协议参考，全部内容从 Mac 端 Swift 源码与 Windows/iPad 端
> 现有实现逐字节核实（源码位置均已标注，截至 2026-09）。不需要阅读 Swift 代码即可对接。

## 0. 端口与角色总览

| 端口 | 协议 | 方向 | 用途 |
|---|---|---|---|
| 8082 | UDP | Mac→局域网广播 | 设备发现（2 秒/次） |
| 8080 | HTTP | 客户端→Mac | 配对 `/pair`、鉴权检查 `/pair-check`、视频流 `/raw`、状态 `/status` |
| 8081 | TCP | 双向长连接 | 控制通道：触摸/滚轮/键盘（C→M），心跳/光标回报（M→C），16 字节定长包 |

**推荐连接时序**：发现（可选）→ `/pair` 换 token（仅首次）→ 连接 TCP 8081 → `GET /raw?token=` 拉流。
配对成功后客户端 IP 进入白名单，控制通道按 IP 放行（`ControlServer.swift:33-41`），
因此**必须先 HTTP 配对、后连控制通道**，否则 8081 直接被拒。

---

## 1. 设备发现（UDP 8082）

Mac 每 2 秒向 `255.255.255.255:8082` 和子网定向广播 `x.x.x.255:8082` 各发一份 JSON
（`main.swift:373-391`）：

```json
{"name":"My-MacBook-Pro","ip":"192.168.1.10","port":8080,"proto":1}
```

- `ip`：Mac 的首个局域网地址（视频/控制都用它）；`port`：HTTP 端口；`proto`：协议版本（当前恒为 1）。
- 安卓端：`DatagramSocket(port: 8082, broadcast: true)` 监听，按 `ip` 去重，建议 6 秒无广播即从列表移除。
  接收广播前获取 `WifiManager.MulticastLock`（部分 OEM WiFi 省电会丢广播包）。
- **手动输 IP 兜底必须有**——发现只锦上添花，Windows/iPad 端也都保留手动入口。

## 2. 配对与鉴权（HTTP 8080）

| 请求 | 成功响应 | 失败响应 |
|---|---|---|
| `GET /pair?code=1234` | `200`，body = token 明文（UUID） | `403`，body = `ERR 配对码不正确` |
| `GET /pair-check?token=xxx` | `200`，body = `OK` | `403`，body = `NEED-PAIR` |
| `GET /raw?token=xxx&w=..&h=..` | `200` 无限视频流 | `403`，body = `NEED-PAIR` |

（`main.swift:674-698`）

- 配对码是 Mac 菜单栏显示的 4 位数字，终身不变；token 换到后**持久化存储**，终身有效。
- 鉴权规则：URL 带 token 且 token 命中 → 放行，并**顺便把当前 IP 记入白名单**
  （`main.swift:362-369`，应对 DHCP 换址）。白名单 IP 之后不带 token 也放行。
- 安卓端建议每次请求都带 `token` 参数（覆盖换 IP 场景），收到 `403 NEED-PAIR` → 自动弹配对码输入。

## 3. 视频流（`GET /raw`）

### 3.1 请求参数

```
GET /raw?token=<token>&w=<宽>&h=<高>[&scale=<100~300>]
```

| 参数 | 必填 | 说明 |
|---|---|---|
| `token` | 建议 | 鉴权；白名单 IP 可省 |
| `w` / `h` | 建议 | 安卓端传自己的物理分辨率（如 2360×1640）。合法域 w:640~7680，h:480~4320；别名 `pw/ph/outw/outh`（`main.swift:703-704`） |
| `scale` | 可选 | 请求 Mac 端虚拟屏 UI 缩放百分比，100~300（`main.swift:707-713`）。MVP 不传（=维持现状） |
| `hidpi` | 不传 | macOS 实际忽略 hiDPI 标志（见 Mac 端已知限制），传了也没用 |

### 3.2 响应与流格式

```
HTTP/1.1 200 OK
Content-Type: video/h264        ← HEVC 模式为 video/hevc（main.swift:721）
Cache-Control: no-store
Connection: close
Transfer-Encoding: chunked      ← 无限流，无 Content-Length，靠断连收尾
```

之后是**裸 Annex-B H.264**（默认编码）无限字节流，安卓端直接流式读 body 即可：

- **SPS+PPS 在每个关键帧（IDR）前重复前插**（`VideoEncoder.swift:180-185`）——
  解码器随时可以入流初始化，不必等流首。
- 新客户端接入时 Mac 立即强制 IDR（`main.swift:728`），正常 ≤1 秒出画；GOP 上限 2 秒。
- 编码档位：**H.264 High Profile + CABAC**（`VideoEncoder.swift:52-64`），不是 Baseline。
  绝大多数安卓硬解支持 High；M2 阶段用 `MediaCodecList` 做一次能力确认即可。
- 无 B 帧、编码零缓冲（MaxFrameDelay=0）→ 收到即解即渲，不需要重排序缓冲。
- 流内**没有时间戳**（Annex-B 裸流不携带 PTS）：解码时间戳由安卓端单调自增生成。
- 码率 AIMD 自适应 10~80Mbps（随网络浮动），读流侧按"有数据就吃"处理，无需解析码率。

### 3.3 两个必须处理的流事件

1. **解码器中途重配**：若另一客户端以**不同分辨率**接入，Mac 会重建虚拟屏，
   流内随之出现新 SPS（画面尺寸变化）。安卓端检测到 SPS 变化必须 `stop()` 后按新参数
   `configure()` 重配解码器（参考 Windows 端"6 秒无帧自动重建"的自愈思路）。
2. **流冻结自愈**：网络抖动可能丢断流。≥6 秒无 NALU → 断开重连 `/raw`；
   重连后仍无帧 → 发控制通道 type 9 请求立即 IDR（见 §4）。

## 4. 控制通道（TCP 8081）

### 4.1 帧格式

**16 字节定长小端包**，一次 `send` 一个包；接收端按 16 字节切帧（`ControlServer.swift:126-137`）。

```
byte 0      : type
byte 1..3   : 0（保留）
byte 4..15  : 随 type 变化的载荷（浮点均为 IEEE754 little-endian）
```

### 4.2 包类型总表

| type | 方向 | 含义 | 载荷布局 |
|---|---|---|---|
| 1 | C→M | 鼠标移动 | nx f32@4，ny f32@8 |
| 2 | C→M | 按键按下 | btn u8@5，nx f32@4，ny f32@8 |
| 3 | C→M | 按键抬起 | 同 type 2 |
| 4 | C→M | 滚轮 | dx f32@4，dy f32@8 |
| 5 | C→M | 键盘（M5 可选） | down u8@4（1=按下），vk u16@6 |
| 7 | M→C | 心跳 | seq u32@4 —— **客户端必须原样回传整个 16 字节** |
| 8 | M→C | 光标回报 | nx f32@4，ny f32@8，visible u8@12 |
| 9 | C→M | 请求立即 IDR 关键帧 | 无载荷 |

### 4.3 语义细节

- **坐标**：归一化 0~1，相对虚拟拓展屏；超出会被 Mac 端钳制（`InputInjector.swift:24`）。
- **按钮码 btn**：`0=左键，1=右键，其他=中键`（`InputInjector.swift:33-48`）。
  安卓映射：单指点按=左键，长按=右键。
- **滚轮**：行单位，`dy>0` = 向上滚（Mac 端换算 `wheel1 = -dy*3`，`InputInjector.swift:50-58`）。
  双指滑动按像素增量换算行数，灵敏度建议初始 `0.002 行/像素`，实测调。
- **移动注入即光标所有权切换**：Mac 端 250ms 内收到注入事件 → 视频里**不再烙 Mac 指针**，
  由客户端依据 type 8 回报**本地绘制光标**（零感知延迟的关键，见 Mac `ARCHITECTURE.md` §1）。
  安卓端：触摸活跃期（含其后 ~300ms）显示本地光标图层，静默后隐藏（否则与入画的 Mac 指针双显）。
- **心跳**：Mac 每连接每 2 秒发 type 7（`ControlServer.swift:93-124`）；客户端收到后**逐字节原样
  回传**，Mac 用它测 RTT 驱动码率自适应。客户端侧 ≥6 秒没收到任何包（心跳/光标）→ 判定断线，
  重连 8081 并重拉 `/raw`。
- **光标回报 type 8**：Mac 以 8ms 定时器检查、**仅坐标/可见性变化时发送**
  （实际频率 ≤125Hz，`ControlServer.swift:55-80`）。`visible=0` 表示 Mac 光标不在拓展屏内
  （本地光标应隐藏）。
- **键盘 type 5（后期）**：载荷是 **Windows VK 码**。Mac 端映射 Ctrl→Cmd、Alt→Option、
  Win→Ctrl、Shift 原样（`InputInjector.swift:107-113`）。安卓软键盘 `KeyEvent.keyCode`
  需一张 Android→VK 映射表（M5 再做，MVP 不实现）。

### 4.4 ⚠️ 现网缺陷：按钮字节被坐标覆盖（截至本方案日，待 M0 修复）

**已发布的三端（Windows/iPad 发送端 + Mac 解析端）存在一个线协议 bug**：
发送端先写 `btn@4` 再写 `nx f32@4`，坐标浮点的最低位字节把按钮字节覆盖掉了
（`windows-receiver/InputForwarder.cs:134`、`ipad-receiver/.../ControlClient.swift:62-69`
注释"协议兼容：同时在 4 写入坐标"即此问题的现场痕迹）。Mac 端读 `d[4]` 当按钮码
（`ControlServer.swift:203-204`），实测日志中 86% 的真实点击 btn 字节为非零值
（= 被注入成中键，多数应用无响应）——这正是 `ISSUES.md` P4「点击偶尔失灵」的确定性根因，
此前归因于 TCP 瞬断不成立。

**本规格采用的格式（type 2/3）**：`btn u8@5`，nx/ny 维持 `@4/@8` 不变。
byte 5 在现行三端的 type 2/3 包中恒为 0，挪按钮码到 byte 5 是零冲突的最小改动。
安卓端**按本规格实现（btn 写 byte 5）**，M0 里程碑同步修正 Mac 解析端与 Windows/iPad 发送端
（四处小改，见 PLAN.md M0）。在 M0 修复落地前，安卓端连调会出现"点击变中键"现象，
属预期，勿按旧格式回退适配。

## 5. 状态观测（联调时用）

- `GET http://<mac-ip>:8080/`（免鉴权）：文本状态页，含当前虚拟屏分辨率、码率、**客户端数**
  ——验证"安卓端已被 Mac 计入"最直接的观测点（`main.swift:659-668`）。
- Mac 端日志 `~/Library/Application Support/MacScreenExtender/sender.log`：
  `客户端接入: ... [raw]`、`🖱 点击 btn=`、`📶 码率自适应`、`🔁 控制通道 RTT` 等行是联调验收的基准证据。
