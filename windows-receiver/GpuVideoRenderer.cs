using System;
using System.Runtime.InteropServices;
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

    private IntPtr _hwnd;
    private int _swapW, _swapH;
    private int _spriteW, _spriteH;
    private (byte[] px, int w, int h)? _pendingCursor;
    private volatile bool _resizePending;
    private int _pendW, _pendH;
    private int _srcW = -1, _srcH = -1;
    private bool _bltLogged, _cursorLogged;

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
    private bool EnsureDevice(AVFrame* frame)
    {
        if (_device != null) return true;
        try
        {
            using var tex = new D3D11Texture2DT((nint)frame->data[0]);
            _device = tex.Device;
            _context = _device.ImmediateContext;
            _log("GPU: 采用解码设备");

            using var mt = _context.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var factory = dxgiDevice.GetParent<IDXGIFactory2>();

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
            return true;
        }
        catch (Exception ex)
        {
            _log($"GPU: 设备采用异常 {ex.GetType().Name}: {ex.Message}");
            _device = null; _context = null;
            return false;
        }
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

    public bool RenderFrame(AVFrame* frame, float cursorNx, float cursorNy, bool cursorVisible)
    {
        try
        {
            if (!EnsureDevice(frame)) return false;
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

            // 等比目标区（黑边）
            double s = Math.Min((double)_swapW / srcW, (double)_swapH / srcH);
            int dw = Math.Max(1, (int)(srcW * s));
            int dh = Math.Max(1, (int)(srcH * s));
            int dx = (_swapW - dw) / 2;
            int dy = (_swapH - dh) / 2;

            _context.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));

            var stream = new VideoProcessorStream
            {
                Enable = true,
                InputSurface = inView,
            };
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0u, true,
                new RawRect(dx, dy, dx + dw, dy + dh));
            if (!_bltLogged) { _bltLogged = true; _log("GPU: 首次 VideoProcessorBlt"); }
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0u, new[] { stream });

            if (cursorVisible) DrawCursor(dx, dy, dw, dh, cursorNx, cursorNy);

            _swapChain.Present(1, PresentFlags.None);
            return true;
        }
        catch (Exception ex)
        {
            _log($"GPU: 渲染异常 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
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

        using var backBuffer = _swapChain.GetBuffer<D3D11Texture2DT>(0);
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
