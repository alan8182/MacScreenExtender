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
using DxgiSwapChainT = Vortice.DXGI.IDXGISwapChain1;
using FeatureLevelT = Vortice.Direct3D.FeatureLevel;

/// <summary>
/// GPU 渲染直通：d3d11va 解码帧（NV12 纹理）→ ID3D11VideoProcessor 缩放/转 BGRA
/// → 交换链垂直同步上屏，全程不落内存。
/// 分工：解码线程只保留帧引用；本类的专用合成线程做全部渲染并以 Present(1)
/// 锁定显示器刷新率——空闲时自动休眠（GPU 零负载）。
/// 光标由 Windows 系统承担（不隐藏）。任一步失败自动回退 CPU 路径（完整保留）。
/// </summary>
public sealed unsafe class GpuVideoRenderer : IDisposable
{
    private readonly Action<string> _log;
    private D3D11DeviceT? _device;
    private D3D11ContextT? _context;
    private DxgiSwapChainT? _swapChain;
    private D3D11RtvT? _rtvBb;
    private D3D11OutputViewT? _bbOutView;
    private D3D11Texture2DT? _backBuffer;
    private D3D11VideoDeviceT? _videoDevice;
    private D3D11VideoContextT? _videoContext;
    private D3D11ProcEnumT? _procEnum;
    private D3D11ProcessorT? _processor;
    private IntPtr _hwnd;
    private nint _devicePtr;
    private int _swapW, _swapH;
    private volatile bool _resizePending;
    private int _pendW, _pendH;
    private int _srcW = -1, _srcH = -1;
    private bool _bltLogged, _vtLogged;

    private VideoDecoder? _src;
    private Thread? _composeThread;
    private volatile bool _composeRunning;
    private long _lastFc = -1;

    /// 设备提供者：返回解码器硬解设备的 ID3D11Device 原生指针
    public Func<IntPtr>? DeviceProvider;

    public GpuVideoRenderer(Action<string> log) { _log = log; }

    /// 只记录窗口参数；设备在首个硬件解码帧到达时从解码器取
    public void Initialize(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _swapW = Math.Max(1, width);
        _swapH = Math.Max(1, height);
        _log($"GPU: 渲染器待命（窗口 {_swapW}x{_swapH}，等待解码设备）");
    }

    /// 绑定帧来源（解码器）并启动合成线程
    public void SetFrameSource(VideoDecoder decoder)
    {
        _src = decoder;
        StartComposeLoop();
    }

    private bool EnsureDevice()
    {
        nint devPtr = DeviceProvider?.Invoke() ?? IntPtr.Zero;
        if (devPtr == IntPtr.Zero) { _log("GPU: 解码器无硬解设备"); return false; }
        if (_device != null && _devicePtr == devPtr) return true;
        if (_device != null) TeardownDevice();

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
            _procEnum = CreateProcEnum(_srcW > 0 ? _srcW : _swapW, _srcH > 0 ? _srcH : _swapH);
            if (_procEnum == null) return false;
            _processor = _videoDevice.CreateVideoProcessor(_procEnum, 0u);
            if (_processor == null) { _log("GPU: VideoProcessor 创建失败"); return false; }
            _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0u, VideoFrameFormat.Progressive);
            SetColorSpace();
            RecreateBackbufferTargets();
            if (_bbOutView == null || _rtvBb == null) return false;

            _log("GPU: 渲染管线全就绪");
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
            OutputFrameRate = new Rational(120, 1),
            OutputWidth = (uint)w,
            OutputHeight = (uint)h,
            Usage = VideoUsage.PlaybackNormal,
        };
        return _videoDevice.CreateVideoProcessorEnumerator(desc);
    }

    private void SetColorSpace()
    {
        if (_videoContext == null || _processor == null) return;
        var inCs = new VideoProcessorColorSpace { Usage = 0, YCbCr_Matrix = 1, Nominal_Range = 1 };
        var outCs = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0 };
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0u, inCs);
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, outCs);
    }

    /// 合成线程主循环：有新帧→缩放合成→垂直同步上屏；无新帧→休眠（GPU 零负载）
    private void StartComposeLoop()
    {
        if (_composeThread != null) return;
        _composeRunning = true;
        _composeThread = new Thread(ComposeLoop) { IsBackground = true, Name = "gpu-compose" };
        _composeThread.Start();
    }

    public void RequestResize(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (w == _swapW && h == _swapH) return;
        _pendW = w; _pendH = h;
        _resizePending = true;
    }

    private void ComposeLoop()
    {
        while (_composeRunning)
        {
            var src = _src;
            long fc = src?.FrameCount ?? 0;
            if (fc == _lastFc)
            {
                Thread.Sleep(5);   // 无新帧：DWM 保持上一画面，零 GPU 开销
                continue;
            }
            _lastFc = fc;

            try
            {
                if (!EnsureDevice()) { Thread.Sleep(100); continue; }
                if (_resizePending) ApplyPendingResize();

                // 取帧 + 建视图 + Blt 同锁原子完成：保留帧的纹理归解码器池所有，
                // 若中途换帧释放，池会回收复用该表面，Blt 会读到失效/被覆写的内容（黑屏）
                lock (src.HwSync)
                {
                    if (!src.TryGetGpuFrameRaw(out var f)) continue;

                    var tex = new D3D11Texture2DT((nint)f->data[0]);
                    int slice = (int)(nint)f->data[1];
                    var inDesc = new VideoProcessorInputViewDescription
                    {
                        ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)slice },
                    };
                    using var inView = _videoDevice.CreateVideoProcessorInputView(tex, _procEnum, inDesc);
                    if (inView == null) { _log("GPU: InputView 创建失败"); continue; }

                    int srcW = f->width, srcH = f->height;
                    double s = Math.Min((double)_swapW / srcW, (double)_swapH / srcH);
                    int dw = Math.Max(1, (int)(srcW * s));
                    int dh = Math.Max(1, (int)(srcH * s));
                    int dx = (_swapW - dw) / 2;
                    int dy = (_swapH - dh) / 2;

                    _context.ClearRenderTargetView(_rtvBb, new Color4(0, 0, 0, 1));
                    _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0u, true,
                        new RawRect(0, 0, srcW, srcH));
                    _videoContext.VideoProcessorSetStreamDestRect(_processor, 0u, true,
                        new RawRect(dx, dy, dx + dw, dy + dh));
                    _videoContext.VideoProcessorBlt(_processor, _bbOutView, 0u, new[] {
                        new VideoProcessorStream { Enable = true, InputSurface = inView },
                    });
                }
                _swapChain.Present(1, PresentFlags.None);
                _presentedOnce = true;
            }
            catch (Exception ex)
            {
                _log($"GPU: 合成异常 {ex.GetType().Name}: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    private bool _presentedOnce;

    private void ApplyPendingResize()
    {
        _resizePending = false;
        _rtvBb?.Dispose(); _rtvBb = null;
        _bbOutView?.Dispose(); _bbOutView = null;
        _backBuffer?.Dispose(); _backBuffer = null;
        var rr = _swapChain.ResizeBuffers(0, (uint)_pendW, (uint)_pendH, Format.Unknown, SwapChainFlags.None);
        if (rr.Failure)
        {
            _log($"GPU: ResizeBuffers 失败 {rr}");
            return;
        }
        _swapW = _pendW; _swapH = _pendH;
        RecreateBackbufferTargets();
        _log($"GPU: 交换链调整 {_swapW}x{_swapH}");
    }

    private void RecreateBackbufferTargets()
    {
        _rtvBb?.Dispose(); _rtvBb = null;
        _bbOutView?.Dispose(); _bbOutView = null;
        _backBuffer?.Dispose(); _backBuffer = null;
        if (_swapChain == null || _device == null || _videoDevice == null || _procEnum == null) return;

        using var bb = _swapChain.GetBuffer<D3D11Texture2DT>(0u);
        _backBuffer = new D3D11Texture2DT(bb.NativePointer);
        _rtvBb = _device.CreateRenderTargetView(bb, null);
        var obDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        };
        _bbOutView = _videoDevice.CreateVideoProcessorOutputView(bb, _procEnum, obDesc);
    }

    private void TeardownDevice()
    {
        _bbOutView?.Dispose(); _bbOutView = null;
        _rtvBb?.Dispose(); _rtvBb = null;
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
