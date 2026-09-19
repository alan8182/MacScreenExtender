# MacScreenExtender — Mac ↔ Windows 局域网扩展屏

把一台 Windows 电脑变成 Mac 的**无线扩展显示器**：Mac 上创建一块虚拟显示器，
画面实时串流到 Windows 全屏显示；Windows 的键鼠直接控制 Mac。
GPU 全管线（VideoToolbox 硬编 + d3d11va 硬解 + D3D11 合成渲染），
光标由当前控制方渲染（控制端 0ms 跟手），支持 H.264/HEVC、60/120fps、自适应码率。

## 功能特性

- **虚拟扩展屏**：Mac 端创建真实存在的虚拟显示器（2560×1600 等，分辨率自动匹配 Windows 屏幕）
- **双端控制**：Windows 键鼠控制 Mac（含系统级快捷键拦截）；Mac 本地触控板同样可用
- **光标零延迟**：控制端系统光标直接渲染，跟随手部无量化跳跃
- **自适应码率（AIMD）**：网络吞吐不足自动降码率保流畅，空闲自动回升保清晰
- **硬解硬编全 GPU 管线**：d3d11va 硬解 + D3D11 合成渲染 + 垂直同步，CPU 占用 1~6%
- **断线自愈**：心跳超时重连、解码僵死重建、分辨率不匹配自动协商、配对失效自动重配
- **自动发现 + 配对鉴权**：局域网自动发现，4 位配对码换长期 token，未配对设备拒绝连接
- **菜单栏 App（Mac）**：状态/暂停恢复/开机自启/重置配对，签名安装后权限持久

## 系统要求

| 端 | 要求 |
|---|---|
| Mac | Apple Silicon（M1+），macOS 13+，Xcode 15+（构建用） |
| Windows | x64，.NET 8 Desktop Runtime，DirectX 11 显卡（硬解可选） |
| 网络 | 同一局域网；建议 5GHz WiFi 或有线；视频码率 10~120Mbps 自适应 |

## 构建

### Mac 端

```bash
cd ~/Documents/screen
./build-app.sh "Apple Development: 你的名字 (TEAMID)"   # 推荐：固定证书签名
# 或
./build-app.sh                                          # ad-hoc 签名（重打包需重新授权）
```

产物：`build/MacScreenSender.app`，脚本会自动安装到 `/Applications`。
> ⚠️ TCC 权限（屏幕录制/辅助功能）绑定签名身份：ad-hoc 签名每次重打包都会
> 丢失权限，固定证书签名一劳永逸（详见 build-app.sh 头部注释）。

### Windows 端

```bash
cd windows-receiver
dotnet publish -c Release -r win-x64
# ffmpeg 共享库（avcodec-62.dll 等）需与本目录 publish/ 内版本一致，随 zip 分发
```

产物：`MacScreenReceiver-win64.zip`（绿色版，解压即用）。

## 使用

1. **Mac**：启动 `/Applications/MacScreenSender.app`（首次需在
   系统设置 → 隐私与安全性 中授予**屏幕录制**和**辅助功能**权限）
2. **Windows**：运行 `MacScreenReceiver.exe` → 局域网内的 Mac 会自动出现在
   「发现」下拉框 → 选中 → 连接 → 首次输入 Mac 菜单栏显示的 **4 位配对码**
3. 首帧到达后自动全屏；`Esc` 退出全屏（再按退出程序）
4. 光标：Windows 鼠标动 → 拓展屏上箭头即时跟随；停手后 Mac 端移动指针同样可见

### 启动参数（Mac 端）

| 参数 | 默认 | 说明 |
|---|---|---|
| `-p/--port` | 8080 | HTTP 端口（控制通道 = 端口+1） |
| `-w/-H` | 1920/1080 | 初始虚拟屏分辨率（Windows 连接后自动按其屏幕重建） |
| `--fps` | 60 | 虚拟屏刷新率 = 采集帧率 = 编码帧率（支持 `--fps 120`） |
| `-b/--bitrate` | 80 | 码率上限 Mbps（AIMD 在此上限内自适应，下限 10） |
| `--codec` | h264 | `h264` / `hevc`（HEVC 同画质省 30-50% 码率，推荐） |
| `--headless` | — | 无菜单栏（SSH/脚本场景） |

示例（120Hz 屏幕全开）：

```bash
/Applications/MacScreenSender.app/Contents/MacOS/MacScreenSender --codec hevc --fps 120 -b 120
```

## 日志与排障

| 日志 | 位置 |
|---|---|
| Mac | `~/Library/Application Support/MacScreenExtender/sender.log` |
| Windows | `%APPDATA%\MacScreenExtender\receiver.log` |

常见症状速查：

| 症状 | 排查 |
|---|---|
| 黑屏/冻帧 | 看接收器状态栏「渲染 GPU/CPU」+ receiver.log；GPU 失败自动回退 CPU |
| 光标不动 | Mac 端 sender.log 看「🖱」行是否增长；无 → 控制通道断，检查 8081 |
| 画面模糊 | 确认 Windows 显示器为原生分辨率；提高 `-b`；确认状态栏无「⚠️不一致」 |
| 连接后无画面 | 看状态栏「检查配对/配对码」；Mac 端日志有无「采集启动超时」 |
| 字体太小 | 虚拟屏 1x 渲染所致：`-w 2048 -H 1280`（UI 大 25%，清晰度略降） |

## 架构

详见 [ARCHITECTURE.md](ARCHITECTURE.md)（数据流、线程模型、协议、设计决策与踩坑记录）。

## 已知限制

- `Ctrl+Alt+Del` 为系统保留组合键，无法转发；`Esc` 保留给本机退全屏
- 虚拟屏为 1x 渲染（macOS 忽略私有 API 的 hiDPI 标志），无 Retina 超采样
- 中文输入使用 Mac 端输入法（Windows 端 IME 全屏时自动禁用）
- 视频流局域网明文传输；配对 token 仅防未配对设备

## License

仅供个人使用。使用了 macOS 私有 API（CGVirtualDisplay），不符合 Mac App Store 分发要求。
