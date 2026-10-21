package com.macscreextender.receiver.media

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicBoolean

/**
 * MediaCodec 硬解 → Surface 零拷贝直出（异步模式，低延迟优先）。
 * - csd-0/csd-1 由首组 SPS/PPS（含起始码）配置；SPS 变化（多端并发触发虚拟屏重建）→ 整体重配自愈
 * - 输出帧立即 releaseOutputBuffer(render=true)，无 vsync 门控、无重排缓冲
 * - 流内无时间戳（PROTOCOL.md §3.2），解码 PTS 用单调时钟
 *
 * 异步模式要点：start() 时 MediaCodec 会一次性把全部 input buffer 经 onInputBufferAvailable
 * 交付，此后只有"已排队"的 buffer 被消化才会再交付新的。因此必须维护空闲 input index 池，
 * NALU 到达时随时取用（feed），否则启动瞬间的回调全部落空 → 永久喂不进数据（黑屏）。
 */
class VideoDecoder(
    private val width: Int,
    private val height: Int,
    private val onError: (String) -> Unit,
) {
    private val thread = HandlerThread("VideoDecoder").apply { start() }
    private val handler = Handler(thread.looper)
    private val lock = Object()

    /** 待解码 NALU（start 前到达的先缓存） */
    private val pendingNalus = ArrayDeque<ByteArray>()

    /** 已交付、尚未排队的空闲 input buffer index */
    private val freeInputs = ArrayDeque<Int>()

    private val released = AtomicBoolean(false)

    @Volatile private var started = false
    val isActive: Boolean get() = started
    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    private var sps: ByteArray? = null
    private var pps: ByteArray? = null

    /** 提供视频输出 Surface 后（拿到首组参数集即可）调用 */
    @Synchronized
    fun start(surface: Surface, sps: ByteArray, pps: ByteArray) {
        if (released.get() || started) return
        this.surface = surface
        this.sps = sps
        this.pps = pps
        createCodec(surface, sps, pps)
    }

    /** SPS 变化（远端虚拟屏重建）→ 释放并按新参数重建 */
    @Synchronized
    fun reconfigure(sps: ByteArray, pps: ByteArray) {
        if (released.get() || !started) return
        val s = surface ?: return
        destroyCodec()
        this.sps = sps
        this.pps = pps
        createCodec(s, sps, pps)
    }

    /** 读流协程调用；start 前到达的 NALU 缓存（上限内），start 后随时喂入 */
    fun submit(nalu: ByteArray) {
        if (released.get()) return
        synchronized(lock) {
            if (pendingNalus.size > PENDING_CAP) pendingNalus.removeFirst()
            pendingNalus.addLast(nalu)
        }
        feed()
    }

    fun release() {
        if (!released.compareAndSet(false, true)) return
        handler.post {
            destroyCodec()
            thread.quitSafely()
        }
    }

    private fun createCodec(surface: Surface, sps: ByteArray, pps: ByteArray) {
        try {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
                setByteBuffer("csd-0", ByteBuffer.wrap(sps))
                setByteBuffer("csd-1", ByteBuffer.wrap(pps))
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, MAX_INPUT_SIZE)
                setInteger(MediaFormat.KEY_PRIORITY, 0)
                if (Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
            val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            c.configure(format, surface, null, 0)
            c.setCallback(callback, handler)
            c.start()
            codec = c
            started = true
            android.util.Log.i(TAG, "解码器启动 ${width}x${height} lowLatency=${Build.VERSION.SDK_INT >= 30}")
        } catch (e: Exception) {
            onError("解码器创建失败: ${e.message}")
        }
    }

    private fun destroyCodec() {
        synchronized(lock) {
            started = false
            pendingNalus.clear()
            freeInputs.clear()
        }
        try {
            codec?.stop()
        } catch (_: Exception) {
        }
        try {
            codec?.release()
        } catch (_: Exception) {
        }
        codec = null
        // Surface 由持有方（FullscreenHost）管理生命周期，这里不释放
    }

    /** 把 pending NALU 尽量多地排进空闲 input buffer（读流线程与解码回调线程都会触发） */
    private fun feed() {
        synchronized(lock) {
            while (started) {
                val idx = freeInputs.removeFirstOrNull() ?: break
                val nalu = pendingNalus.removeFirstOrNull()
                if (nalu == null) {
                    freeInputs.addFirst(idx)   // 没数据，index 还回去
                    break
                }
                val c = codec ?: break
                try {
                    val input = c.getInputBuffer(idx) ?: continue
                    input.clear()
                    if (nalu.size > input.capacity()) continue   // 异常大的 NALU 丢弃防崩溃
                    input.put(nalu)
                    c.queueInputBuffer(idx, 0, nalu.size, System.nanoTime() / 1000, 0)
                } catch (e: Exception) {
                    onError("解码输入异常: ${e.message}")
                    break
                }
            }
        }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {
            synchronized(lock) { freeInputs.addLast(index) }
            feed()
        }

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            try {
                codec.releaseOutputBuffer(index, true)   // 立即上屏，零延迟
            } catch (e: Exception) {
                onError("解码输出异常: ${e.message}")
            }
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) = Unit

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            onError(e.message ?: "解码器错误")
        }
    }

    companion object {
        private const val TAG = "MSE.Decoder"
        private const val PENDING_CAP = 64
        private const val MAX_INPUT_SIZE = 4 * 1024 * 1024   // 80Mbps 下 IDR 帧不小
    }
}
