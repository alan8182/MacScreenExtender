using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using SharpGen.Runtime;
using Vortice;
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

/// <summary>
/// GPU 渲染直通：d3d11va 解码帧（NV12 纹理）→ ID3D11VideoProcessor 转 BGRA
/// → 交换链垂直同步上屏，全程不落内存。
/// 光标由 Windows 系统光标承担（不隐藏），渲染器只负责视频画面。
/// 专用合成线程以 Present(1) 锁定显示器刷新率。
/// 任一步失败返回 false，调用方累计后回退 CPU 路径（完整保留）。
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
    private D3D11OutputViewT? _videoOutView;   // 视频纹理（源分辨率）的输出视图，解码线程 Blt 用
    private D3D11InputViewT? _videoInView;     // 视频纹理的输入视图，合成 Blt 用
    private D3D11Texture2DT? _videoTex;
    private D3D11OutputViewT? _bbOutView;      // 后备缓冲的输出视图，合成 Blt 用
    private D3D11Texture2DT? _backBuffer;
    private IntPtr _hwnd;
    private nint _devicePtr;
    private int _swapW, _swapH;
    private volatile bool _resizePending;
    private int _pendW, _pendH;
    private int _srcW = -1, _srcH = -1;
    private bool _bltLogged, _vtLogged;

    // 专用合成线程：Present(1) 自动锁定显示器刷新率（120Hz 屏 = 120 次合成/秒）
    private Thread? _composeThread;
    private volatile bool _composeRunning;

    /// 设备提供者：返回解码器硬解设备的 ID3D11Device 原生指针
    public Func<IntPtr>? DeviceProvider;

    public GpuVideoRenderer(Action<string> log) { _log = log; }

    /// 只记录窗口参数；设备在首个硬件解码帧到达时从帧上"采用"
    public void Initialize(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _swapW = Math.Max(1, width);
        _swapH = Math.Max(1, height);
        _log($"GPU: 渲染器待命（窗口 {_swapW}x{_swapH}，等待解码设备）");
    }

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
            using var factory = GetDxgiFactory(dxgiDevice);
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

    private static IDXGIFactory2 GetDxgiFactory(IDXGIDevice dxgiDevice)
    {
        using var adapter = dxgiDevice.GetAdapter();
        return adapter.GetParent<IDXGIFactory2>();
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
        return _videoDevice.CreateVideoProcessorEnumerator(desc);
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

    /// 解码线程：把硬件解码帧转 BGRA 写入"源分辨率视频纹理"（无缩放，不直接上屏）
    public bool UploadFrame(AVFrame* frame)
    {
        try
        {
            if (!EnsureDevice()) return false;
            int srcW = frame->width, srcH = frame->height;
            if (srcW != _srcW || srcH != _srcH)
            {
                _srcW = srcW; _srcH = srcH;
                RecreateVideoTex(srcW, srcH);
                _log($"GPU: 源分辨率 {srcW}x{srcH}");
            }
            if (_videoTex == null || _videoOutView == null) return false;

            var tex = new D3D11Texture2DT((nint)frame->data[0]);
            int slice = (int)(nint)frame->data[1];

            var inDesc = new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)slice },
            };
            using var inView = _videoDevice.CreateVideoProcessorInputView(tex, _procEnum, inDesc);
            if (inView == null) { _log("GPU: InputView 创建失败"); return false; }
            if (!_bltLogged) { _bltLogged = true; _log("GPU: 首次 VideoProcessorBlt"); }

            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0u, true,
                new RawRect(0, 0, srcW, srcH));
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0u, true,
                new RawRect(0, 0, srcW, srcH));
            _videoContext.VideoProcessorBlt(_processor, _videoOutView, 0u, new[] { stream(inView) });
            return true;
        }
        catch (Exception ex)
        {
            _log($"GPU: 帧上传异常 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private VideoProcessorStream stream(D3D11InputViewT inView)
    {
        return new VideoProcessorStream { Enable = true, InputSurface = inView };
    }

    private VideoProcessorStream stream(int sx, int sy, int sw, int sh, D3D11InputViewT inView)
    {
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0u, true,
            new RawRect(sx, sy, sx + sw, sy + sh));
        return new VideoProcessorStream { Enable = true, InputSurface = inView };
    }

    /// 合成线程：等比缩放视频纹理到后备缓冲 + 垂直同步上屏。
    /// 尺寸调整也在此线程应用（与 Present 串行，规避 ResizeBuffers 的线程约束）
    public void Compose()
    {
        try
        {
            if (_swapChain == null || _videoTex == null || _context == null || _device == null) return;

            if (_resizePending)
            {
                // 两步走：本帧释放全部后备缓冲引用且不 Present（让交换链翻转完成），
                // 下一帧再 ResizeBuffers —— 紧贴 Present 调用会得到 DXGI_ERROR_INVALID_CALL
                _rtv?.Dispose(); _rtv = null;
                _bbOutView?.Dispose(); _bbOutView = null;
                _backBuffer?.Dispose(); _backBuffer = null;
                var rr = _swapChain.ResizeBuffers(0, (uint)_pendW, (uint)_pendH, Format.Unknown, SwapChainFlags.None);
                if (rr.Success)
                {
                    _resizePending = false;
                    _swapW = _pendW; _swapH = _pendH;
                    using var bb = _swapChain.GetBuffer<D3D11Texture2DT>(0u);
                    _backBuffer = new D3D11Texture2DT(bb.NativePointer);
                    _rtv = _device.CreateRenderTargetView(bb, null);
                    var obDesc = new VideoProcessorOutputViewDescription
                    {
                        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    };
                    _bbOutView = _videoDevice.CreateVideoProcessorOutputView(bb, _procEnum, obDesc);
                    _log($"GPU: 交换链调整 {_swapW}x{_swapH}");
                }
                else
                {
                    Thread.Sleep(30);   // 让在途 Present 完成，下轮重试
                    return;
                }
            }

            // 等比目标区（黑边由 Clear 铺底）
            double s = Math.Min((double)_swapW / _srcW, (double)_swapH / _srcH);
            int dw = Math.Max(1, (int)(_srcW * s));
            int dh = Math.Max(1, (int)(_srcH * s));
            int dx = (_swapW - dw) / 2;
            int dy = (_swapH - dh) / 2;

            _context.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0u, true,
                new RawRect(dx, dy, dx + dw, dy + dh));
            _videoContext.VideoProcessorBlt(_processor, _bbOutView, 0u, new[] { stream(_videoInView) });
            _swapChain.Present(1, PresentFlags.None);
        }
        catch { }
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

    public void RequestResize(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (w == _swapW && h == _swapH) return;
        _pendW = w; _pendH = h;
        _resizePending = true;
    }


    /// (重建)源分辨率视频纹理 + 输入/输出视图
    private void RecreateVideoTex(int srcW, int srcH)
    {
        _videoInView?.Dispose(); _videoInView = null;
        _videoOutView?.Dispose(); _videoOutView = null;
        _videoTex?.Dispose(); _videoTex = null;
        if (_device == null || _videoDevice == null || _procEnum == null) return;

        var vtDesc = new Texture2DDescription
        {
            Width = (uint)srcW, Height = (uint)srcH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        };
        _videoTex = _device.CreateTexture2D(vtDesc);
        var inDesc = new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        };
        _videoInView = _videoDevice.CreateVideoProcessorInputView(_videoTex, _procEnum, inDesc);
        var outDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        };
        _videoOutView = _videoDevice.CreateVideoProcessorOutputView(_videoTex, _procEnum, outDesc);
    }

    private void TeardownDevice()
    {
        _videoInView?.Dispose(); _videoInView = null;
        _videoOutView?.Dispose(); _videoOutView = null;
        _bbOutView?.Dispose(); _bbOutView = null;
        _rtv?.Dispose(); _rtv = null;
        _videoTex?.Dispose(); _videoTex = null;
        _backBuffer?.Dispose(); _backBuffer = null;
        _processor?.Dispose(); _processor = null;
        _procEnum?.Dispose(); _procEnum = null;
        _videoDevice?.Dispose(); _videoDevice = null;
        _videoContext?.Dispose(); _videoContext = null;
        _swapChain?.Dispose(); _swapChain = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _devicePtr = IntPtr.Zero;
    }

    public void Dispose()
    {
        _composeRunning = false;
        try { _composeThread?.Join(1000); } catch { }
        TeardownDevice();
    }
}
