using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace MacScreenReceiver;

/// <summary>
/// FFmpeg 软解码器：拉 http 裸 Annex-B H.264 流 → BGRA 帧缓冲（自转自有节拍，无时间戳依赖）。
/// 断流自动重连。渲染端 BlitOrDraw 在锁内拿原生缓冲直绘，零拷贝托管堆。
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    readonly string _url;
    Thread _thread;
    volatile bool _running;

    AVFormatContext* _fmt;
    AVCodecContext* _dec;
    SwsContext* _sws;
    AVFrame* _frame;
    AVFrame* _swFrame;                 // 硬解帧 → 内存下载目标
    AVPacket* _pkt;
    AVBufferRef* _hwDev;               // d3d11va 设备（跨重开复用，Dispose 时释放）
    int _videoIndex = -1;
    int _srcFormat = -1;
    int _consecSendErr;
    // 硬解熔断（实例级）：驱动不配合时本次解码器退回软解；新连接重新尝试
    private bool _hwDisabled;

    IntPtr _bgra = IntPtr.Zero;
    int _w, _h;
    long _frameCount;
    readonly object _sync = new();

    // 本地光标（Mac 60Hz 回报位置，解码后混入帧缓冲 → 单次上屏，原理性无闪烁）
    byte[] _sprite;
    int _spriteW, _spriteH;
    float _curNx, _curNy;
    bool _curVisible;
    readonly object _cursorLock = new();

    public object Sync => _sync;
    public IntPtr Bgra => _bgra;
    public int Width => _w;
    public int Height => _h;
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// 每解出一帧回调（Form 端用于"帧到即画"，消除轮询抖动；Form 侧负责合并防洪水）
    public Action? FrameReady;

    /// GPU 渲染器（非空且硬解帧时：解码线程直接 GPU 上屏，零内存拷贝）
    public GpuVideoRenderer? Renderer;
    /// 渲染器提供的 D3D11VA 设备引用工厂（解码纹理与渲染同设备的关键）
    public Func<IntPtr>? CreateDecoderHwRef;
    private readonly Action<string> _log;
    /// GPU 上屏连续失败（Form 据此切换状态显示；解码继续走 CPU 下载路径）
    public volatile bool GpuFailed;
    public bool IsGpuRendering => Renderer != null && !GpuFailed;
    private int _gpuFailCount;

    /// 硬解设备的 ID3D11Device 原生指针（渲染器用它搭建同设备管线）
    public IntPtr HwD3DDevice
    {
        get
        {
            if (_hwDev == default) return IntPtr.Zero;
            var ctx = (AVHWDeviceContext*)_hwDev->data;
            if (ctx->type != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA) return IntPtr.Zero;
            var d3d = (AVD3D11VADeviceContext*)ctx->hwctx;
            return (IntPtr)d3d->device;
        }
    }

    /// 光标精灵（BGRA 直通 alpha），解码后每帧混入
    public void SetCursorSprite(byte[] bgra, int w, int h)
    {
        lock (_cursorLock) { _sprite = bgra; _spriteW = w; _spriteH = h; }
    }

    /// Mac 端回报的光标位置（归一化，tip 在左上）
    public void UpdateCursor(float nx, float ny, bool visible)
    {
        lock (_cursorLock) { _curNx = nx; _curNy = ny; _curVisible = visible; }
    }

    static VideoDecoder()
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
        ffmpeg.avformat_network_init();
    }

    public VideoDecoder(string url) { _url = url; _log = m => Log.Write(m); }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ffmpeg-decode" };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(6000); } catch { }   // rw_timeout 5s 内必然退出读循环
        CloseLocked();
        lock (_sync)
        {
            if (_bgra != IntPtr.Zero) { Marshal.FreeHGlobal(_bgra); _bgra = IntPtr.Zero; }
        }
        if (_hwDev != null)
        {
            var dev = _hwDev; _hwDev = null;
            ffmpeg.av_buffer_unref(&dev);
        }
    }

    void Loop()
    {
        while (_running)
        {
            try
            {
                if (!Open()) { Thread.Sleep(3000); continue; }
                ReadLoop();
            }
            catch { }
            CloseLocked();
            if (_running) Thread.Sleep(2000);   // 断流后稍等重连
        }
    }

    bool Open()
    {
        var fmt = ffmpeg.avformat_alloc_context();
        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "fflags", "nobuffer", 0);
        ffmpeg.av_dict_set(&opts, "flags", "low_delay", 0);
        ffmpeg.av_dict_set(&opts, "probesize", "131072", 0);
        ffmpeg.av_dict_set(&opts, "analyzeduration", "500000", 0);
        ffmpeg.av_dict_set(&opts, "rw_timeout", "5000000", 0);   // 5s，防止半开 TCP 卡死解码线程
        // 不强制指定封装：自动探测 H.264/HEVC 裸流（Mac 端 --codec 决定）
        int ret = ffmpeg.avformat_open_input(&fmt, _url, null, &opts);
        ffmpeg.av_dict_free(&opts);
        if (ret < 0)
        {
            _log($"解码器: 打开流失败 err={ret}");
            _fmt = null;
            return false;
        }
        _fmt = fmt;

        if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
        {
            _log("解码器: find_stream_info 失败");
            return false;
        }
        _videoIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (_videoIndex < 0) return false;

        var par = _fmt->streams[_videoIndex]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec == null) return false;
        _dec = ffmpeg.avcodec_alloc_context3(codec);
        if (ffmpeg.avcodec_parameters_to_context(_dec, par) < 0) return false;
        // 帧级多线程会预缓冲多帧增加延迟；硬解不需要线程（也规避
        // 帧线程+d3d11va+外部设备的崩溃组合）；软解 HEVC 用 2 线程换吞吐

        // d3d11va 硬解：解码（熵解码+帧重建，占 CPU 大头）交给显卡。
        // 设备由 ffmpeg 自建（实测稳定）；渲染器从解码帧上"采用"同一设备，帧零拷贝上屏。
        // 硬解不可用时自动退回软解
        bool hwAttached = false;
        if (!_hwDisabled && _hwDev == null)
        {
            AVBufferRef* dev = null;
            if (ffmpeg.av_hwdevice_ctx_create(&dev, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) == 0)
            {
                _hwDev = dev;
            }
            else
            {
                _hwDisabled = true;
            }
        }
        if (_hwDev != null && !_hwDisabled)
        {
            _dec->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDev);
            hwAttached = true;
        }
        _srcFormat = -1;
        _consecSendErr = 0;

        AVDictionary* dopts = null;
        ffmpeg.av_dict_set(&dopts, "flags", "low_delay", 0);
        _log($"解码器: 打开 ({(hwAttached ? "硬件" : "软解")})");
        int openRet = ffmpeg.avcodec_open2(_dec, codec, &dopts);
        if (openRet < 0 && hwAttached)
        {
            // 硬解初始化失败：摘掉 hw_device_ctx 用软解重开一次
            ffmpeg.av_buffer_unref(&_dec->hw_device_ctx);
            _hwDisabled = true;
            hwAttached = false;
            if (ffmpeg.avcodec_open2(_dec, codec, &dopts) < 0) return false;
        }
        else if (openRet < 0) return false;
        if (hwAttached) _dec->thread_count = 1;   // 硬解路径：单线程（帧线程与 d3d11va 组合有崩溃风险）

        _frame = ffmpeg.av_frame_alloc();
        _swFrame = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
        _log($"解码器: 就绪 {_dec->width}x{_dec->height} 线程={_dec->thread_count}");
        return true;
    }

    void ReadLoop()
    {
        var pkt = _pkt;
        long lastSeen = _frameCount;
        var stallStart = DateTime.UtcNow;
        bool firstPacketLogged = false;
        while (_running && ffmpeg.av_read_frame(_fmt, pkt) >= 0)
        {
            if (!firstPacketLogged) { firstPacketLogged = true; _log("解码器: 收到首包"); }
            try
            {
                if (pkt->stream_index == _videoIndex)
                {
                    int sendRet = ffmpeg.avcodec_send_packet(_dec, pkt);
                    if (sendRet == 0 || sendRet == unchecked((int)ffmpeg.EAGAIN))
                    {
                        if (sendRet == 0) _consecSendErr = 0;
                        while (ffmpeg.avcodec_receive_frame(_dec, _frame) >= 0)
                        {
                            if (Renderer != null && !GpuFailed &&
                                _frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                            {
                                // GPU 直渲（解码线程执行；CPU 零拷贝）
                                float cnx, cny; bool cvis;
                                lock (_cursorLock) { cnx = _curNx; cny = _curNy; cvis = _curVisible; }
                                if (Renderer.UploadFrame(_frame))
                                {
                                    lock (_sync) { _w = _frame->width; _h = _frame->height; }
                                    _gpuFailCount = 0;
                                    _frameCount++;
                                    if (_frameCount == 1) _log($"解码器: GPU 首帧已出 {_w}x{_h}");
                                    FrameReady?.Invoke();
                                    continue;
                                }
                                if (++_gpuFailCount > 300) GpuFailed = true;   // 容忍初始探测期的失败，300 帧（约 5-15 秒）仍全败才熔断
                                ConvertAndStore(_frame);
                            }
                            else
                            {
                                ConvertAndStore(_frame);
                            }
                        }
                    }
                    else
                    {
                        // 硬解连续失败 → 熔断回软解重开
                        if (++_consecSendErr > 50 && _hwDev != null && !_hwDisabled)
                        {
                            _hwDisabled = true;
                            break;
                        }
                    }
                }
            }
            finally { ffmpeg.av_packet_unref(pkt); }

            // 卡死自愈：网络在来包但 6 秒解不出新帧（任何原因的解码器僵死）→ 跳出重开
            if (_frameCount != lastSeen) { lastSeen = _frameCount; stallStart = DateTime.UtcNow; }
            else if ((DateTime.UtcNow - stallStart).TotalSeconds > 6) { _log("解码器: 6 秒无新帧，重建"); break; }
        }
        _log($"解码器: 读循环退出 (running={_running})");
    }

    /// 解码帧 → BGRA 原生缓冲（渲染端 StretchDIBits 直接用）。硬解帧先下载到内存
    void ConvertAndStore(AVFrame* srcFrame)
    {
        AVFrame* f = srcFrame;
        bool hw = srcFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
        if (hw)
        {
            if (_swFrame == null) return;
            ffmpeg.av_frame_unref(_swFrame);
            if (ffmpeg.av_hwframe_transfer_data(_swFrame, srcFrame, 0) != 0) return;   // 丢这一帧，不崩
            f = _swFrame;
        }

        int w = f->width, h = f->height, fmt = f->format;
        lock (_sync)
        {
            if (w != _w || h != _h || fmt != _srcFormat)
            {
                if (_bgra != IntPtr.Zero) Marshal.FreeHGlobal(_bgra);
                _bgra = Marshal.AllocHGlobal(checked(w * 4 * h));
                _w = w; _h = h; _srcFormat = fmt;
                _sws = ffmpeg.sws_getCachedContext(_sws, w, h, (AVPixelFormat)fmt,
                         w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                         (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            }
            if (_sws == null) return;
            byte* dst = (byte*)_bgra;
            byte*[] dstSlice = { dst };
            int[] dstStride = { w * 4 };
            ffmpeg.sws_scale(_sws, f->data, f->linesize, 0, h, dstSlice, dstStride);

            float cnx, cny; bool cvis; byte[] spr; int spw, sph;
            lock (_cursorLock) { cnx = _curNx; cny = _curNy; cvis = _curVisible; spr = _sprite; spw = _spriteW; sph = _spriteH; }
            if (cvis && spr != null) BlendCursor(dst, w, h, cnx, cny, spr, spw, sph);

            _frameCount++;
        }
        if (_frameCount == 1) _log($"解码器: 首帧已出 {w}x{h}");
        FrameReady?.Invoke();
    }

    /// 光标精灵 alpha 混入帧缓冲（BGRA，tip 对齐 nx/ny）
    static void BlendCursor(byte* dst, int w, int h, float nx, float ny, byte[] spr, int spw, int sph)
    {
        int x0 = (int)(nx * w), y0 = (int)(ny * h);
        int rowBytes = w * 4;
        fixed (byte* sp = spr)
        {
            for (int r = 0; r < sph; r++)
            {
                int dy = y0 + r;
                if (dy < 0 || dy >= h) continue;
                for (int c = 0; c < spw; c++)
                {
                    int dx = x0 + c;
                    if (dx < 0 || dx >= w) continue;
                    int s = (r * spw + c) * 4;
                    int a = sp[s + 3];
                    if (a == 0) continue;
                    int d = dy * rowBytes + dx * 4;
                    if (a == 255)
                    {
                        dst[d] = sp[s]; dst[d + 1] = sp[s + 1]; dst[d + 2] = sp[s + 2];
                    }
                    else
                    {
                        dst[d] = (byte)((sp[s] * a + dst[d] * (255 - a)) / 255);
                        dst[d + 1] = (byte)((sp[s + 1] * a + dst[d + 1] * (255 - a)) / 255);
                        dst[d + 2] = (byte)((sp[s + 2] * a + dst[d + 2] * (255 - a)) / 255);
                    }
                    dst[d + 3] = 255;
                }
            }
        }
    }

    void CloseLocked()
    {
        var pkt = _pkt; var frame = _frame; var swFrame = _swFrame; var dec = _dec; var fmt = _fmt; var sws = _sws;
        _pkt = null; _frame = null; _swFrame = null; _dec = null; _fmt = null; _sws = null;
        if (pkt != null) ffmpeg.av_packet_free(&pkt);
        if (frame != null) ffmpeg.av_frame_free(&frame);
        if (swFrame != null) ffmpeg.av_frame_free(&swFrame);
        if (dec != null) ffmpeg.avcodec_free_context(&dec);
        if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        if (sws != null) ffmpeg.sws_freeContext(sws);
    }
}
