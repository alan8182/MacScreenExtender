using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MacScreenReceiver;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ReceiverForm());
    }
}

public class ReceiverForm : Form
{
    private TextBox _ipBox;
    private Button _goButton;
    private Label _status;
    private System.Windows.Forms.Timer _retryTimer;   // 保留：手动重连提示用（未再触发于断流，解码器自愈）
    private string _url;
    private bool _started;
    private System.Net.Sockets.TcpClient _ctl;
    private System.IO.Stream _ctlStream;
    private readonly object _ctlLock = new();
    private int _screenW = 1920, _screenH = 1080;
    private InputForwarder _forwarder;
    private DateTime _lastKeepalive = DateTime.MinValue;
    private DateTime _lastFrameStamp = DateTime.MinValue;
    private long _lastFrameCount;
    private readonly byte[] _readBuf = new byte[256];
    private string _ip;
    private System.Windows.Forms.Timer _watchdog;
    private System.Windows.Forms.Timer _paint;
    private VideoDecoder _dec;
    private bool _autoFullscreenDone;   // 首帧后自动全屏只做一次，Esc 退出后不再抢
    private byte[] _cursorSprite;       // 本地光标精灵（混入解码帧缓冲，单次上屏无闪烁）
    private const int CursorW = 30, CursorH = 36;
    private int _modeW, _modeH, _modeHz;   // 本机显示器模式（诊断分辨率/刷新率匹配用）
    private DateTime _mismatchSince = DateTime.MinValue;
    private int _mismatchAttempts;
    private ComboBox _discover;
    private readonly System.Collections.Generic.List<Discovered> _discovered = new();
    private ReceiverConfig _config = new();
    private IntPtr _oldImc = IntPtr.Zero;
    private int _inputLatencyMs = -1;   // 输入回路延迟 EWMA（-1 = 未知）
    private DateTime _noFrameSince = DateTime.MinValue;   // 零画面持续起点（配对自愈用）
    private GpuVideoRenderer? _gpu;

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacScreenExtender");

    public ReceiverForm()
    {
        Text = "Mac 扩展屏接收端 v5（自动发现+配对）";
        ClientSize = new Size(960, 540);
        FormBorderStyle = FormBorderStyle.Sizable;   // 可自由缩放；首帧到达后自动进全屏
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = false;   // 画面由 PaintTick 直绘 DC，框架缓冲绘制反而引入 GDI+ 冲突

        var label = new Label { Text = "Mac 的 IP 地址：", Left = 20, Top = 20, AutoSize = true };
        _ipBox = new TextBox { Left = 20, Top = 45, Width = 300 };
        _goButton = new Button { Text = "连接", Left = 335, Top = 43, Width = 90 };
        _status = new Label { Left = 20, Top = 125, Width = 430, AutoSize = true, ForeColor = Color.DimGray };
        var discLabel = new Label { Text = "发现:", Left = 20, Top = 78, AutoSize = true };
        _discover = new ComboBox { Left = 70, Top = 74, Width = 355, DropDownStyle = ComboBoxStyle.DropDownList };
        _discover.SelectedIndexChanged += (_, _) =>
        {
            if (_discover.SelectedItem is Discovered d) _ipBox.Text = d.Ip;
        };
        var hint = new Label
        {
            Left = 20, Top = 160, Width = 430, AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "局域网内的 Mac 会自动出现在上面（需 Mac 端运行中）。\r\n首次连接输入 Mac 菜单栏显示的 4 位配对码。\r\n连接成功后进入全屏；全屏中按键直接控制 Mac，Esc 退出全屏。"
        };
        Controls.AddRange(new Control[] { label, _ipBox, _goButton, discLabel, _discover, _status, hint });

        LoadConfig();
        StartDiscovery();

        _goButton.Click += (_, _) => Start();
        _ipBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Start(); };

        _retryTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); StartDecoder(); };

        // 绘制：帧到即画（FrameReady 事件驱动）。定时器仅作兜底（窗口暴露/缩放后补绘），
        // 不能高频——它不看有没有新帧，高频会把静止画面反复 blit 白烧 GPU/内存
        _paint = new System.Windows.Forms.Timer { Interval = 200 };
        _paint.Tick += (_, _) => PaintTick();
        _paint.Start();

        // 链路 watchdog：1) 6 秒没心跳 → 重建控制通道；2) 8 秒无新解码帧 → 重建解码器
        _watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        _watchdog.Tick += (_, _) =>
        {
            if (!_started || FormBorderStyle != FormBorderStyle.None) return;
            if ((DateTime.UtcNow - _lastKeepalive).TotalSeconds > 6)
            {
                _lastKeepalive = DateTime.UtcNow;   // 防止连发
                _status.Text = "控制通道中断，正在恢复…";
                if (!string.IsNullOrEmpty(_ip)) ConnectCtl(_ip);
            }
            var fc = _dec?.FrameCount ?? 0;
            if (fc == _lastFrameCount)
            {
                if (_lastFrameStamp != DateTime.MinValue && (DateTime.UtcNow - _lastFrameStamp).TotalSeconds > 8)
                {
                    _lastFrameStamp = DateTime.UtcNow;   // 防止连发
                    _status.Text = "画面停滞，重启解码…";
                    StartDecoder();
                }
                // 30 秒零画面 → 探测配对状态：Mac 端重置配对/换码后自动重新配对
                if (fc == 0 && _started && !string.IsNullOrEmpty(_ip))
                {
                    if (_noFrameSince == DateTime.MinValue) _noFrameSince = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - _noFrameSince).TotalSeconds > 30)
                    {
                        _noFrameSince = DateTime.UtcNow;
                        HttpGet(_ip, $"/pair-check?token={Uri.EscapeDataString(_config.Token)}", out int st);
                        if (st == 403)
                        {
                            Log.Write("服务端 token 已失效，自动重新配对");
                            _config.Token = "";
                            SaveConfig();
                            using var dlg = new PairDialog();
                            if (dlg.ShowDialog(this) == DialogResult.OK)
                            {
                                var resp = HttpGet(_ip, $"/pair?code={Uri.EscapeDataString(dlg.Code)}", out int pst);
                                if (pst == 200) { _config.Token = resp.Trim(); SaveConfig(); Log.Write("重新配对成功"); }
                                else Log.Write($"重新配对失败（{pst}）");
                            }
                            StartDecoder();
                        }
                    }
                }
            }
            else
            {
                _lastFrameCount = fc;
                _lastFrameStamp = DateTime.UtcNow;
            }

            // 流分辨率与请求的不一致持续 5 秒 → 重连（触发 Mac 端重建虚拟屏到本机分辨率）
            if (_modeW > 0 && _dec != null && _dec.Width > 0 &&
                (_dec.Width != _modeW || _dec.Height != _modeH))
            {
                if (_mismatchSince == DateTime.MinValue) _mismatchSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - _mismatchSince).TotalSeconds > 5)
                {
                    _mismatchSince = DateTime.UtcNow;   // 防止连发
                    _mismatchAttempts++;
                    _status.Text = _mismatchAttempts > 3
                        ? "分辨率重建失败：请重启 Mac 端发送程序"
                        : "分辨率不匹配，重连以重建虚拟屏…";
                    StartDecoder();
                }
            }
            else
            {
                _mismatchSince = DateTime.MinValue;
                _mismatchAttempts = 0;
            }

            UpdateDiagStatus();
        };
        _watchdog.Start();
        _cursorSprite = BuildCursorSprite();
    }

    /// 状态栏诊断：视频源分辨率 vs 本机显示器设置（不一致=有拉伸/黑边风险的当场可见）
    void UpdateDiagStatus()
    {
        if (!_started || FormBorderStyle != FormBorderStyle.None) return;
        var dec = _dec;
        if (dec == null || dec.Width == 0 || _modeW == 0) return;
        string txt = $"已连接 视频{dec.Width}×{dec.Height} | 显示器{_modeW}×{_modeH}@{_modeHz}Hz";
        if (dec.Width != _modeW || dec.Height != _modeH) txt += " ⚠️不一致";
        txt += _gpu != null && !dec.GpuFailed ? " | 渲染 GPU" : " | 渲染 CPU";
        var lat = Interlocked.CompareExchange(ref _inputLatencyMs, 0, 0);
        if (lat >= 0) txt += $" | 输入 ~{lat}ms";
        if (_status.Text != txt) _status.Text = txt;
    }

    /// 预渲染黑底白边箭头光标（BGRA+alpha，tip 在左上）
    static byte[] BuildCursorSprite()
    {
        using var bmp = new Bitmap(CursorW, CursorH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const float s = 1.5f;
            var pts = new PointF[]
            {
                new(0.5f * s, 0.5f * s), new(0.5f * s, 19.5f * s), new(5.0f * s, 15.5f * s),
                new(8.5f * s, 22.5f * s), new(11.5f * s, 21.0f * s), new(8.0f * s, 14.5f * s),
                new(13.5f * s, 14.5f * s)
            };
            g.FillPolygon(Brushes.Black, pts);
            using var pen = new Pen(Color.White, 2.2f);
            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            g.DrawPolygon(pen, pts);
        }
        var data = new byte[CursorW * CursorH * 4];
        var rect = new Rectangle(0, 0, CursorW, CursorH);
        var bits = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(bits.Scan0, data, 0, data.Length);
        bmp.UnlockBits(bits);
        return data;
    }

    void Start()
    {
        var ip = _ipBox.Text.Trim();
        if (string.IsNullOrEmpty(ip)) { _status.Text = "请输入 Mac 的 IP"; return; }

        // 配对：首次连接需输入 Mac 菜单栏显示的 4 位配对码
        _status.Text = "检查配对…";
        var probe = HttpGet(ip, $"/pair-check?token={Uri.EscapeDataString(_config.Token)}", out int st);
        if (st != 200)
        {
            if (st != 403) { _status.Text = $"无法连接 Mac（{st}）"; return; }
            using var dlg = new PairDialog();
            if (dlg.ShowDialog(this) != DialogResult.OK) { _status.Text = "已取消"; return; }
            _status.Text = "配对中…";
            var resp = HttpGet(ip, $"/pair?code={Uri.EscapeDataString(dlg.Code)}", out int pst);
            if (pst != 200)
            { _status.Text = pst == 403 ? "配对码不正确" : $"配对失败（{pst}）"; return; }
            _config.Token = resp.Trim();
            _status.Text = "配对成功";
        }
        _config.Ip = ip;
        SaveConfig();

        // EnumDisplaySettings 返回屏幕真实物理分辨率，不受 DPI 缩放影响
        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };
        int w, h;
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode) && devMode.dmPelsWidth > 0)
        {
            w = (int)devMode.dmPelsWidth; h = (int)devMode.dmPelsHeight;
            _modeHz = (int)devMode.dmDisplayFrequency;
        }
        else
        {
            var b = Screen.PrimaryScreen.Bounds;
            w = b.Width; h = b.Height;
        }
        _modeW = w; _modeH = h;
        // 裸流（h264/hevc 自动探测）。分辨率参数必须带上：Mac 端按它重建虚拟屏
        _url = $"http://{ip}:8080/raw?w={w}&h={h}&token={Uri.EscapeDataString(_config.Token)}";
        _screenW = w; _screenH = h;
        _ip = ip;
        _lastKeepalive = DateTime.UtcNow;
        _autoFullscreenDone = false;   // 新连接允许再次自动全屏；watchdog 重启解码不重置
        _started = true;
        ConnectCtl(ip);
        StartDecoder();
    }

    // ---- 解码与渲染 ----

    void StartDecoder()
    {
        if (!_started) return;
        _status.Text = "连接中 " + _url;
        _dec?.Dispose();
        _dec = null;
        _gpu?.Dispose(); _gpu = null;
        try
        {
            _gpu = new GpuVideoRenderer(Log.Write);
            if (_gpu.Initialize(Handle, Math.Max(1, ClientRectangle.Width), Math.Max(1, ClientRectangle.Height)))
            {
                _gpu.SetCursorSprite(_cursorSprite, CursorW, CursorH);
                Log.Write("GPU 渲染路径启用");
            }
            else
            {
                _gpu.Dispose(); _gpu = null;
                Log.Write("GPU 初始化失败，使用 CPU 渲染路径");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"GPU 初始化异常: {ex.Message}");
            _gpu?.Dispose(); _gpu = null;
        }

        _dec = new VideoDecoder(_url);
        _dec.SetCursorSprite(_cursorSprite, CursorW, CursorH);
        if (_gpu != null)
        {
            _dec.Renderer = _gpu;
            _dec.CreateDecoderHwRef = _gpu.CreateDecoderHwRef;
        }
        _dec.FrameReady = OnFrameReady;   // 帧到即画（合并排队），消灭轮询节拍差
        _noFrameSince = DateTime.MinValue;
        _dec.Start();
    }

    private volatile bool _paintPending;
    private void OnFrameReady()
    {
        if (_dec is { IsGpuRendering: true }) return;   // GPU 已在解码线程上屏
        if (_paintPending || !IsHandleCreated) return;
        _paintPending = true;
        try { BeginInvoke(new Action(() => { _paintPending = false; PaintTick(); })); }
        catch { _paintPending = false; }
    }

    void PaintTick()
    {
        try
        {
            DoPaintTick();
        }
        catch { /* 绘制帧的偶发 GDI 冲突直接跳过本帧，绝不弹窗 */ }
    }

    void DoPaintTick()
    {
        var dec = _dec;
        if (dec == null) return;
        if (dec.FrameCount > 0)
        {
            var wasWindowed = FormBorderStyle != FormBorderStyle.None;
            if (!_autoFullscreenDone)
            {
                // 首帧到达即进全屏（一次）；跟随窗口当前所在的那块屏
                _status.Text = "已连接";
                _autoFullscreenDone = true;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                _oldImc = ImmAssociateContext(Handle, IntPtr.Zero);   // 禁本机 IME，输入全交 Mac
                _forwarder?.Dispose();
                _forwarder = new InputForwarder(ControlActive, SendPacket, _screenW, _screenH);
                Cursor.Hide();
            }
            if (wasWindowed && _status.Text != "已连接") _status.Text = "已连接";
        }
        if (dec.FrameCount == 0) return;
        if (dec.IsGpuRendering) return;   // 画面已由 GPU 渲染器直出
        lock (dec.Sync)
        {
            if (dec.Bgra == IntPtr.Zero || dec.Width == 0) return;
            using var g = CreateGraphics();
            IntPtr hdc = g.GetHdc();
            try
            {
                var rc = ClientRectangle;
                // 永远等比缩放居中：比例一致时恰好铺满；不一致时出黑边而不是拉伸变形。
                // 关键：PatBlt 只扫"黑边条"，绝不覆盖内容区——直绘屏幕 DC 的每笔 GDI 都会
                // 立即上屏，若整屏刷黑再画视频，每帧都会闪一帧黑
                double s = Math.Min((double)rc.Width / dec.Width, (double)rc.Height / dec.Height);
                int dw = Math.Max(1, (int)(dec.Width * s));
                int dh = Math.Max(1, (int)(dec.Height * s));
                int dx = (rc.Width - dw) / 2;
                int dy = (rc.Height - dh) / 2;
                if (dx > 0) PatBlt(hdc, 0, 0, dx, rc.Height, 0x00000042 /*BLACKNESS*/);
                if (rc.Width - dx - dw > 0) PatBlt(hdc, dx + dw, 0, rc.Width - dx - dw, rc.Height, 0x00000042);
                if (dy > 0) PatBlt(hdc, dx, 0, dw, dy, 0x00000042);
                if (rc.Height - dy - dh > 0) PatBlt(hdc, dx, dy + dh, dw, rc.Height - dy - dh, 0x00000042);
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = dec.Width;
                bmi.bmiHeader.biHeight = -dec.Height;   // 负高 = 自上而下行序
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                StretchDIBits(hdc, dx, dy, dw, dh, 0, 0, dec.Width, dec.Height,
                              dec.Bgra, ref bmi, 0, 0x00CC0020 /*SRCCOPY*/);
            }
            finally { g.ReleaseHdc(hdc); }
        }
    }

    // ---- 控制通道（TCP 8081）----

    void ConnectCtl(string ip)
    {
        Task.Run(() =>
        {
            try
            {
                var c = new System.Net.Sockets.TcpClient();
                c.NoDelay = true;
                c.Connect(ip, 8081);
                lock (_ctlLock) { _ctl?.Close(); _ctl = c; _ctlStream = c.GetStream(); }
                _lastKeepalive = DateTime.UtcNow;
                ReadCtlLoop(c);
            }
            catch { lock (_ctlLock) { _ctlStream = null; } }
        });
    }

    /// 读 Mac 端心跳与光标回报（16 字节定长包流，按帧切分解析）
    void ReadCtlLoop(System.Net.Sockets.TcpClient c)
    {
        try
        {
            var s = c.GetStream();
            var buf = new byte[1024];
            var acc = new byte[1024];
            int accLen = 0;
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                _lastKeepalive = DateTime.UtcNow;
                if (accLen + n > acc.Length) Array.Resize(ref acc, accLen + n);
                Buffer.BlockCopy(buf, 0, acc, accLen, n);
                accLen += n;
                int off = 0;
                while (accLen - off >= 16)
                {
                    HandleCtlPacket(acc, off);
                    off += 16;
                }
                if (off > 0) { Buffer.BlockCopy(acc, off, acc, 0, accLen - off); accLen -= off; }
            }
        }
        catch { }
        lock (_ctlLock) { if (_ctl == c) _ctlStream = null; }
        _dec?.UpdateCursor(0, 0, false);   // 控制链路断了，本地光标隐藏（视频内回退光标由 Mac 端恢复）
    }

    void HandleCtlPacket(byte[] p, int off)
    {
        if (p[off] != 8) return;   // 7=心跳（已在读循环刷新 keepalive）
        _dec?.UpdateCursor(BitConverter.ToSingle(p, off + 4),
                           BitConverter.ToSingle(p, off + 8),
                           p[off + 12] != 0);

        // 输入回路延迟估算：本次光标回报反映的是最后一次注入的移动
        var fw = _forwarder;
        if (fw != null)
        {
            var sent = fw.LastMoveSentTicks;
            if (sent > 0)
            {
                var ms = (DateTime.UtcNow.Ticks - sent) / TimeSpan.TicksPerMillisecond;
                if (ms > 0 && ms < 2000)
                {
                    var old = Interlocked.CompareExchange(ref _inputLatencyMs, 0, 0);
                    var next = old < 0 ? (int)ms : (old * 7 + (int)ms) / 8;
                    Interlocked.Exchange(ref _inputLatencyMs, next);
                }
            }
        }
    }

    void SendPacket(byte[] p)
    {
        lock (_ctlLock)
        {
            try { _ctlStream?.Write(p, 0, p.Length); } catch { _ctlStream = null; }
        }
    }

    bool ControlActive()
    {
        // 全屏 && 前台窗口是本窗体（GDI 直绘不再产生 libvlc 子窗口）
        return FormBorderStyle == FormBorderStyle.None
            && NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GA_ROOT) == Handle;
    }

    // 键盘转发已由 InputForwarder 的 WH_KEYBOARD_LL 低级钩子统一处理
    // （down/up 成对、VK 码直传、系统组合键一并接管），窗体层不再参与

    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, int rop);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr context);

    [DllImport("gdi32.dll")]
    private static extern int StretchDIBits(IntPtr hdc, int xDst, int yDst, int wDst, int hDst,
        int xSrc, int ySrc, int wSrc, int hSrc, IntPtr lpBits, ref BITMAPINFO bmi, int iUsage, int rop);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 全屏时系统级重绘用黑色擦底（避免灰白闪块）；窗口模式走默认
        if (FormBorderStyle == FormBorderStyle.None) e.Graphics.Clear(Color.Black);
        else base.OnPaintBackground(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _gpu?.RequestResize(Math.Max(1, ClientRectangle.Width), Math.Max(1, ClientRectangle.Height));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (FormBorderStyle == FormBorderStyle.None)
            {
                _forwarder?.Dispose(); _forwarder = null;
                if (_oldImc != IntPtr.Zero) ImmAssociateContext(Handle, _oldImc);
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
                Cursor.Show();
            }
            else
            {
                Close();
            }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- 自动发现与配对 ----

    internal sealed record Discovered(string Ip, string Name)
    {
        public override string ToString() => $"{Name}（{Ip}）";
    }

    internal sealed class ReceiverConfig
    {
        public string Ip { get; set; } = "";
        public string Token { get; set; } = "";
    }

    internal sealed class PairDialog : Form
    {
        public string Code => _box.Text.Trim();
        private readonly TextBox _box;

        public PairDialog()
        {
            Text = "配对 Mac";
            Width = 330; Height = 160;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            var label = new Label { Text = "输入 Mac 菜单栏显示的 4 位配对码：", Left = 15, Top = 15, AutoSize = true };
            _box = new TextBox { Left = 15, Top = 45, Width = 270 };
            _box.KeyPress += (_, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            var ok = new Button { Text = "配对", Left = 15, Top = 80, Width = 125, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Left = 160, Top = 80, Width = 125, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { label, _box, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    void StartDiscovery()
    {
        Task.Run(() =>
        {
            try
            {
                using var udp = new UdpClient(8082);
                while (true)
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    var data = udp.Receive(ref ep);
                    string ip, name;
                    try
                    {
                        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(data));
                        ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
                        name = doc.RootElement.GetProperty("name").GetString() ?? "Mac";
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(ip)) continue;
                    try { BeginInvoke(new Action(() =>
                    {
                        foreach (var d in _discovered) if (d.Ip == ip) return;
                        var item = new Discovered(ip, name);
                        _discovered.Add(item);
                        _discover.Items.Add(item);
                        if (_discover.Items.Count == 1) _discover.SelectedIndex = 0;
                    })); } catch { }
                }
            }
            catch { }
        });
    }

    static string HttpGet(string ip, string path, out int status)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create($"http://{ip}:8080{path}");
            req.Timeout = 3000; req.ReadWriteTimeout = 3000; req.Proxy = null;
            using var resp = (HttpWebResponse)req.GetResponse();
            status = (int)resp.StatusCode;
            using var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch (WebException ex)
        {
            status = ex.Response is HttpWebResponse r ? (int)r.StatusCode : 0;
            return "";
        }
        catch { status = 0; return ""; }
    }

    void LoadConfig()
    {
        try
        {
            var path = Path.Combine(ConfigDir, "config.json");
            if (File.Exists(path))
                _config = JsonSerializer.Deserialize<ReceiverConfig>(File.ReadAllText(path)) ?? new ReceiverConfig();
            else if (File.Exists(Path.Combine(ConfigDir, "mac_ip.txt")))
                _config.Ip = File.ReadAllText(Path.Combine(ConfigDir, "mac_ip.txt")).Trim();   // 兼容旧版
        }
        catch { }
        _ipBox.Text = _config.Ip;
    }

    void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(Path.Combine(ConfigDir, "config.json"),
                              JsonSerializer.Serialize(_config));
        }
        catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _paint?.Stop();
        _watchdog?.Stop();
        _forwarder?.Dispose();
        _retryTimer?.Stop();
        _dec?.Dispose();
        _gpu?.Dispose(); _gpu = null;
        base.OnFormClosed(e);
    }
}


/// 极简滚动日志（远程迭代用：%APPDATA%\MacScreenExtender\receiver.log）
internal static class Log
{
    private static readonly object _lock = new();
    private static readonly string LogPath = Path.Combine(ConfigDirStatic, "receiver.log");

    internal static string ConfigDirStatic =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacScreenExtender");

    public static void Write(string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024) File.WriteAllText(LogPath, "");
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
            }
        }
        catch { }
    }
}
