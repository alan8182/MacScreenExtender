package com.macscreextender.receiver.net

import android.content.Context
import android.net.wifi.WifiManager
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.SocketTimeoutException

data class DiscoveredMac(
    val name: String,
    val ip: String,
    val httpPort: Int,
    val lastSeenMillis: Long,
)

/**
 * 局域网设备发现：监听 Mac 端 UDP 8082 广播（2 秒/次）。
 * JSON: {"name":"..","ip":"..","port":8080,"proto":1}；6 秒无广播的条目老化剔除。
 * 部分 OEM WiFi 省电会丢广播包——UI 侧必须保留手动输 IP 入口。
 */
class DiscoveryService(private val onUpdate: (List<DiscoveredMac>) -> Unit) {

    private var socket: DatagramSocket? = null
    private var thread: Thread? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    @Volatile private var running = false
    private val macs = LinkedHashMap<String, DiscoveredMac>()

    fun start(context: Context) {
        if (running) return
        running = true
        try {
            val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wifi?.createMulticastLock("MacScreenExtender")?.apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {
        }
        thread = Thread({ loop() }, "discovery").apply { start() }
    }

    fun stop() {
        running = false
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        socket = null
        multicastLock?.release()
        multicastLock = null
        thread = null
        macs.clear()
    }

    private fun loop() {
        try {
            socket = DatagramSocket(8082).apply {
                broadcast = true
                reuseAddress = true
                soTimeout = 1000   // 周期醒来做老化剔除
            }
        } catch (e: Exception) {
            running = false
            return
        }
        val buf = ByteArray(2048)
        while (running) {
            val packet = DatagramPacket(buf, buf.size)
            try {
                socket?.receive(packet) ?: break
            } catch (_: SocketTimeoutException) {
                purgeStale()
                continue
            } catch (_: Exception) {
                break   // socket 已关闭
            }
            val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
            val mac = parse(text) ?: continue
            synchronized(macs) {
                macs[mac.ip] = mac
                purgeStaleLocked()
                onUpdate(macs.values.toList())
            }
        }
        running = false
    }

    private fun purgeStale() = synchronized(macs) {
        val before = macs.size
        purgeStaleLocked()
        if (macs.size != before) onUpdate(macs.values.toList())
    }

    private fun purgeStaleLocked() {
        val now = System.currentTimeMillis()
        macs.entries.removeAll { now - it.value.lastSeenMillis > STALE_MILLIS }
    }

    private fun parse(text: String): DiscoveredMac? = try {
        val json = JSONObject(text)
        val ip = json.optString("ip", "")
        if (ip.isEmpty()) null else DiscoveredMac(
            name = json.optString("name", ip),
            ip = ip,
            httpPort = json.optInt("port", 8080),
            lastSeenMillis = System.currentTimeMillis(),
        )
    } catch (_: Exception) {
        null
    }

    companion object {
        private const val STALE_MILLIS = 6_000L
    }
}
