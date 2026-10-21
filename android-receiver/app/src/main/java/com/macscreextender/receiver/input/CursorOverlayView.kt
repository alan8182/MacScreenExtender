package com.macscreextender.receiver.input

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Path
import android.view.View

/**
 * 本地光标叠加层：依据 Mac 端 type 8 光标回报绘制（0ms 感知延迟的关键，ARCHITECTURE.md §1）。
 * 仅在触摸活跃窗口内显示（此时 Mac 不再把指针烙进视频帧）；静默期隐藏，避免与入画的 Mac 指针双显。
 * update() 会被控制通道线程调用，内部用 postInvalidate。
 *
 * 绘制在像素空间完成：单位路径一次性经 Matrix 放大到目标尺寸，禁止 canvas.scale——
 * 那会把 strokeWidth 一并放大几十倍，箭头会糊成巨大轮廓。
 */
class CursorOverlayView(context: Context) : View(context) {

    @Volatile private var nx = -1f
    @Volatile private var ny = -1f
    @Volatile private var cursorVisible = false
    @Volatile private var touchActive = false

    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.BLACK }
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        style = Paint.Style.STROKE
        strokeWidth = resources.displayMetrics.density * 1.5f
    }

    private val unitArrow = Path().apply {
        // 经典指针箭头（单位坐标）
        moveTo(0f, 0f)
        lineTo(0f, 0.82f)
        lineTo(0.20f, 0.64f)
        lineTo(0.34f, 1.00f)
        lineTo(0.46f, 0.95f)
        lineTo(0.33f, 0.60f)
        lineTo(0.58f, 0.60f)
        close()
    }
    private val arrowPx = Path()
    private val matrix = Matrix()
    private var builtForSize = -1f

    private fun arrowPath(sizePx: Float): Path {
        if (builtForSize != sizePx) {
            matrix.reset()
            matrix.setScale(sizePx, sizePx)
            arrowPx.reset()
            unitArrow.transform(matrix, arrowPx)
            builtForSize = sizePx
        }
        return arrowPx
    }

    fun update(nx: Float, ny: Float, visible: Boolean) {
        this.nx = nx
        this.ny = ny
        this.cursorVisible = visible
        postInvalidate()
    }

    fun setTouchActive(active: Boolean) {
        if (touchActive == active) return
        touchActive = active
        postInvalidate()
    }

    override fun onDraw(canvas: Canvas) {
        if (!touchActive || !cursorVisible || nx < 0 || width == 0 || height == 0) return
        val sizePx = resources.displayMetrics.density * 22f   // ≈22dp 的标准指针
        val x = nx * width
        val y = ny * height
        canvas.save()
        canvas.translate(x, y)
        val path = arrowPath(sizePx)
        canvas.drawPath(path, fill)
        canvas.drawPath(path, stroke)
        canvas.restore()
    }
}
