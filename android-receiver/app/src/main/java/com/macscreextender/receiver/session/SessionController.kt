package com.macscreextender.receiver.session

import android.content.Context
import android.view.Surface
import com.macscreextender.receiver.input.CursorOverlayView
import com.macscreextender.receiver.input.InputSender
import com.macscreextender.receiver.media.NaluParser
import com.macscreextender.receiver.media.VideoDecoder
import com.macscreextender.receiver.net.ControlClient
import com.macscreextender.receiver.net.DiscoveredMac
import com.macscreextender.receiver.net.DiscoveryService
import com.macscreextender.receiver.net.PairResult
import com.macscreextender.receiver.net.PairingClient
import com.macscreextender.receiver.net.VideoStreamClient
import com.macscreextender.receiver.service.StreamService
import com.macscreextender.receiver.store.ConfigStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * 会话状态机（App 中枢）：发现 → 配对 → 控制通道+视频流 → 看门狗自愈。
 * 时序：HTTP 配对成功后 IP 才会被 Mac 端控制通道放行（PROTOCOL.md §0），必须先配对后连 8081；
 * 视频流等 Surface 就绪后再拉（新连接 Mac 端强制 IDR，≤1 秒出画），避免先于渲染缓冲帧。
 */
object SessionController {

    sealed class State {
        data object Idle : State()
        data class Connecting(val host: String) : State()
        data class PairingRequired(val host: String) : State()
        data class Streaming(val host: String, val label: String) : State()
        data class Failed(val message: String) : State()
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private lateinit var appContext: Context

    private val _state = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = _state.asStateFlow()

    /** 全局状态切换留痕（真机排障：adb logcat -s MSE） */
    private fun setState(next: State) {
        android.util.Log.i("MSE", "state → $next")
        _state.value = next
    }

    private val _discovered = MutableStateFlow<List<DiscoveredMac>>(emptyList())
    val discovered: StateFlow<List<DiscoveredMac>> = _discovered.asStateFlow()

    private val _toast = MutableSharedFlow<String>(extraBufferCapacity = 8)
    val toast: SharedFlow<String> = _toast.asSharedFlow()

    private var discovery: DiscoveryService? = null

    // ---- 会话组件 ----
    private var control: ControlClient? = null
    private var video: VideoStreamClient? = null
    private var decoder: VideoDecoder? = null
    private var parser: NaluParser? = null
    private var watchdogJob: Job? = null
    private var surface: Surface? = null
    private var outW = 0
    private var outH = 0

    private var host = ""
    private var httpPort = 8080
    private var sessionLabel = ""

    // SPS/PPS 就绪门：两者齐了才 start 解码器；SPS 变化触发重配
    private val paramLock = Any()
    private var storedSps: ByteArray? = null
    private var storedPps: ByteArray? = null
    private var decoderSps: ByteArray? = null

    private val videoRetries = AtomicInteger(0)
    private val videoRestarting = AtomicBoolean(false)
    private val controlRestarting = AtomicBoolean(false)

    // 光标所有权窗口（ARCHITECTURE.md §1：触摸注入活跃期由本地画光标）
    @Volatile private var touchActiveUntil = 0L
    private var cursorIdleJob: Job? = null

    @Volatile private var cursorOverlay: CursorOverlayView? = null

    fun init(context: Context) {
        if (::appContext.isInitialized) return
        appContext = context.applicationContext
        ConfigStore.init(appContext)
    }

    // ---- 发现 ----

    fun startDiscovery() {
        if (_state.value !is State.Idle && _state.value !is State.Failed) return
        if (discovery != null) return
        discovery = DiscoveryService { list -> _discovered.value = list }.also {
            it.start(appContext)
        }
    }

    fun stopDiscovery() {
        discovery?.stop()
        discovery = null
        _discovered.value = emptyList()
    }

    // ---- 连接 / 配对 ----

    fun startSession(host: String, httpPort: Int, label: String) {
        failSession(null)
        this.host = host
        this.httpPort = httpPort
        this.sessionLabel = label.ifEmpty { host }
        setState(State.Connecting(host))
        scope.launch(Dispatchers.IO) {
            val token = ConfigStore.token
            if (token.isEmpty()) {
                setState(State.PairingRequired(host))
                return@launch
            }
            when (PairingClient.pairCheck(host, httpPort, token)) {
                PairingClient.PairCheckResult.Ok -> beginStreaming()
                PairingClient.PairCheckResult.NeedPair -> {
                    ConfigStore.clearPairing()
                    setState(State.PairingRequired(host))
                }
                is PairingClient.PairCheckResult.Error -> {
                    setState(State.Failed("无法连接 $host：网络不可达"))
                }
            }
        }
    }

    fun submitPairCode(code: String) {
        if (_state.value !is State.PairingRequired) return
        scope.launch(Dispatchers.IO) {
            when (val result = PairingClient.pair(host, httpPort, code)) {
                is PairResult.Success -> {
                    ConfigStore.token = result.token
                    setState(State.Connecting(host))
                    beginStreaming()
                }
                is PairResult.WrongCode -> _toast.emit("配对码不正确")
                is PairResult.Error -> _toast.emit("配对失败：${result.message}")
            }
        }
    }

    fun cancelPairing() {
        if (_state.value is State.PairingRequired) setState(State.Idle)
    }

    private fun beginStreaming() {
        if (_state.value is State.Failed) return
        StreamService.start(appContext)
        startControl()
        setState(State.Streaming(host, sessionLabel))
        startWatchdog()
    }

    private fun startControl() {
        control = ControlClient(
            host = host,
            onCursor = { nx, ny, visible -> cursorOverlay?.update(nx, ny, visible) },
            onClosed = { reason ->
                if (_state.value is State.Streaming && controlRestarting.compareAndSet(false, true)) {
                    scope.launch {
                        delay(RESTART_BACKOFF_MS)
                        controlRestarting.set(false)
                        if (_state.value is State.Streaming) {
                            control?.close()
                            startControl()
                        }
                    }
                }
            },
        ).also { it.start() }
    }

    // ---- Surface / 视频流 ----

    /** FullscreenHost 的 surfaceChanged 回调（含尺寸） */
    fun onSurfaceAvailable(surface: Surface, width: Int, height: Int) {
        this.surface = surface
        outW = width.coerceAtLeast(640)
        outH = height.coerceAtLeast(480)
        startVideo()
    }

    fun onSurfaceDestroyed() {
        stopVideoOnly()
        surface = null
    }

    private fun startVideo() {
        val s = surface ?: return
        stopVideoOnly()
        val d = VideoDecoder(outW, outH) { msg -> onVideoFatal(msg) }
        decoder = d
        val p = NaluParser(
            onNalu = { nalu -> d.submit(nalu) },
            onSps = { sps -> onParameterSet(sps = sps) },
            onPps = { pps -> onParameterSet(pps = pps) },
        )
        parser = p
        synchronized(paramLock) {
            storedSps = null
            storedPps = null
            decoderSps = null
        }
        video = VideoStreamClient(
            host = host,
            httpPort = httpPort,
            token = ConfigStore.token,
            width = outW,
            height = outH,
            parser = p,
            onFirstByte = { videoRetries.set(0) },
            onEnded = { scheduleVideoRestart() },
            onNeedPair = {
                ConfigStore.clearPairing()
                failSession("配对已失效，请重新输入配对码")
                setState(State.PairingRequired(host))
            },
        ).also { it.start(scope) }
    }

    private fun onParameterSet(sps: ByteArray? = null, pps: ByteArray? = null) {
        synchronized(paramLock) {
            if (sps != null) storedSps = sps
            if (pps != null) storedPps = pps
            val s = storedSps ?: return
            val pp = storedPps ?: return
            val d = decoder ?: return
            val sf = surface ?: return
            when {
                !d.isActive -> d.start(sf, s, pp)
                decoderSps?.contentEquals(s) != true -> d.reconfigure(s, pp)   // 远端虚拟屏重建
            }
            decoderSps = s
        }
    }

    private fun stopVideoOnly() {
        video?.cancel()
        video = null
        decoder?.release()
        decoder = null
        parser = null
    }

    private fun scheduleVideoRestart() {
        if (_state.value !is State.Streaming) return
        if (!videoRestarting.compareAndSet(false, true)) return
        val retries = videoRetries.incrementAndGet()
        if (retries > MAX_VIDEO_RETRIES) {
            videoRestarting.set(false)
            failSession("视频流多次中断，请检查网络")
            return
        }
        scope.launch {
            delay(minOf(1000L * retries, RESTART_BACKOFF_MS * 2))
            videoRestarting.set(false)
            if (_state.value is State.Streaming && surface != null) startVideo()
        }
    }

    private fun onVideoFatal(msg: String) {
        scope.launch { _toast.emit("解码异常：$msg") }
        scheduleVideoRestart()
    }

    // ---- 看门狗 ----

    private fun startWatchdog() {
        watchdogJob?.cancel()
        watchdogJob = scope.launch {
            while (isActive && _state.value is State.Streaming) {
                delay(2000)
                val now = System.currentTimeMillis()
                val v = video
                if (v != null && v.lastDataMillis != 0L &&
                    now - v.lastDataMillis > VIDEO_STALL_MS &&
                    videoRestarting.compareAndSet(false, true)
                ) {
                    v.cancel()   // 读超时 8s 兜底之外的主动断流重拉
                    videoRestarting.set(false)
                    scheduleVideoRestart()
                }
                val c = control
                if (c != null && c.lastRxMillis != 0L &&
                    now - c.lastRxMillis > CONTROL_STALL_MS &&
                    controlRestarting.compareAndSet(false, true)
                ) {
                    scope.launch {
                        delay(RESTART_BACKOFF_MS)
                        controlRestarting.set(false)
                        if (_state.value is State.Streaming) {
                            c.close()
                            control = null
                            startControl()
                        }
                    }
                }
            }
        }
    }

    // ---- 触摸输入门面 ----

    val input: InputSender = object : InputSender {
        override fun move(nx: Float, ny: Float) {
            markTouchActivity()
            control?.sendMove(nx, ny)
        }

        override fun button(btn: Int, down: Boolean, nx: Float, ny: Float) {
            markTouchActivity()
            control?.sendButton(btn, down, nx, ny)
        }

        override fun wheel(dx: Float, dy: Float) {
            markTouchActivity()
            control?.sendWheel(dx, dy)
        }
    }

    private fun markTouchActivity() {
        touchActiveUntil = System.currentTimeMillis() + CURSOR_ACTIVE_MS
        cursorOverlay?.setTouchActive(true)
        cursorIdleJob?.cancel()
        cursorIdleJob = scope.launch {
            delay(CURSOR_ACTIVE_MS + 100)
            if (System.currentTimeMillis() >= touchActiveUntil) {
                cursorOverlay?.setTouchActive(false)
            }
        }
    }

    fun registerCursorOverlay(overlay: CursorOverlayView?) {
        cursorOverlay = overlay
        if (overlay == null) cursorIdleJob?.cancel()
    }

    // ---- 断开 ----

    fun disconnect() {
        if (_state.value is State.Idle) return
        failSession(null)
        setState(State.Idle)
    }

    private fun failSession(message: String?) {
        watchdogJob?.cancel()
        watchdogJob = null
        stopVideoOnly()
        control?.close()
        control = null
        surface = null
        cursorOverlay?.setTouchActive(false)
        StreamService.stop(appContext)
        if (message != null) setState(State.Failed(message))
    }

    private const val VIDEO_STALL_MS = 6_000L
    private const val CONTROL_STALL_MS = 6_000L
    private const val RESTART_BACKOFF_MS = 1_000L
    private const val MAX_VIDEO_RETRIES = 6
    private const val CURSOR_ACTIVE_MS = 300L
}
