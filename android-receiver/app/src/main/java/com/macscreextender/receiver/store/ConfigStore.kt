package com.macscreextender.receiver.store

import android.content.Context
import android.content.SharedPreferences

/** 配对 token 与最近一次连接目标的持久化 */
object ConfigStore {
    private const val FILE = "receiver_config"

    private lateinit var sp: SharedPreferences

    fun init(context: Context) {
        if (!::sp.isInitialized) sp = context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)
    }

    var token: String
        get() = sp.getString("token", "") ?: ""
        set(v) = sp.edit().putString("token", v).apply()

    var host: String
        get() = sp.getString("host", "") ?: ""
        set(v) = sp.edit().putString("host", v).apply()

    var httpPort: Int
        get() = sp.getInt("httpPort", 8080)
        set(v) = sp.edit().putInt("httpPort", v).apply()

    var macName: String
        get() = sp.getString("macName", "") ?: ""
        set(v) = sp.edit().putString("macName", v).apply()

    fun clearPairing() = sp.edit().remove("token").apply()
}
