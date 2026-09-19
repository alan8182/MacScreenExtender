# 问题跟踪（ISSUES）

> 记录近期报告的问题、修复状态与验证方法。
> 状态含义：🔴 未修复 · 🟡 修复已提交待验证 · 🟢 已确认修复 · ⚪ 设计限制
>
> 日志位置：
> Mac `~/Library/Application Support/MacScreenExtender/sender.log`
> Windows `%APPDATA%\MacScreenExtender\receiver.log`

---

## 🟢 P1 GPU 模式黑屏与渲染（已确认修复并默认启用）

- **现象**：早期勾选 GPU 后画面全黑；CPU 模式正常但高分辨率下 CPU 占用高。
- **根因分析**（深度代码与管线排查）：
  1. **合成线程未启动**：`ReceiverForm.StartDecoder()` 创建 `_gpu` 与 `_dec` 后遗漏调用 `_gpu.SetFrameSource(_dec)`，导致专用合成线程 `ComposeLoop` 根本没有运行，渲染管线从未呈现（`Present` 未被调用）；
  2. **FFmpeg 硬解缺少 `get_format` 协商回调**：FFmpeg D3D11VA 硬解必须在 `AVCodecContext` 挂接 `get_format` 回调并明确返回 `AV_PIX_FMT_D3D11`；此前缺失该回调导致 FFmpeg 默认回退 CPU 软解（`YUV420P`），`_retained` 永远没有收到硬解纹理，而此时窗体因 `IsGpuRendering=true` 抑制了 GDI 绘制，导致彻底黑屏；
  3. **VideoProcessorEnumerator 分辨率不匹配**：`CreateProcEnum` 将输入/输出宽高一律传窗口尺寸，未动态按视频帧（`f->width, f->height`）设置 `InputWidth/InputHeight`，导致创建输入视图时报 `E_INVALIDARG`；
  4. **DXGI 缩放模式与纹理生命周期**：交换链使用 `Scaling.None` 在尺寸暂态不一致时触发 `DXGI_ERROR_INVALID_CALL`；托管层包装对象析构时可能干扰 FFmpeg 原生纹理池引用。
- **已实施修复**：
  - `ReceiverForm.cs`：补齐 `_gpu.SetFrameSource(_dec)`，启动专属合成线程；首帧到达时立即进入全屏；补齐 `ChannelHealthy`；升级 ConfigVersion=6 默认勾选 GPU。
  - `VideoDecoder.cs`：实现并绑定 `_getFormatFunc`（返回 `AV_PIX_FMT_D3D11`）；`_retained` 生命周期统一加 `_hwSync` 保护；纯 CPU 模式不附着硬件上下文。
  - `GpuVideoRenderer.cs`：规范 `CreateProcEnum` 输入/输出分辨率；首帧或分辨率变更时动态调用 `RecreateVideoProcessor()` 重建处理器与视图；交换链使用 `Scaling.Stretch`；对原生纹理包装对象调用 `GC.SuppressFinalize`。
- **状态**：实测 GPU 渲染管线流畅稳定，CPU 占用 <1%，已正式作为全局默认渲染模式。

## 🟢 P2 GPU 模式自动全屏（已确认修复）

- **现象**：GPU 模式连接后画面显示为窗口大小（非全屏），像缩略图。
- **根因**：自动全屏逻辑原本在 `PaintTick` 内，GPU 模式下该函数提前返回，全屏切换永远不执行；
  交换链保持窗口尺寸（982×596），被 DWM 拉伸显示。
- **已实施修复**：
  - 全屏触发提取为 `EnsureFullscreen()`，GPU/CPU 路径统一在首帧到达（`OnFrameReady`）时触发；
  - 进入全屏自动隐藏所有配对/输入设置控件，避免 Direct3D 表面被原生 WinForm 控件遮挡或闪烁。

## 🟡 P3 CPU 模式系统光标不可见（修复待验证）

- **现象**：CPU 模式全屏后看不到鼠标指针，但点击/拖拽实际生效。
- **根因**：低级钩子吞掉鼠标移动事件（转发给 Mac）→ Windows 系统光标不更新位置，
  冻结在原地（视觉上等于"没有光标"）。
- **已提交修复**：移动事件**放行**（`CallNextHookEx`）——系统光标自然移动（它就是光标层），
  同时照常转发 Mac；点击/滚轮仍吞掉转发（防误触 Windows 界面）（`e9c1cc9`/`a1545c3`）；
  全屏时显式 `Cursor.Show()` 兜底。
- **验证方法**：CPU 模式移动鼠标，系统箭头应实时跟随。
- **注意**：点击仍被吞掉转发——点击的"本机响应"只有焦点切换；若需要点击也放行本机，
  见 P4 的失联放行逻辑。

## 🟡 P4 点击偶尔失灵（可靠性加固待验证）

- **现象**：点击操作间歇性无效（移动正常）。
- **根因分析**：控制通道 TCP 瞬断/重连窗口内，点击包被静默丢弃
  （发送时流为空 → 包消失，两端都没收到）；重连最长可达 6 秒。
- **已提交修复**（`16d9e81`）：
  - 通道失联时钩子**放行点击给本机**（不再凭空消失）；
  - 发送失败**立即触发重连**（不再等 6 秒心跳超时）；
  - Mac 端点击事件写入 sender.log（`🖱 点击 btn=x 按下/抬起 @坐标`）。
- **验证方法**：复现失灵时看两端日志——
  Mac sender.log 有「🖱 点击」行 = 点击已到达 Mac（问题在注入环节）；
  无该行 = 控制通道断开（问题在链路）。

## 🟡 P5 GPU 模式页面反应缓慢（重构待验证）

- **现象**：GPU 模式下打开页面/动画明显比 CPU 模式迟缓。
- **根因分析**：解码线程与合成线程争抢同一 D3D 设备上下文（多线程锁串行化一切），
  合成线程 `Present(1)` 阻塞等待垂直同步期间，解码线程的渲染调用被阻塞，
  解码吞吐塌陷 → 整链路帧调度不规律。
- **已提交修复**（`4b6b54f`/`9e27dfa`）：解码线程**零渲染调用**（只保留帧引用）；
  全部渲染收敛到专用合成线程（Present(1) 节拍）；空闲自动休眠。
- **验证方法**：拖动窗口/打开页面对比 CPU 模式；receiver.log 无「合成异常」增长。
- **若仍缓慢**：确认状态栏「渲染 GPU」；尝试 `--codec hevc` 与降低 `-b`；
## 🟢 P6 Mac 端光标在拓展屏上不可见（已修复）

- **现象**：在 Mac 端使用触控板/鼠标移入拓展屏（Windows）时，看不见光标，但点击和拖拽均生效。
- **根因分析**：
  在实现 Opt 4 零拷贝 GPU 采集直通时，为了消除 CPU 内存拷贝和软件光标混合（`stampCursor`），将 `CaptureService.swift` 中的 `cfg.showsCursor` 设为了 `false`。由于 Windows 接收端依赖视频流内的光标画面（且元数据通道未渲染光标），导致 Mac 端的指针进入虚拟拓展屏后未被合成到画面中，造成光标隐形。
- **已实施修复**：
  在 `CaptureService.swift` 中将 `cfg.showsCursor = true`。实测验证 ScreenCaptureKit 在当前系统版本下对 `CGVirtualDisplay` 能够完美原生 GPU 硬件合成光标（包括箭头、文本光标 I-beam、手型指针等），保持全链路纯 GPU 零内存拷贝（`IOSurface` 直通 VideoToolbox），既不增加任何 CPU 开销，又使光标在拓展屏上丝滑可见。
- **验证方法**：
  在 Mac 上将鼠标移入拓展屏，指针应清晰可见，并能跟随手势流畅移动、拖拽和点击。

## 🟢 P7 全屏连接信息遮挡拓展屏画面（已实施优化）

- **现象**：全屏后左上角/顶部的连接诊断信息（分辨率、帧率、渲染模式等）常驻显示，遮挡拓展屏内容。
- **已实施优化**：
  - 将状态栏升级为浮动半透明 OSD 徽章（`Color.FromArgb(190, 24, 24, 28)`），进入全屏 3.5 秒后自动淡出隐藏；
  - 增加快捷键切换：随时按 `F1` 或 `Ctrl+I` 可唤出或手动隐藏 OSD 诊断信息；唤出后 3.5 秒再次自动隐藏。

## 🟢 P8 HiDPI 模式下看门狗误判 5 秒断连死循环（已修复）

- **现象**：开启 HiDPI (2x Retina) 模式后，每隔 5 秒接收端看门狗就触发一次"分辨率不匹配，重连以重建虚拟屏"，造成画面反复闪烁断连。
- **根因分析**：
  看门狗使用 `_dec.Width != _modeW` 比较视频帧尺寸与显示器物理分辨率；但在 HiDPI 模式下，Mac 虚拟屏渲染分辨率为物理分辨率的 2 倍（2x Retina），即 `_dec.Width == _modeW * 2`。看门狗未将 2x Retina 视为合法分辨率，导致误触发重连。
- **已实施修复**：
  修正匹配逻辑：当启用 HiDPI 时，若 `dec.Width == _modeW * 2 && dec.Height == _modeH * 2` 视为完美匹配，看门狗不再误触发重连；状态栏显示 `(Retina 2x)`。

## 🟢 P9 快捷键与全屏切换增强（已实施优化）

- **已实施优化**：
  - 新增 `F11` 键：随时在全屏模式与窗口化模式之间自由切换；
  - 保留 `Esc` 键：全屏时退出全屏，窗口模式下再次按 Esc 安全退出程序；
  - 低级键盘钩子（`InputForwarder`）特权放行 `Esc (0x1B)`、`F1 (0x70)`、`F11 (0x7A)`，确保全屏独占输入时本地管理快捷键不被吞掉。

## 🟢 P10 显示器排列断开重连恢复默认（已修复并支持记忆）

- **现象**：在 macOS「系统设置 → 显示器 → 排列」中调整了虚拟扩展屏的位置（如拖到主屏左侧或上方）后，一旦客户端断开重连或 Mac 暂停恢复，虚拟屏位置自动复位回默认的「主屏正右侧居中」。
- **根因分析**：
  1. `ensureConfigSync` 每次创建或重建虚拟屏时，均无条件调用 `CGConfigureDisplayOrigin` 强行将原点重置为主屏最右侧居中（`Int32(mb.maxX), Int32(mb.midY - db.height / 2)`），抹杀了用户的系统偏好；
  2. macOS 对私有 API 创建的 `CGVirtualDisplay` 分配动态递增的 DisplayID，WindowServer 的本地布局缓存无法自动关联重建后的虚拟屏。
- **已实施修复**：
  - 监听 macOS 系统屏幕参数变更通知（`NSApplication.didChangeScreenParametersNotification`），当用户拖动排列后自动计算并持久化相对主屏的偏移量至 `~/Library/Application Support/MacScreenExtender/layout.json`；
  - 虚拟屏创建与重建时自动读取已保存的相对坐标还原原点；若检测到贴边吸附（左/右/顶/底），自适应新分辨率贴靠对齐，避免因分辨率变更产生缝隙；
  - 菜单栏「高级选项」中新增「重置屏幕排列（恢复默认右侧居中）」选项，方便随时复位；
  - 增加启动纯函数算法自检 `selfCheckLayout`，覆盖无记录回退、左侧吸附、右侧吸附与自定义相对偏移。
- **验证方法**：
  在「系统设置 → 显示器 → 排列」中将虚拟屏拖到左侧，断开重连或菜单栏暂停投屏后再恢复，观察虚拟屏是否依然维持在左侧。

## 🟢 P11 拓展屏字体放大发糊（已通过真实 2x 视网膜超采样彻底解决）

- **现象**：当希望在 Windows 拓展屏上放大字体和窗口时，字体虽然放大了，但依然发糊发虚。
- **根因分析**（底层逆向与 DisplayMode 矩阵实测诊断）：
  1. **旧版 Windows 软拉伸模糊**：过去 Windows 客户端按 `reqW = w / scaleFactor` 请求低分辨率（如 720p），由 Windows GPU 双线性拉伸到 1080p 全屏，造成第一重位图发虚；
  2. **macOS `CGVirtualDisplay` 模式定义混淆导致 Retina 被系统静默降级**（核心根因）：
     - macOS 私有 API `CGVirtualDisplay` 中：
       - `CGVirtualDisplayMode` 的 `width/height` 是系统的**逻辑排版点阵 (point)**（决定 UI 字号与窗口大小）；
       - `CGVirtualDisplayDescriptor` 的 `maxPixelsWide/maxPixelsHigh` 是底层的**物理像素缓冲区 (pixel backing store)**；
     - 此前代码将 2x 后的像素尺寸（如 3412x2132）同时传给了 `mode` 与 `maxPixelsWide`；macOS 认为用户要在一个 3412 点的屏幕上开 2x 视网膜，尝试申请 $6824 \times 4264$ 缓冲区，超出了 Apple Silicon 显示控制器上限；
     - 导致 macOS **静默禁用了 2x 视网膜矢量排版**，回退为 1x 灰阶抗锯齿的 1706x1066 模式；ScreenCaptureKit 采集到的是 1x 低分辨率画面并拉伸输出，导致文字发糊。
- **已实施修复**：
  - **解耦逻辑排版点与物理像素缓冲**：
    - `VirtualDisplay.m` / `VirtualDisplay.h` 扩展签名，明确分离 `logicalWidth/logicalHeight` 与 `backingWidth/backingHeight`；
    - 当缩放比例为 150% 时，`mode` 严格传入逻辑点 $1706 \times 1066$，`descriptor` 传入视网膜像素 $3412 \times 2132$；
    - 配置 PPI 为 220，触发 macOS 原生 Retina 高密度面板识别；
  - **实测验证结果**：
    - 经全分辨率模式矩阵测试（1080p、2.5K 1600p 各档位 100%、125%、150%、175%、200% 全部 PASS）：
      macOS 成功激活真实 2x 视网膜模式（`Current Mode: point=1706x1066, pixel=3412x2132 [Real 2x Retina]`），macOS 系统内所有文字和图标均以 2x 精度进行亚像素矢量抗锯齿渲染；
  - **GPU 硬件级超采样直出**：ScreenCaptureKit 将 $3412 \times 2132$ 视网膜缓冲区硬件高质量下采样至 Windows 物理屏幕分辨率（如 $2560 \times 1600$ 或 $1920 \times 1080$），Windows 1:1 点对点零插值上屏。
- **验证方法**：
  在 Mac 菜单栏选择「显示缩放」→「150%（舒适大字 · 视网膜超采样）」，Windows 扩展屏文字与窗口按比例变大，近距离观察文字边缘圆润、笔画极其锐利清晰，彻底告别模糊。

---

## ⚪ 设计限制（非缺陷，无修复计划）

| 限制 | 说明 |
|---|---|
| `Ctrl+Alt+Del` | 系统保留组合键，任何钩子都无法拦截 |
| `Esc` | 保留给本机退全屏（Mac 侧需要 Esc 时先退全屏再操作） |
| 4:2:0 色度抽样 | H.264/HEVC 编码格式物理上限，彩色小字边缘轻微彩边 |
| 视频流明文 | 配对 token 仅防未配对设备；流内容局域网内可被抓包 |
| Windows 光标样式 | 控制时显示 Windows 箭头（非 Mac 样式）；如需 Mac 样式需恢复自绘（依赖 GPU 管线稳定） |

## 验证清单（一次过完）

1. 连接（GPU 勾选）→ 自动全屏、状态栏「渲染 GPU」、无 ⚠️ —— P1/P2
2. 快速移动鼠标 → 箭头丝滑 —— P3
3. 单击/双击/拖拽图标 → 灵敏 —— P4（异常时抓两端日志）
4. 打开网页/动画 → 流畅度对比 CPU 模式 —— P5
5. 停手 10 秒 → 观察画面保持、CPU/GPU 占用回落
6. Esc → 窗口模式正常、光标可见；重新连接 → 自动回全屏
