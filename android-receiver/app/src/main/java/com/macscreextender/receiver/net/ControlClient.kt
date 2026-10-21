package com.macscreextender.receiver.net

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.io.DataInputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.TimeUnit

/**
 * 控制通道：TCP 8081，16 字节定长小端包（协议见 PROTOCOL.md §4）。
 * 发送：触摸/滚轮/关键帧请求；接收：type 7 心跳（原样回传）与 type 8 光标回报。
 * 触摸事件在 UI 线程产生——所有写出经由独立写线程的 FIFO 队列，
 * 既避开 NetworkOnMainThreadException，又保证 down/move/up 顺序。
 */
class ControlClient(
    private val host: String,
    private val port: Int = 8081,
    private val onCursor: ((nx: Float, ny: Float, visible: Boolean) -> Unit)? = null,
    private val onClosed: ((reason: String?) -> Unit)? = null,
) {
    @Volatile var lastRxMillis: Long = 0L
        private set

    private val scope = CoroutineScope(Dispatchers.IO + Job())
    private var socket: Socket? = null
    private var out: java.io.OutputStream? = null
    private val outbox = LinkedBlockingDeque<ByteArray>()

    @Volatile private var running = false

    fun start() {
        if (running) return
        running = true
        scope.launch { readLoop() }
        scope.launch { writeLoop() }
    }

    private suspend fun readLoop() {
        var failure: String? = null
        try {
            val s = Socket()
            s.tcpNoDelay = true
            s.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
            socket = s
            out = s.getOutputStream()
            val input = DataInputStream(s.getInputStream())
            val rx = ByteArray(16)
            while (true) {
                input.readFully(rx)
                lastRxMillis = System.currentTimeMillis()
                when (rx[0].toInt()) {
                    TYPE_HEARTBEAT -> outbox.offerLast(rx.copyOf())   // 原样回传（经写线程保序）
                    TYPE_CURSOR -> {
                        val bb = ByteBuffer.wrap(rx).order(ByteOrder.LITTLE_ENDIAN)
                        onCursor?.invoke(bb.getFloat(4), bb.getFloat(8), rx[12].toInt() == 1)
                    }
                }
            }
        } catch (e: Exception) {
            failure = e.message
        } finally {
            closeQuietly()
        }
        if (running) onClosed?.invoke(failure)
    }

    private fun writeLoop() {
        while (running) {
            val packet = try {
                outbox.pollFirst(200, TimeUnit.MILLISECONDS)
            } catch (_: InterruptedException) {
                break
            } ?: continue
            val o = out ?: continue
            try {
                o.write(packet)
                o.flush()
            } catch (_: Exception) {
                // 断线由读循环发现并回调，发送侧静默
            }
        }
    }

    // ---- 发送端（UI 线程可调用：仅入队，零阻塞）----

    fun sendMove(nx: Float, ny: Float) = send(TYPE_MOVE) { putFloat(4, nx); putFloat(8, ny) }

    /** btn: 0=左键 1=右键。按钮码必须写 byte5——byte4..7 是 nx 浮点（历史 bug 见 PROTOCOL.md §4.4） */
    fun sendButton(btn: Int, down: Boolean, nx: Float, ny: Float) =
        send(if (down) TYPE_BUTTON_DOWN else TYPE_BUTTON_UP) {
            put(5, btn.toByte()); putFloat(4, nx); putFloat(8, ny)
        }

    /** dy>0 = 向上滚（与 Windows 滚轮上、iPad 手指下滑一致） */
    fun sendWheel(dx: Float, dy: Float) = send(TYPE_WHEEL) { putFloat(4, dx); putFloat(8, dy) }

    fun requestKeyframe() = send(TYPE_KEYFRAME_REQUEST) { }

    private inline fun send(type: Int, payload: ByteBuffer.() -> Unit) {
        if (!running) return
        val packet = ByteArray(16)
        ByteBuffer.wrap(packet).order(ByteOrder.LITTLE_ENDIAN).apply {
            put(0, type.toByte())
            payload()
        }
        outbox.offerLast(packet)
    }

    fun close() {
        running = false
        outbox.clear()
        closeQuietly()
        scope.coroutineContext[Job]?.cancel()
    }

    private fun closeQuietly() {
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        socket = null
        out = null
    }

    companion object {
        const val TYPE_MOVE = 1
        const val TYPE_BUTTON_DOWN = 2
        const val TYPE_BUTTON_UP = 3
        const val TYPE_WHEEL = 4
        const val TYPE_HEARTBEAT = 7
        const val TYPE_CURSOR = 8
        const val TYPE_KEYFRAME_REQUEST = 9
        private const val CONNECT_TIMEOUT_MS = 4000
    }
}
