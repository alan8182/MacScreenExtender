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
        CrashGuard.Install();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            Log.Write($"UI线程异常: {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"⚠️ 未处理异常(进程退出): {e.ExceptionObject.GetType().Name}: {e.ExceptionObject}");
        Application.Run(new ReceiverForm());
    }
}

sealed class ScaleOption
{
    public string Label { get; }
    public int Percent { get; }
    public ScaleOption(string label, int percent) { Label = label; Percent = percent; }
    public override string ToString() => Label;
}

internal class CardPanel : Panel
{
    public int CornerRadius { get; set; } = 12;
    public Color BorderColor { get; set; } = Color.FromArgb(228, 231, 235);

    public CardPanel()
    {
        BackColor = Color.White;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 先用父容器底色擦底，确保圆角外切四角完全与父背景一致
        Color parentBg = Parent?.BackColor ?? Color.FromArgb(245, 245, 247);
        using var bgBrush = new SolidBrush(parentBg);
        e.Graphics.FillRectangle(bgBrush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Math.Max(8, (int)Math.Round(CornerRadius * (DeviceDpi / 96f)));
        using var path = CreateRoundedRect(r, radius);
        using (var cardBrush = new SolidBrush(BackColor))
        {
            g.FillPath(cardBrush, path);
        }
        using var pen = new Pen(BorderColor, Math.Max(1f, 1.2f * (DeviceDpi / 96f)));
        g.DrawPath(pen, path);
    }

    public static System.Drawing.Drawing2D.GraphicsPath CreateRoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal class ModernButton : Button
{
    public bool IsPrimary { get; set; } = true;
    public int CornerRadius { get; set; } = 8;
    private bool _hovered;
    private bool _pressed;

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { _pressed = true; Invalidate(); base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { _pressed = false; Invalidate(); base.OnMouseUp(mevent); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Math.Max(6, (int)Math.Round(CornerRadius * (DeviceDpi / 96f)));
        using var path = CardPanel.CreateRoundedRect(r, radius);

        Color bg;
        Color fg;
        if (!Enabled)
        {
            bg = Color.FromArgb(235, 238, 242);
            fg = Color.FromArgb(156, 163, 175);
        }
        else if (IsPrimary)
        {
            bg = _pressed ? Color.FromArgb(0, 91, 181) : (_hovered ? Color.FromArgb(0, 119, 237) : Color.FromArgb(0, 113, 227));
            fg = Color.White;
        }
        else
        {
            bg = _pressed ? Color.FromArgb(225, 228, 234) : (_hovered ? Color.FromArgb(238, 240, 244) : Color.FromArgb(245, 247, 250));
            fg = Color.FromArgb(55, 65, 81);
        }

        using (var brush = new SolidBrush(bg)) g.FillPath(brush, path);
        if (!IsPrimary && Enabled)
        {
            using var pen = new Pen(Color.FromArgb(220, 223, 228), 1f);
            g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, flags);
    }
}

internal class DeviceIconBadge : Label
{
    public DeviceIconBadge()
    {
        Size = new Size(46, 46);
        Font = new Font("Segoe UI Emoji", 20f);
        Text = "💻";
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Math.Max(6, (int)Math.Round(10f * (DeviceDpi / 96f)));
        using var path = CardPanel.CreateRoundedRect(r, radius);
        using var brush = new SolidBrush(Color.FromArgb(243, 246, 250));
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(226, 232, 240), 1f);
        e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(30, 41, 59),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

internal class KbdBadge : Label
{
    public KbdBadge(string key, string desc)
    {
        Text = $"{key}  {desc}";
        AutoSize = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        ForeColor = Color.FromArgb(71, 85, 105);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = Math.Max(6, (int)Math.Round(8f * (DeviceDpi / 96f)));
        using var path = CardPanel.CreateRoundedRect(r, radius);
        using var brush = new SolidBrush(Color.White);
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(226, 232, 240), 1f);
        e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

public class ReceiverForm : Form
{
    private TextBox _ipBox;
    private ModernButton _goButton;
    private Label _status;
    private System.Windows.Forms.Timer _retryTimer;   // 保留：手动重连提示用（未再触发于断流，解码器自愈）
    private string _url;
    private bool _started;
    private System.Net.Sockets.TcpClient _ctl;
    private System.IO.Stream _ctlStream;
    private readonly object _ctlLock = new();
    private int _screenW = 1920, _screenH = 1080;
    private int _reqW = 0, _reqH = 0;
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
    private ComboBox _scaleCombo;
    private CheckBox _gpuCheck;   // GPU 硬件加速
    private CheckBox _hiDpiCheck; // HiDPI 2x 视网膜清晰度
    private readonly System.Collections.Generic.List<Discovered> _discovered = new();
    private ReceiverConfig _config = new();
    private IntPtr _oldImc = IntPtr.Zero;
    private int _inputLatencyMs = -1;   // 输入回路延迟 EWMA（-1 = 未知）
    private DateTime _noFrameSince = DateTime.MinValue;   // 零画面持续起点（配对自愈用）
    private GpuVideoRenderer? _gpu;
    private System.Windows.Forms.Timer _osdTimer;
    private Control[] _setupControls = Array.Empty<Control>();

    // 现代浅色卡片式 UI 元素（整体背景大卡片容器）
    private Panel _setupPanel;
    private Label _headerLabel;
    private Label _beaconLabel;
    private CardPanel _mainCard;
    private Panel _deviceCard;
    private DeviceIconBadge _devIcon;
    private Label _devNameLabel;
    private Label _devSubLabel;
    private Panel _divider1;
    private Panel _qualitySection;
    private Label _cardTitle;
    private Label _scaleLbl;
    private Panel _divider2;
    private Label _manualToggle;
    private Panel _manualPanel;
    private Label _manualIpLbl;
    private ModernButton _manualGoBtn;
    private Panel _kbdPanel;
    private KbdBadge _badgeEsc;
    private KbdBadge _badgeF11;
    private KbdBadge _badgeF1;
    private Label _osd;

    private static string ConfigDir =>
Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacScreenExtender");

    // ponytail: WinForms pt-font scales with DPI, but pixel sizes do not. S(px) ensures all bounds scale linearly with DeviceDpi.
    private float DpiScale => Math.Max(1.0f, (float)DeviceDpi / 96.0f);
    private int S(int px) => (int)Math.Round(px * DpiScale);

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    public ReceiverForm()
    {
        Text = "Mac 随航拓展屏（MacScreenReceiver）";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 245, 247);
        Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        DoubleBuffered = false;

        // 根设置面板：提供整体实心浅灰背景 #F5F5F7，全屏时整体隐藏，退出全屏时整体呈现
        _setupPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(245, 245, 247)
        };

        // 1. 顶部 Branding 与状态灯
        _headerLabel = new Label
        {
            Text = "Mac 随航无线拓展屏",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(29, 29, 31)
        };
        _beaconLabel = new Label
        {
            Text = "● 局域网搜寻中…",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(217, 119, 6)
        };

        // 2. 整体设置大卡片（统揽设备发现、画质设置、手动IP，彻底消灭割裂感）
        _mainCard = new CardPanel
        {
            CornerRadius = 14,
            BorderColor = Color.FromArgb(226, 232, 240),
            BackColor = Color.White
        };

        // 2.1 设备发现区域
        _deviceCard = new Panel
        {
            BackColor = Color.White
        };
        _devIcon = new DeviceIconBadge();
        _devNameLabel = new Label
        {
            Text = "正在搜寻局域网中的 Mac…",
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(29, 29, 31),
            AutoEllipsis = true
        };
        _devSubLabel = new Label
        {
            Text = "请确保 Mac 端已打开 MacScreenSender",
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoEllipsis = true
        };
        _discover = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Microsoft YaHei UI", 9f),
            Visible = false
        };
        _discover.SelectedIndexChanged += (_, _) =>
        {
            if (_discover.SelectedItem is Discovered d)
            {
                UpdateDiscoveredHero(d);
            }
        };

        _goButton = new ModernButton
        {
            Text = "连接",
            CornerRadius = 8,
            IsPrimary = true
        };
        _goButton.Click += (_, _) => Start();

        _deviceCard.Controls.AddRange(new Control[] { _devIcon, _devNameLabel, _devSubLabel, _discover, _goButton });

        // 2.2 分隔线 1
        _divider1 = new Panel
        {
            BackColor = Color.FromArgb(241, 245, 249)
        };

        // 2.3 显示与画质配置区域
        _qualitySection = new Panel
        {
            BackColor = Color.White
        };
        _cardTitle = new Label
        {
            Text = "显示与画质设置",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(29, 29, 31)
        };

        _scaleLbl = new Label
        {
            Text = "显示缩放:",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(55, 65, 81)
        };
        _scaleCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        _scaleCombo.Items.AddRange(new object[]
        {
            new ScaleOption("100% · 点对点直出 (原生满血最清晰 · 默认)", 100),
            new ScaleOption("125% · 轻度放大", 125),
            new ScaleOption("150% · 舒适放大", 150),
            new ScaleOption("175% · 大字模式", 175),
            new ScaleOption("200% · 超清视网膜 2x (建议大屏/高分屏)", 200),
            new ScaleOption("自动匹配本机屏幕缩放", 0)
        });
        _scaleCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_scaleCombo.SelectedItem is ScaleOption opt)
            {
                _config.ScalePercent = opt.Percent;
                SaveConfig();
                if (_started) Start();
            }
        };

        _gpuCheck = new CheckBox
        {
            Text = "GPU 硬件加速 (Direct3D 11 · 极速低延迟 · 推荐开启)",
            AutoSize = true,
            Checked = true,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(55, 65, 81)
        };
        _gpuCheck.CheckedChanged += (_, _) =>
        {
            _config.Gpu = _gpuCheck.Checked;
            SaveConfig();
            _autoFullscreenDone = false;
            if (_started) StartDecoder();
        };

        _hiDpiCheck = new CheckBox
        {
            Text = "超清视网膜渲染 (HiDPI 2x · 建议 1080p 屏开启)",
            AutoSize = true,
            Checked = false,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(55, 65, 81)
        };
        _hiDpiCheck.CheckedChanged += (_, _) =>
        {
            _config.HiDpi = _hiDpiCheck.Checked;
            SaveConfig();
            if (_started) Start();
        };

        _qualitySection.Controls.AddRange(new Control[] { _cardTitle, _scaleLbl, _scaleCombo, _gpuCheck, _hiDpiCheck });

        // 2.4 分隔线 2
        _divider2 = new Panel
        {
            BackColor = Color.FromArgb(241, 245, 249)
        };

        // 2.5 手动 IP 输入展开区
        _manualToggle = new Label
        {
            Text = "⚙ 手动指定 Mac IP 地址 (跨网段 / VPN 场景)",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(0, 113, 227),
            Cursor = Cursors.Hand
        };
        _manualPanel = new Panel { Visible = false, BackColor = Color.White };
        _manualIpLbl = new Label
        {
            Text = "Mac IP:",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(55, 65, 81)
        };
        _ipBox = new TextBox
        {
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        _ipBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Start(); };
        _manualGoBtn = new ModernButton
        {
            Text = "直连",
            CornerRadius = 7,
            IsPrimary = false
        };
        _manualGoBtn.Click += (_, _) => Start();
        _manualPanel.Controls.AddRange(new Control[] { _manualIpLbl, _ipBox, _manualGoBtn });

        _manualToggle.Click += (_, _) =>
        {
            _manualPanel.Visible = !_manualPanel.Visible;
            _manualToggle.Text = _manualPanel.Visible
                ? "▼ 收起手动 IP 设置"
                : "⚙ 手动指定 Mac IP 地址 (跨网段 / VPN 场景)";
            Relayout();
        };

        _mainCard.Controls.AddRange(new Control[] { _deviceCard, _divider1, _qualitySection, _divider2, _manualToggle, _manualPanel });

        // 3. 状态栏与操作快捷键指南
        _status = new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(107, 114, 128)
        };

        _kbdPanel = new Panel();
        _badgeEsc = new KbdBadge("Esc", "退出全屏");
        _badgeF11 = new KbdBadge("F11", "切换全屏");
        _badgeF1 = new KbdBadge("F1", "显隐诊断");
        _kbdPanel.Controls.AddRange(new Control[] { _badgeEsc, _badgeF11, _badgeF1 });

        _setupPanel.Controls.AddRange(new Control[] { _headerLabel, _beaconLabel, _mainCard, _status, _kbdPanel });

        // 4. 全屏独立 OSD 标签（挂在 Form 根层，全屏时浮层呈现）
        _osd = new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f),
            BackColor = Color.FromArgb(190, 24, 24, 28),
            ForeColor = Color.White,
            Padding = new Padding(12, 7, 12, 7),
            Left = 20, Top = 20,
            Visible = false
        };

        _setupControls = new Control[] { _setupPanel };
        Controls.AddRange(new Control[] { _setupPanel, _osd });

        Relayout();

        _osdTimer = new System.Windows.Forms.Timer { Interval = 3500 };
        _osdTimer.Tick += (_, _) =>
        {
            _osdTimer.Stop();
            if (FormBorderStyle == FormBorderStyle.None && _osd != null) _osd.Visible = false;
        };

        LoadConfig();
        StartDiscovery();

        _retryTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); StartDecoder(); };

        _paint = new System.Windows.Forms.Timer { Interval = 200 };
        _paint.Tick += (_, _) => PaintTick();
        _paint.Start();

        _watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
        _watchdog.Tick += (_, _) =>
        {
            if (!_started) return;
            if (_dec is { FrameCount: > 0 } && !_autoFullscreenDone) EnsureFullscreen();
            if (FormBorderStyle != FormBorderStyle.None) return;
            if ((DateTime.UtcNow - _lastKeepalive).TotalSeconds > 6)
            {
                _lastKeepalive = DateTime.UtcNow;
                _status.Text = "控制通道中断，正在恢复…";
                _status.Visible = true;
                if (!string.IsNullOrEmpty(_ip)) ConnectCtl(_ip);
            }
            var fc = _dec?.FrameCount ?? 0;
            if (fc == _lastFrameCount)
            {
                if (_lastFrameStamp != DateTime.MinValue && (DateTime.UtcNow - _lastFrameStamp).TotalSeconds > 8)
                {
                    _lastFrameStamp = DateTime.UtcNow;
                    _status.Text = "画面停滞，重启解码…";
                    _status.Visible = true;
                    if (string.IsNullOrEmpty(_config.Token) && !string.IsNullOrEmpty(_ip))
                    {
                        HttpGet(_ip, $"/pair-check?token={Uri.EscapeDataString(_config.Token)}", out int st);
                        if (st == 403)
                        {
                            _config.Token = "";
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

            int targetW = _reqW > 0 ? _reqW : _modeW;
            int targetH = _reqH > 0 ? _reqH : _modeH;
            bool isPhysical = (_dec != null && _dec.Width == _modeW && _dec.Height == _modeH);
            bool isRetina = (_hiDpiCheck.Checked && _dec != null && _dec.Width == targetW * 2 && _dec.Height == targetH * 2);
            bool matches = (_dec != null && _dec.Width == targetW && _dec.Height == targetH) || isRetina || isPhysical;
            if (targetW > 0 && _dec != null && _dec.Width > 0 && !matches)
            {
                if (_mismatchSince == DateTime.MinValue) _mismatchSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - _mismatchSince).TotalSeconds > 5)
                {
                    _mismatchSince = DateTime.UtcNow;
                    _mismatchAttempts++;
                    _status.Text = _mismatchAttempts > 3
                        ? "分辨率重建失败：请重启 Mac 端发送程序"
                        : "分辨率不匹配，重连以重建虚拟屏…";
                    _status.Visible = true;
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

    void Relayout()
    {
        int cardW = S(600);
        int padX = S(24);
        int innerPad = S(20);
        int contentW = cardW - innerPad * 2;

        // 顶部 Header
        _headerLabel.Left = padX;
        _headerLabel.Top = S(20);

        _beaconLabel.Left = padX + S(2);
        _beaconLabel.Top = _headerLabel.Bottom + S(6);

        // 主大卡片
        _mainCard.Left = padX;
        _mainCard.Top = _beaconLabel.Bottom + S(16);
        _mainCard.Width = cardW;

        // 1. 设备发现区
        _deviceCard.Left = 0;
        _deviceCard.Top = S(8);
        _deviceCard.Width = cardW;

        _devIcon.Location = new Point(innerPad, S(14));
        _devIcon.Size = new Size(S(46), S(46));

        _goButton.Size = new Size(S(96), S(42));
        _goButton.Location = new Point(cardW - innerPad - _goButton.Width, S(16));

        int devTextLeft = _devIcon.Right + S(12);
        int devTextW = _goButton.Left - devTextLeft - S(14);
        _devNameLabel.Location = new Point(devTextLeft, S(14));
        _devNameLabel.Size = new Size(devTextW, S(24));

        _devSubLabel.Location = new Point(devTextLeft, _devNameLabel.Bottom + S(2));
        _devSubLabel.Size = new Size(devTextW, S(20));

        _discover.Location = new Point(devTextLeft, _devSubLabel.Bottom + S(8));
        _discover.Size = new Size(devTextW, S(28));

        int devBottom = _discover.Visible ? _discover.Bottom : Math.Max(_devIcon.Bottom, _goButton.Bottom);
        _deviceCard.Height = devBottom + S(14);

        // 分隔线 1
        _divider1.Left = innerPad;
        _divider1.Top = _deviceCard.Bottom + S(10);
        _divider1.Width = contentW;
        _divider1.Height = Math.Max(1, S(1));

        // 2. 显示与画质设置区
        _qualitySection.Left = 0;
        _qualitySection.Top = _divider1.Bottom + S(14);
        _qualitySection.Width = cardW;

        _cardTitle.Location = new Point(innerPad, 0);

        _scaleLbl.Location = new Point(innerPad, _cardTitle.Bottom + S(14));

        int scaleComboLeft = _scaleLbl.Right + S(12);
        _scaleCombo.Location = new Point(scaleComboLeft, _scaleLbl.Top - S(3));
        _scaleCombo.Size = new Size(cardW - innerPad - scaleComboLeft, S(28));

        _gpuCheck.Location = new Point(innerPad, Math.Max(_scaleLbl.Bottom, _scaleCombo.Bottom) + S(14));
        _hiDpiCheck.Location = new Point(innerPad, _gpuCheck.Bottom + S(10));

        _qualitySection.Height = _hiDpiCheck.Bottom + S(4);

        // 分隔线 2
        _divider2.Left = innerPad;
        _divider2.Top = _qualitySection.Bottom + S(14);
        _divider2.Width = contentW;
        _divider2.Height = Math.Max(1, S(1));

        // 3. 手动 IP 输入展开区
        _manualToggle.Location = new Point(innerPad, _divider2.Bottom + S(14));

        _manualPanel.Left = innerPad;
        _manualPanel.Width = contentW;

        _manualIpLbl.Location = new Point(0, S(6));
        _manualGoBtn.Size = new Size(S(88), S(32));
        _manualGoBtn.Location = new Point(contentW - _manualGoBtn.Width, S(2));

        int ipBoxLeft = _manualIpLbl.Right + S(8);
        _ipBox.Location = new Point(ipBoxLeft, S(3));
        _ipBox.Size = new Size(_manualGoBtn.Left - ipBoxLeft - S(10), S(28));

        _manualPanel.Height = Math.Max(_ipBox.Bottom, _manualGoBtn.Bottom) + S(4);

        if (_manualPanel.Visible)
        {
            _manualPanel.Top = _manualToggle.Bottom + S(10);
            _mainCard.Height = _manualPanel.Bottom + S(18);
        }
        else
        {
            _mainCard.Height = _manualToggle.Bottom + S(18);
        }

        // 4. 底部状态与快捷键徽章
        _status.Location = new Point(padX + S(2), _mainCard.Bottom + S(14));
        _status.Width = cardW;

        _kbdPanel.Location = new Point(padX, _status.Bottom + S(8));
        _kbdPanel.Width = cardW;

        int gap = S(12);
        int badgeW = (cardW - gap * 2) / 3;
        int badgeH = S(34);
        _kbdPanel.Height = badgeH;

        _badgeEsc.SetBounds(0, 0, badgeW, badgeH);
        _badgeF11.SetBounds(badgeW + gap, 0, badgeW, badgeH);
        _badgeF1.SetBounds((badgeW + gap) * 2, 0, cardW - (badgeW + gap) * 2, badgeH);

        int clientW = cardW + padX * 2;
        int totalH = _kbdPanel.Bottom + S(24);
        ClientSize = new Size(clientW, totalH);
    }

    void UpdateDiscoveredHero(Discovered d)
    {
        _devNameLabel.Text = d.Name;
        _devSubLabel.Text = $"{d.Ip} · 局域网广播高速直连";
        _beaconLabel.Text = "● 已发现 Mac 设备就绪";
        _beaconLabel.ForeColor = Color.FromArgb(22, 163, 74);
        _ipBox.Text = d.Ip;
        _goButton.Enabled = true;
    }

    void SelectScaleComboByPercent(int percent)
    {
        if (_scaleCombo == null) return;
        for (int i = 0; i < _scaleCombo.Items.Count; i++)
        {
            if (_scaleCombo.Items[i] is ScaleOption opt && opt.Percent == percent)
            {
                _scaleCombo.SelectedIndex = i;
                return;
            }
        }
        if (_scaleCombo.Items.Count > 0) _scaleCombo.SelectedIndex = 0;
    }

    /// 状态栏诊断：视频源分辨率 vs 本机显示器设置（不一致=有拉伸/黑边风险的当场可见）
    void UpdateDiagStatus()
    {
        if (!_started || FormBorderStyle != FormBorderStyle.None) return;
        var dec = _dec;
        if (dec == null || dec.Width == 0 || _modeW == 0) return;
        int targetW = _reqW > 0 ? _reqW : _modeW;
        int targetH = _reqH > 0 ? _reqH : _modeH;
        string txt = $"已连接 视频{dec.Width}×{dec.Height} | 显示器{_modeW}×{_modeH}@{_modeHz}Hz";
        bool isRetina = (_hiDpiCheck.Checked && dec.Width == targetW * 2 && dec.Height == targetH * 2);
        if (isRetina) txt += " (Retina 2x)";
        else if (dec.Width != _modeW || dec.Height != _modeH) txt += " (已缩放)";
        txt += _gpu != null && !dec.GpuFailed ? " | 渲染 GPU" : " | 渲染 CPU";
        var lat = Interlocked.CompareExchange(ref _inputLatencyMs, 0, 0);
        if (lat >= 0) txt += $" | 输入 ~{lat}ms";
        txt += " (按 F1 显隐)";
        if (_osd != null && _osd.Text != txt) _osd.Text = txt;
        if (_status != null && _status.Text != txt) _status.Text = txt;
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
        if (string.IsNullOrEmpty(ip) && _discovered.Count > 0)
        {
            ip = _discovered[0].Ip;
            _ipBox.Text = ip;
        }
        if (string.IsNullOrEmpty(ip))
        {
            _status.Text = "请等待自动发现 Mac，或在下方手动输入 IP";
            return;
        }

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
        _config.Gpu = _gpuCheck.Checked;
        _config.HiDpi = _hiDpiCheck.Checked;
        if (_scaleCombo.SelectedItem is ScaleOption opt) _config.ScalePercent = opt.Percent;
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

        // 计算 UI 缩放与等效逻辑分辨率
        float scaleFactor = 1.0f;
        int scalePercent = _config.ScalePercent;
        if (scalePercent <= 0)
        {
            float dpi = DeviceDpi;
            scaleFactor = Math.Max(1.0f, dpi / 96.0f);
            scalePercent = (int)Math.Round(scaleFactor * 100);
        }
        else
        {
            scaleFactor = scalePercent / 100.0f;
        }

        int reqW = (int)Math.Round(w / scaleFactor);
        int reqH = (int)Math.Round(h / scaleFactor);
        if ((reqW & 1) != 0) reqW--;
        if ((reqH & 1) != 0) reqH--;
        reqW = Math.Max(640, reqW);
        reqH = Math.Max(480, reqH);
        _reqW = reqW; _reqH = reqH;

        // 裸流（h264/hevc 自动探测）。传 pw/ph 物理尺寸与 scale 缩放，Mac 端按此进行视网膜超采样直出物理流
        string hidpi = _hiDpiCheck.Checked ? "&hidpi=1" : "";
        _url = $"http://{ip}:8080/raw?w={reqW}&h={reqH}&pw={w}&ph={h}&scale={scalePercent}{hidpi}&token={Uri.EscapeDataString(_config.Token)}";
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
        if (_gpuCheck.Checked)
        {
            try
            {
                _gpu = new GpuVideoRenderer(Log.Write);
                _gpu.Initialize(Handle, Math.Max(1, ClientRectangle.Width), Math.Max(1, ClientRectangle.Height));
                Log.Write("GPU 渲染器待命（设备将在首帧采用）");
            }
            catch (Exception ex)
            {
                Log.Write($"GPU 初始化异常: {ex.Message}");
                _gpu?.Dispose(); _gpu = null;
            }
        }
        else
        {
            Log.Write("使用 CPU 渲染路径（GPU 未勾选）");
        }

        _dec = new VideoDecoder(_url);
        _dec.KeyframeRequester = RequestKeyframe;
        if (_gpu != null)
        {
            _dec.Renderer = _gpu;
            // 设备提供者：渲染器从解码器取硬解设备指针
            _gpu.DeviceProvider = () => _dec?.HwD3DDevice ?? IntPtr.Zero;
            _gpu.SetFrameSource(_dec);
        }
        _dec.FrameReady = OnFrameReady;   // 帧到即画（合并排队），消灭轮询节拍差
        _noFrameSince = DateTime.MinValue;
        _dec.Start();
    }

    private volatile bool _paintPending;
    private void OnFrameReady()
    {
        if (!_started) return;
        if (!_autoFullscreenDone && IsHandleCreated)
        {
            try { BeginInvoke(new Action(EnsureFullscreen)); } catch { }
        }
        if (_dec is { IsGpuRendering: true }) return;   // GPU：画面由专用合成线程直出
        if (_paintPending || !IsHandleCreated) return;
        _paintPending = true;
        try { BeginInvoke(new Action(() => { _paintPending = false; PaintTick(); })); }
        catch { _paintPending = false; }
    }

    /// 首帧到达即进全屏（一次）；跟随窗口当前所在的那块屏
    void EnsureFullscreen()
    {
        if (_autoFullscreenDone && FormBorderStyle == FormBorderStyle.None) return;
        _autoFullscreenDone = true;
        Log.Write("进入全屏");
        FormBorderStyle = FormBorderStyle.None;
        Bounds = Screen.FromControl(this).Bounds;
        foreach (var c in _setupControls) c.Visible = false;

        _osd.Text = "已连接 (按 Esc 退出全屏 · F1 显隐诊断)";
        _osd.Left = 20;
        _osd.Top = 20;
        _osd.BringToFront();
        _osd.Visible = true;
        _osdTimer.Stop();
        _osdTimer.Start();

        _oldImc = ImmAssociateContext(Handle, IntPtr.Zero);   // 禁本机 IME，输入全交 Mac
        _forwarder?.Dispose();
        _forwarder = new InputForwarder(ControlActive, SendPacket, _screenW, _screenH, ChannelHealthy);
        Cursor.Show();   // 保证系统光标可见（它就是零延迟光标层）
    }

    void StopSession()
    {
        _started = false;
        _autoFullscreenDone = false;
        _osdTimer.Stop();
        if (_osd != null) _osd.Visible = false;
        _forwarder?.Dispose(); _forwarder = null;
        if (_oldImc != IntPtr.Zero)
        {
            ImmAssociateContext(Handle, _oldImc);
            _oldImc = IntPtr.Zero;
        }
        _dec?.Dispose(); _dec = null;
        _gpu?.Dispose(); _gpu = null;
        lock (_ctlLock)
        {
            try { _ctlStream?.Dispose(); } catch { }
            try { _ctl?.Close(); } catch { }
            _ctl = null;
            _ctlStream = null;
        }
        Invalidate();
    }

    void ExitFullscreen()
    {
        if (FormBorderStyle != FormBorderStyle.None) return;
        Log.Write("退出全屏，停止投屏并返回主设置界面");
        StopSession();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        WindowState = FormWindowState.Normal;
        foreach (var c in _setupControls) c.Visible = true;
        _manualPanel.Visible = false;
        _manualToggle.Text = "⚙ 手动指定 Mac IP 地址 (跨网段 / VPN 场景)";
        _status.Text = "已退出全屏 · 就绪";
        Relayout();
    }

    void ToggleOsd()
    {
        if (FormBorderStyle != FormBorderStyle.None || _osd == null) return;
        if (_osd.Visible)
        {
            _osdTimer.Stop();
            _osd.Visible = false;
        }
        else
        {
            UpdateDiagStatus();
            _osd.BringToFront();
            _osd.Visible = true;
            _osdTimer.Stop();
            _osdTimer.Start();
        }
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
            if (!_autoFullscreenDone) EnsureFullscreen();
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
                RequestKeyframe(); // 控制通道连上立即请求 IDR 关键帧，起流0等待
                ReadCtlLoop(c);
            }
            catch { lock (_ctlLock) { _ctlStream = null; } }
        });
    }

    /// 立即向 Mac 请求 IDR 关键帧（type 9）
    public void RequestKeyframe()
    {
        var p = new byte[16];
        p[0] = 9;
        SendPacket(p);
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
            try
            {
                if (_ctlStream == null) { BeginInvoke(new Action(TryReconnect)); return; }
                _ctlStream.Write(p, 0, p.Length);
            }
            catch { _ctlStream = null; BeginInvoke(new Action(TryReconnect)); }
        }
    }

    void TryReconnect()
    {
        if (!string.IsNullOrEmpty(_ip)) ConnectCtl(_ip);
    }

    bool ChannelHealthy()
    {
        lock (_ctlLock) return _ctlStream != null;
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
        // 全屏时系统级重绘用黑色擦底（避免灰白闪块）；窗口模式用 macOS 浅灰底色 #F5F5F7
        if (FormBorderStyle == FormBorderStyle.None) e.Graphics.Clear(Color.Black);
        else e.Graphics.Clear(Color.FromArgb(245, 245, 247));
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
                ExitFullscreen();
            }
            else
            {
                Close();
            }
            return true;
        }
        if (keyData == Keys.F11)
        {
            if (FormBorderStyle == FormBorderStyle.None)
            {
                ExitFullscreen();
            }
            else if (_started)
            {
                EnsureFullscreen();
            }
            return true;
        }
        if (keyData == Keys.F1 || keyData == (Keys.Control | Keys.I))
        {
            ToggleOsd();
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
        public bool Gpu { get; set; } = true;
        public bool HiDpi { get; set; } = false;
        public int ScalePercent { get; set; } = 100;
        public int ConfigVersion { get; set; } = 8;
    }

    internal sealed class PairDialog : Form
    {
        public string Code => _box.Text.Trim();
        private readonly TextBox _box;

        public PairDialog()
        {
            Text = "配对 Mac 拓展屏";
            float dpiScale = Math.Max(1.0f, (float)DeviceDpi / 96.0f);
            int S(int px) => (int)Math.Round(px * dpiScale);

            ClientSize = new Size(S(360), S(230));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = Color.FromArgb(245, 245, 247);
            Font = new Font("Microsoft YaHei UI", 10f);

            var card = new CardPanel { Left = S(16), Top = S(14), Width = S(328), Height = S(202) };
            var icon = new Label { Text = "🔒", Left = S(14), Top = S(14), Font = new Font("Segoe UI Emoji", 14f), AutoSize = true };
            var label = new Label
            {
                Text = "输入 Mac 菜单栏显示的 4 位配对码",
                Left = S(44), Top = S(16), AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(29, 29, 31)
            };

            _box = new TextBox
            {
                Left = S(64), Top = S(56), Width = S(200), Height = S(42),
                Font = new Font("Consolas", 20f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                MaxLength = 4
            };
            _box.KeyPress += (_, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };

            var ok = new ModernButton { Text = "确认配对", Left = S(64), Top = S(124), Width = S(95), Height = S(36), IsPrimary = true, DialogResult = DialogResult.OK };
            var cancel = new ModernButton { Text = "取消", Left = S(169), Top = S(124), Width = S(95), Height = S(36), IsPrimary = false, DialogResult = DialogResult.Cancel };

            card.Controls.AddRange(new Control[] { icon, label, _box, ok, cancel });
            Controls.Add(card);
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
                        if (_discovered.Count == 1)
                        {
                            _discover.SelectedIndex = 0;
                            UpdateDiscoveredHero(item);
                        }
                        else
                        {
                            _beaconLabel.Text = $"● 已发现 {_discovered.Count} 台 Mac 设备";
                            _discover.Visible = true;
                            _discover.BringToFront();
                            _deviceCard.Height = 104;
                            Relayout();
                        }
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
            {
                _config = JsonSerializer.Deserialize<ReceiverConfig>(File.ReadAllText(path)) ?? new ReceiverConfig();
                if (_config.ConfigVersion < 8)
                {
                    _config.Gpu = true;
                    _config.HiDpi = false;
                    _config.ScalePercent = 100;
                    _config.ConfigVersion = 8;
                    SaveConfig();
                }
            }
            else if (File.Exists(Path.Combine(ConfigDir, "mac_ip.txt")))
            {
                _config.Ip = File.ReadAllText(Path.Combine(ConfigDir, "mac_ip.txt")).Trim();   // 兼容旧版
                _config.Gpu = true;
                _config.HiDpi = false;
                _config.ScalePercent = 100;
                _config.ConfigVersion = 8;
                SaveConfig();
            }
            else
            {
                _config.Gpu = true;
                _config.HiDpi = false;
                _config.ScalePercent = 100;
                _config.ConfigVersion = 8;
            }
        }
        catch { }
        _ipBox.Text = _config.Ip;
        _gpuCheck.Checked = _config.Gpu;
        _hiDpiCheck.Checked = _config.HiDpi;
        SelectScaleComboByPercent(_config.ScalePercent);
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
        _retryTimer?.Stop();
        StopSession();
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


/// 原生崩溃最后防线：进程被段错误杀死前，记录异常代码和所在模块（故障定位的决定性证据）
internal static class CrashGuard
{
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);
    private delegate int FilterDelegate(IntPtr exceptionPointers);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleEx(uint flags, IntPtr moduleName, out IntPtr module);
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileName(IntPtr module, System.Text.StringBuilder name, uint size);

    private static FilterDelegate? _kept;   // 防委托被 GC 回收

    public static void Install()
    {
        _kept = Filter;
        SetUnhandledExceptionFilter(Marshal.GetFunctionPointerForDelegate(_kept));
    }

    private static int Filter(IntPtr pointers)
    {
        try
        {
            var rec = Marshal.ReadIntPtr(pointers);
            uint code = (uint)Marshal.ReadInt32(rec);
            nint addr = Marshal.ReadIntPtr(rec, 16);
            string module = "?";
            if (GetModuleHandleEx(0x6, addr, out var mod) && mod != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(1024);
                GetModuleFileName(mod, sb, 1024);
                module = sb.ToString();
            }
            Log.Write($"💥 原生崩溃 code=0x{code:X8} @ 0x{(ulong)addr:X} 模块=[{module}]");
        }
        catch { }
        return 0;   // 交回系统默认处理（进程退出），我们不掩盖
    }
}
