package com.macscreextender.receiver.media

/**
 * Annex-B H.264 NALU 切分与参数集提取（移植 ipad-receiver NALUParser.swift，安卓无需 AVCC 转换）。
 * 输出：每个完整 NALU（含 00 00 00 01 起始码，MediaCodec 直接可吃）。
 * SPS/PPS 单独回调（nalType 7/8），并带 SPS 内容变化检测（多端并发时虚拟屏重建 → 流内新 SPS）。
 * 单线程使用（视频读流协程内）。
 */
class NaluParser(
    private val onNalu: (ByteArray) -> Unit,
    private val onSps: (ByteArray) -> Unit,
    private val onPps: (ByteArray) -> Unit,
) {
    private var buf = ByteArray(256 * 1024)
    private var size = 0
    private var lastSps: ByteArray? = null

    fun reset() {
        size = 0
        lastSps = null
    }

    fun append(data: ByteArray, len: Int) {
        ensureCapacity(size + len)
        System.arraycopy(data, 0, buf, size, len)
        size += len
        process()
    }

    private fun process() {
        var searchIndex = 0
        while (true) {
            val sc = findStartCode(searchIndex) ?: return
            val naluStart = sc.last + 1
            val next = findStartCode(naluStart) ?: return   // 尾部不完整，等更多数据
            val naluEnd = next.first
            if (naluEnd > naluStart) emit(naluStart, naluEnd)
            System.arraycopy(buf, naluEnd, buf, 0, size - naluEnd)
            size -= naluEnd
            searchIndex = 0
        }
    }

    private fun emit(naluStart: Int, naluEnd: Int) {
        val payloadLen = naluEnd - naluStart
        val out = ByteArray(payloadLen + START_CODE.size)
        START_CODE.copyInto(out)
        System.arraycopy(buf, naluStart, out, START_CODE.size, payloadLen)
        when (buf[naluStart].toInt() and 0x1F) {
            NAL_SPS -> {
                // 带 start code 上报：控制器同时用它做 MediaCodec csd-0（csd 必须含起始码）
                if (lastSps == null || !lastSps!!.contentEquals(out)) {
                    lastSps = out.copyOf()
                    onSps(out)
                }
            }
            NAL_PPS -> onPps(out)
        }
        onNalu(out)
    }

    /** 返回起始码所在区间（3 字节 000001 或 4 字节 00000001） */
    private fun findStartCode(from: Int): IntRange? {
        var i = from
        while (i + 2 < size) {
            if (buf[i] == 0.toByte() && buf[i + 1] == 0.toByte()) {
                if (buf[i + 2] == 1.toByte()) return i..(i + 2)
                if (i + 3 < size && buf[i + 2] == 0.toByte() && buf[i + 3] == 1.toByte()) return i..(i + 3)
            }
            i++
        }
        return null
    }

    private fun ensureCapacity(capacity: Int) {
        if (buf.size >= capacity) return
        var newSize = buf.size
        while (newSize < capacity) newSize *= 2
        buf = buf.copyOf(newSize)
    }

    companion object {
        private val START_CODE = byteArrayOf(0, 0, 0, 1)
        private const val NAL_SPS = 7
        private const val NAL_PPS = 8
    }
}
