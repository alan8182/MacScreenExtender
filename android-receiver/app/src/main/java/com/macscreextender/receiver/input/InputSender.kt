package com.macscreextender.receiver.input

/** 触摸 → 控制通道的发送门面（SessionController 提供， TouchMapper 消费） */
interface InputSender {
    fun move(nx: Float, ny: Float)
    fun button(btn: Int, down: Boolean, nx: Float, ny: Float)
    fun wheel(dx: Float, dy: Float)

    companion object {
        const val BTN_LEFT = 0
        const val BTN_RIGHT = 1
    }
}
