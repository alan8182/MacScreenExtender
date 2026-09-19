using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MacScreenReceiver;

// FFmpeg.AutoGen 与 Vortice 声明了同名 COM 接口，用别名区分（Vortice 侧为本实现）
using D3D11DeviceT = Vortice.Direct3D11.ID3D11Device;
using D3D11ContextT = Vortice.Direct3D11.ID3D11DeviceContext;
using D3D11VideoDeviceT = Vortice.Direct3D11.ID3D11VideoDevice;
using D3D11VideoContextT = Vortice.Direct3D11.ID3D11VideoContext;
using D3D11Texture2DT = Vortice.Direct3D11.ID3D11Texture2D;
using D3D11ProcEnumT = Vortice.Direct3D11.ID3D11VideoProcessorEnumerator;
using D3D11ProcessorT = Vortice.Direct3D11.ID3D11VideoProcessor;
using D3D11OutputViewT = Vortice.Direct3D11.ID3D11VideoProcessorOutputView;
using D3D11RtvT = Vortice.Direct3D11.ID3D11RenderTargetView;
using D3D11InputViewT = Vortice.Direct3D11.ID3D11VideoProcessorInputView;
using DxgiSwapChainT = Vortice.DXGI.IDXGISwapChain1;
using DxgiSurfaceT = Vortice.DXGI.IDXGISurface;
using FeatureLevelT = Vortice.Direct3D.FeatureLevel;
using InterpModeT = Vortice.Direct2D1.InterpolationMode;
using D2DPixelFmtT = Vortice.DCommon.PixelFormat;
using D2DAlphaT = Vortice.DCommon.AlphaMode;

/// <summary>
/// GPU 渲染直通：d3d11va 解码帧（NV12 纹理）→ ID3D11VideoProcessor 转 BGRA
/// → 交换链垂直同步上屏，全程不落内存。光标精灵经 D2D 叠画。
/// 任一步失败返回 false，调用方累计后回退 CPU 路径（下载+sws+GDI，完整保留）。
/// 所有渲染调用发生在解码线程（单线程化）；UI 线程仅 RequestResize（帧首应用）。
/// </summary>
public sealed unsafe class GpuVideoRenderer : IDisposable
{
    private readonly Action<string> _log;
    private D3D11DeviceT? _device;
    private D3D11ContextT? _context;
    private DxgiSwapChainT? _swapChain;
    private D3D11RtvT? _rtv;
    private D3D11VideoDeviceT? _videoDevice;
    private D3D11VideoContextT? _videoContext;
    private D3D11ProcEnumT? _procEnum;
    private D3D11ProcessorT? _processor;
    private D3D11OutputViewT? _outputView;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID2D1Bitmap1? _d2dTarget;
    private ID2D1Bitmap1? _cursorD2d;
    private AVBufferRef* _hwRef;
    private D3D11Texture2DT? _videoTex;             // 解码帧的 GPU 持久副本（含黑边）
    private ID3D11VideoProcessorOutputView? _videoOutView;
    private ID3D11RenderTargetView? _videoRtv;
    private D3D11Texture2DT? _backBuffer;
    private int _lastDx, _lastDy, _lastDw = 1, _lastDh = 1;

    // 光标状态（Mac 回报，作为本地位置不可用时的兜底）
    private readonly object _cursorLock = new();
    private float _curNx, _curNy;
    private bool _curVisible;

    // 专用合成线程：Present(1) 自动锁定显示器刷新率（120Hz 屏 = 120 次合成/秒）
    private Thread? _composeThread;
    private volatile bool _composeRunning;
    /// 本地光标提供者（控制中返回 Windows 鼠标实时位置，0ms 延迟）
    public Func<(float nx, float ny, bool vis)?>? LocalCursorProvider;
    /// 设备提供者：返回解码器硬解设备的 ID3D11Device 原生指针
    public Func<IntPtr>? DeviceProvider;
    private Func<(float nx, float ny, bool vis)?>? _localProvider;

    private IntPtr _hwnd;
    private nint _devicePtr;
    private int _swapW, _swapH;
    private int _spriteW, _spriteH;
    private (byte[] px, int w, int h)? _pendingCursor;
    private volatile bool _resizePending;
    private int _pendW, _pendH;
    private int _srcW = -1, _srcH = -1;
    private bool _bltLogged, _cursorLogged;
    private bool _localActive;
    private long _localLastSeen;
    private float _localNx, _localNy;

    // 合成节奏仪表
    private long _presCount, _d2dErr;
    private double _presMinMs = double.MaxValue, _presMaxMs;
    private long _lastPresTs; private long _lastGaugeTs;
    private int _srcLocal, _srcReport, _srcHidden;

    public GpuVideoRenderer(Action<string> log) { _log = log; }

    /// 只记录窗口参数；设备在首个硬件解码帧到达时从帧上"采用"
    public void Initialize(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _swapW = Math.Max(1, width);
        _swapH = Math.Max(1, height);
        _log($"GPU: 渲染器待命（窗口 {_swapW}x{_swapH}，等待解码设备）");
    }

    /// 从解码帧上采用设备（解码纹理属于它），并搭建整条渲染管线
    /// 从解码帧上采用设备（解码纹理属于它），并搭建整条渲染管线
    private bool EnsureDevice()
    {
        nint devPtr = DeviceProvider?.Invoke() ?? IntPtr.Zero;
        if (devPtr == IntPtr.Zero) { _log("GPU: 解码器无硬解设备"); return false; }
        if (_device != null && _devicePtr == devPtr) return true;   // 已就绪
        if (_device != null) TeardownDevice();                       // 解码器重建了设备 → 重建管线

        try
        {
            _devicePtr = devPtr;
            // QI 产生渲染器自有的引用（解码器销毁设备时不会连带渲染器）
            var raw = new D3D11DeviceT(devPtr);
            _device = raw.QueryInterface<D3D11DeviceT>();
            _context = _device.ImmediateContext;
            _log("GPU: 设备已采用");

            using var mt = _context.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var factory = adapterGetParent(dxgiDevice);
            _log("GPU: DXGI 工厂就绪");

            var scDesc = new SwapChainDescription1
            {
                Width = (uint)_swapW,
                Height = (uint)_swapH,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.None,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            };
            _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, scDesc);
            if (_swapChain == null) { _log("GPU: 交换链创建失败"); return false; }
            _log("GPU: 交换链就绪 (FLIP_SEQUENTIAL)");

            _videoDevice = _device.QueryInterface<D3D11VideoDeviceT>();
            _videoContext = _context.QueryInterface<D3D11VideoContextT>();
            _procEnum = CreateProcEnum(_swapW, _swapH);
            if (_procEnum == null) return false;
            _processor = _videoDevice.CreateVideoProcessor(_procEnum, 0u);
            if (_processor == null) { _log("GPU: VideoProcessor 创建失败"); return false; }
            _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0u, VideoFrameFormat.Progressive);
            SetColorSpace();

            RecreateTargets();
            if (_outputView == null || _rtv == null) return false;

            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
            _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
            _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            _log("GPU: 渲染管线全就绪");
            StartComposeLoop();
            return true;
        }
        catch (Exception ex)
        {
            _log($"GPU: 设备采用异常 {ex.GetType().Name}: {ex.Message}");
            TeardownDevice();
            return false;
        }
    }

    private static IDXGIFactory2 adapterGetParent(IDXGIDevice dxgiDevice)
    {
        using var adapter = dxgiDevice.GetAdapter();
        return adapter.GetParent<IDXGIFactory2>();
    }

    private void TeardownDevice()
    {
        _outputView?.Dispose(); _outputView = null;
        _rtv?.Dispose(); _rtv = null;
        _d2dTarget?.Dispose(); _d2dTarget = null;
        _d2dContext?.Dispose(); _d2dContext = null;
        _d2dDevice?.Dispose(); _d2dDevice = null;
        _d2dFactory?.Dispose(); _d2dFactory = null;
        _processor?.Dispose(); _processor = null;
        _procEnum?.Dispose(); _procEnum = null;
        _videoDevice?.Dispose(); _videoDevice = null;
        _videoContext?.Dispose(); _videoContext = null;
        _swapChain?.Dispose(); _swapChain = null;
        _videoTex?.Dispose(); _videoTex = null;
        _videoRtv?.Dispose(); _videoRtv = null;
        _backBuffer?.Dispose(); _backBuffer = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _devicePtr = IntPtr.Zero;
    }

    private D3D11ProcEnumT? CreateProcEnum(int w, int h)
    {
        if (_videoDevice == null) return null;
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)w,
            InputHeight = (uint)h,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)w,
            OutputHeight = (uint)h,
            Usage = VideoUsage.PlaybackNormal,
        };
        var penum = _videoDevice.CreateVideoProcessorEnumerator(desc);
        if (penum == null)
        {
            _log("GPU: ProcessorEnum 创建失败");
            return null;
        }
        return penum;
    }

    private void SetColorSpace()
    {
        if (_videoContext == null || _processor == null) return;
        // 输入：BT.709 16-235（SCK 采集惯例）；输出：RGB full，防发灰
        var inCs = new VideoProcessorColorSpace { Usage = 0, YCbCr_Matrix = 1, Nominal_Range = 1 };
        var outCs = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0 };
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0u, inCs);
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, outCs);
    }

    public void SetCursorSprite(byte[] bgra, int w, int h)
    {
        // 跨线程安全：只存内存，帧渲染时惰性建 GPU 资源
        _pendingCursor = (bgra, w, h);
        _cursorD2d = null;
    }

    public void RequestResize(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (w == _swapW && h == _swapH) return;
        _pendW = w; _pendH = h;
        _resizePending = true;
    }

    /// 解码线程：把硬件解码帧转 BGRA 并缩放/加黑边到持久视频纹理（不直接上屏）
    public bool UploadFrame(AVFrame* frame)
    {
        try
        {
            if (!EnsureDevice()) return false;
            if (_resizePending) ApplyPendingResize();
            if (_srcW != frame->width || _srcH != frame->height)
            {
                _srcW = frame->width; _srcH = frame->height;
                _log($"GPU: 源分辨率 {_srcW}x{_srcH}");
            }

            var tex = new D3D11Texture2DT((nint)frame->data[0]);
            int slice = (int)(nint)frame->data[1];
            int srcW = frame->width, srcH = frame->height;

            var inDesc = new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)slice },
            };
            using var inView = _videoDevice.CreateVideoProcessorInputView(tex, _procEnum, inDesc);
            if (inView == null) { _log("GPU: InputView 创建失败"); return false; }

            // 等比目标区（黑边），写入持久视频纹理
            double s = Math.Min((double)_swapW / srcW, (double)_swapH / srcH);
            int dw = Math.Max(1, (int)(srcW * s));
            int dh = Math.Max(1, (int)(srcH * s));
            int dx = (_swapW - dw) / 2;
            int dy = (_swapH - dh) / 2;
            _lastDx = dx; _lastDy = dy; _lastDw = dw; _lastDh = dh;

            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0u, true,
                new RawRect(dx, dy, dx + dw, dy + dh));
            _videoContext.VideoProcessorBlt(_processor, _videoOutView, 0u, new[] { stream(0, 0, srcW, srcH, inView) });
            return true;
        }
        catch (Exception ex)
        {
            _log($"GPU: 帧上传异常 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private VideoProcessorStream stream(int sx, int sy, int sw, int sh, D3D11InputViewT inView)
    {
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0u, true,
            new RawRect(sx, sy, sx + sw, sy + sh));
        return new VideoProcessorStream { Enable = true, InputSurface = inView };
    }

    private void StartComposeLoop()
    {
        if (_composeThread != null) return;
        _composeRunning = true;
        _composeThread = new Thread(ComposeLoop) { IsBackground = true, Name = "gpu-compose" };
        _composeThread.Start();
    }

    private void ComposeLoop()
    {
        while (_composeRunning)
        {
            Compose();
            if (_videoTex == null) Thread.Sleep(10);   // 无画面：低速空转；有画面：Present(1) 自锁刷新率
        }
    }

    /// 合成一帧：视频纹理 → 后备缓冲 → 光标（本地位置优先）→ 垂直同步上屏
    public void Compose()
    {
        try
        {
            if (_swapChain == null || _videoTex == null || _backBuffer == null || _context == null) return;
            _context.CopyResource(_backBuffer, _videoTex);

            // 光标源粘滞：本地位置 500ms 内出现过就坚持用本地（避免两源来回切换产生跳跃）
            float cnx, cny; bool cvis;
            var local = _localProvider?.Invoke();
            if (local.HasValue)
            {
                _localLastSeen = Environment.TickCount64;
                _localNx = local.Value.nx; _localNy = local.Value.ny;
                _localActive = true;
            }
            else if (_localActive && Environment.TickCount64 - _localLastSeen > 500)
            {
                _localActive = false;
            }
            if (_localActive) { cnx = _localNx; cny = _localNy; cvis = true; }
            else lock (_cursorLock) { cnx = _curNx; cny = _curNy; cvis = _curVisible; }
            if (cvis) DrawCursor(_lastDx, _lastDy, _lastDw, _lastDh, cnx, cny);

            _swapChain.Present(1, PresentFlags.None);

            var now = Environment.TickCount64;
            if (_lastPresTs != 0)
            {
                var dt = now - _lastPresTs;
                if (dt < _presMinMs) _presMinMs = dt;
                if (dt > _presMaxMs) _presMaxMs = dt;
                _presCount++;
            }
            _lastPresTs = now;
            if (local.HasValue) _srcLocal++; else if (cvis) _srcReport++; else _srcHidden++;
            if (now - _lastGaugeTs > 5000)
            {
                if (_presCount > 0)
                    _log($"GPU 合成: {_presCount / 5.0:F0}fps 间隔 {_presMinMs:F1}-{_presMaxMs:F1}ms | 光标源 本地:{_srcLocal / 5} 回报:{_srcReport / 5} 隐藏:{_srcHidden / 5} D2D错:{_d2dErr}");
                _presCount = 0; _presMinMs = double.MaxValue; _presMaxMs = 0;
                _srcLocal = _srcReport = _srcHidden = 0;
                _lastGaugeTs = now;
            }
        }
        catch (Exception ex)
        {
            _d2dErr++;
            if (_d2dErr % 20 == 1) _log($"GPU: 合成异常 {_d2dErr} 次: {ex.Message}");
        }
    }

    public void UpdateCursor(float nx, float ny, bool visible)
    {
        lock (_cursorLock) { _curNx = nx; _curNy = ny; _curVisible = visible; }
    }

    private void DrawCursor(float dx, float dy, float dw, float dh, float nx, float ny)
    {
        EnsureCursorTexture();
        if (_cursorD2d == null || _d2dContext == null || _d2dTarget == null) return;
        float scale = Math.Max(1.2f, dw / 1600f);
        float cx = dx + nx * dw, cy = dy + ny * dh;
        _d2dContext.Target = _d2dTarget;
        if (!_cursorLogged) { _cursorLogged = true; _log("GPU: 首次 D2D 光标绘制"); }
        _d2dContext.BeginDraw();
        _d2dContext.DrawBitmap(_cursorD2d,
            new RawRectF(cx, cy, cx + _spriteW * scale, cy + _spriteH * scale),
            1f, InterpModeT.Linear, null, null);
        _d2dContext.EndDraw();
    }

    private void EnsureCursorTexture()
    {
        if (_cursorD2d != null || _pendingCursor == null || _d2dContext == null) return;
        var (px, w, h) = _pendingCursor.Value;
        var props = new BitmapProperties1(
            new D2DPixelFmtT(Format.B8G8R8A8_UNorm, D2DAlphaT.Premultiplied));
        fixed (byte* p = px)
        {
            var size = new SizeI(w, h);
            _cursorD2d = _d2dContext.CreateBitmap(size, (nint)p, (uint)(w * 4), props);
        }
        _pendingCursor = null;
    }

    private void ApplyPendingResize()
    {
        _resizePending = false;
        if (_swapChain == null) return;
        _log($"GPU: 交换链调整 {_pendW}x{_pendH}");
        var r = _swapChain.ResizeBuffers(0, (uint)_pendW, (uint)_pendH, Format.Unknown, SwapChainFlags.None);
        if (r.Failure)
        {
            _log($"GPU: ResizeBuffers 失败 {r}");
            return;
        }
        _swapW = _pendW; _swapH = _pendH;
        RecreateTargets();
    }

    /// 重建依赖交换链后备缓冲的资源（RTV / 输出视图 / D2D 目标）
    private void RecreateTargets()
    {
        _outputView?.Dispose(); _outputView = null;
        _rtv?.Dispose(); _rtv = null;
        _d2dTarget?.Dispose(); _d2dTarget = null;
        if (_swapChain == null || _device == null || _videoDevice == null || _procEnum == null) return;

        using var backBuffer = _swapChain.GetBuffer<D3D11Texture2DT>(0u);
        _backBuffer?.Dispose();
        _backBuffer = new D3D11Texture2DT(backBuffer.NativePointer);

        // 持久视频纹理（BGRA，含黑边）
        _videoOutView?.Dispose(); _videoOutView = null;
        _videoRtv?.Dispose(); _videoRtv = null;
        _videoTex?.Dispose(); _videoTex = null;
        var vtDesc = new Texture2DDescription
        {
            Width = (uint)_swapW, Height = (uint)_swapH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        };
        _videoTex = _device.CreateTexture2D(vtDesc);
        var vtOutDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        };
        _videoOutView = _videoDevice.CreateVideoProcessorOutputView(_videoTex, _procEnum, vtOutDesc);
        _videoRtv = _device.CreateRenderTargetView(_videoTex, null);
        _context.ClearRenderTargetView(_videoRtv, new Color4(0, 0, 0, 1));

        _rtv = _device.CreateRenderTargetView(backBuffer, null);
        var outDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        };
        _outputView = _videoDevice.CreateVideoProcessorOutputView(backBuffer, _procEnum, outDesc);
        if (_outputView == null) _log("GPU: OutputView 创建失败");

        if (_d2dContext != null)
        {
            using var surface = backBuffer.QueryInterface<DxgiSurfaceT>();
            var props = new BitmapProperties1(
                new D2DPixelFmtT(Format.B8G8R8A8_UNorm, D2DAlphaT.Ignore));
            _d2dTarget = _d2dContext.CreateBitmapFromDxgiSurface(surface, props);
        }
    }

    public void Dispose()
    {
        _composeRunning = false;
        try { _composeThread?.Join(1000); } catch { }
        _outputView?.Dispose();
        _rtv?.Dispose();
        _d2dTarget?.Dispose();
        _cursorD2d?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
        _processor?.Dispose();
        _procEnum?.Dispose();
        _videoDevice?.Dispose();
        _videoContext?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        if (_hwRef != default)
        {
            var hw = _hwRef;
            ffmpeg.av_buffer_unref(&hw);
            _hwRef = default;
        }
    }
}
