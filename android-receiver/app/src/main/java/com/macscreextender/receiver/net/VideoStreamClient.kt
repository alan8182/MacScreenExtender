package com.macscreextender.receiver.net

import com.macscreextender.receiver.media.NaluParser
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import okhttp3.Call
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * /raw 视频流拉取（HTTP chunked 无限流，PROTOCOL.md §3）。
 * 阻塞读循环跑在 IO 协程；读超时 8s 兜底流卡死（另有应用层 6s 看门狗）。
 */
class VideoStreamClient(
    private val host: String,
    private val httpPort: Int,
    private val token: String,
    private val width: Int,
    private val height: Int,
    private val parser: NaluParser,
    private val onFirstByte: () -> Unit,
    private val onEnded: () -> Unit,
    private val onNeedPair: () -> Unit,
) {
    @Volatile var lastDataMillis: Long = 0L
        private set

    private val client = OkHttpClient.Builder()
        .connectTimeout(4, TimeUnit.SECONDS)
        .readTimeout(8, TimeUnit.SECONDS)
        .build()

    @Volatile private var call: Call? = null
    @Volatile private var cancelled = false

    fun start(scope: CoroutineScope): Job = scope.launch(Dispatchers.IO) { run() }

    fun cancel() {
        cancelled = true
        try {
            call?.cancel()
        } catch (_: Exception) {
        }
    }

    private fun run() {
        try {
            val request = Request.Builder()
                .url("http://$host:$httpPort/raw?token=$token&w=$width&h=$height")
                .build()
            val c = client.newCall(request)
            call = c
            c.execute().use { resp ->
                if (cancelled) return
                when {
                    resp.code == 403 -> {
                        android.util.Log.w(TAG, "视频流 403 NEED-PAIR")
                        onNeedPair()
                        return
                    }
                    !resp.isSuccessful -> {
                        android.util.Log.w(TAG, "视频流 HTTP ${resp.code}")
                        onEnded()
                        return
                    }
                }
                android.util.Log.i(TAG, "视频流已连接 ${width}x${height}")
                val input = resp.body?.byteStream() ?: run { onEnded(); return }
                parser.reset()
                val buf = ByteArray(64 * 1024)
                var first = true
                while (true) {
                    val n = input.read(buf)
                    if (n < 0) break
                    lastDataMillis = System.currentTimeMillis()
                    if (first) {
                        first = false
                        onFirstByte()
                        android.util.Log.i(TAG, "视频流首字节到达")
                    }
                    parser.append(buf, n)
                }
                if (!cancelled) {
                    android.util.Log.w(TAG, "视频流 EOF（服务端关闭）")
                    onEnded()
                }
            }
        } catch (e: IOException) {
            if (!cancelled) {
                android.util.Log.w(TAG, "视频流中断: ${e.message}")
                onEnded()
            }
        } catch (e: Exception) {
            if (!cancelled) onEnded()
        }
    }

    companion object {
        private const val TAG = "MSE.Video"
    }
}
