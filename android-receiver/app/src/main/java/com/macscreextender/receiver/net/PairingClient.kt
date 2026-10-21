package com.macscreextender.receiver.net

import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.util.concurrent.TimeUnit

/** 配对与鉴权检查（阻塞调用，须在 IO 线程执行）。协议见 android-receiver/PROTOCOL.md §2 */
object PairingClient {

    sealed class PairCheckResult {
        data object Ok : PairCheckResult()
        data object NeedPair : PairCheckResult()
        data class Error(val message: String) : PairCheckResult()
    }

    private val client = OkHttpClient.Builder()
        .connectTimeout(4, TimeUnit.SECONDS)
        .readTimeout(4, TimeUnit.SECONDS)
        .build()

    /** 验证 token 是否有效 */
    fun pairCheck(host: String, httpPort: Int, token: String): PairCheckResult {
        return try {
            val resp = client.newCall(
                Request.Builder().url("http://$host:$httpPort/pair-check?token=$token").build()
            ).execute()
            resp.use {
                when {
                    it.code == 200 && it.body?.string()?.trim() == "OK" -> PairCheckResult.Ok
                    it.code == 403 -> PairCheckResult.NeedPair
                    else -> PairCheckResult.Error("HTTP ${it.code}")
                }
            }
        } catch (e: IOException) {
            PairCheckResult.Error(e.message ?: "网络错误")
        }
    }

    /**
     * 凭 4 位配对码换长期 token。
     * 返回 Pairing.Success(token) / Pairing.WrongCode / Pairing.Error(网络)
     */
    fun pair(host: String, httpPort: Int, code: String): PairResult {
        return try {
            val resp = client.newCall(
                Request.Builder().url("http://$host:$httpPort/pair?code=$code").build()
            ).execute()
            resp.use {
                val body = it.body?.string()?.trim() ?: ""
                if (it.code == 200 && body.isNotEmpty() && !body.startsWith("ERR")) {
                    PairResult.Success(body)
                } else {
                    PairResult.WrongCode
                }
            }
        } catch (e: IOException) {
            PairResult.Error(e.message ?: "网络错误")
        }
    }
}

sealed class PairResult {
    data class Success(val token: String) : PairResult()
    data object WrongCode : PairResult()
    data class Error(val message: String) : PairResult()
}
