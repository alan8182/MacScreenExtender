package com.macscreextender.receiver.input

import android.annotation.SuppressLint
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import kotlin.math.abs
import kotlin.math.hypot

/**
 * 触摸手势 → 控制通道协议包（手势表见 android-receiver/PLAN.md §5.3）：
 * - 单指点按（<200ms 不动）= 左键单击；按下拖动 = 左键拖拽；长按（≥500ms）= 右键（震动反馈）
 * - 双指滑动 = 滚轮（手指下滑 → dy>0，与 Windows 滚轮上/iPad 手势方向一致）
 * - 双指双击 = 呼出/收起退出栏（本地 UI，不进协议）
 * 触摸屏内容跟手（自然方向）：手指上滑 = 内容上移 = 向下滚，由 dy 符号约定自然得出。
 */
class TouchMapper(
    private val view: View,
    private val senderProvider: () -> InputSender?,
    private val onInputActivity: () -> Unit,
    private val onExitGesture: () -> Unit,
) : View.OnTouchListener {

    private val handler = Handler(Looper.getMainLooper())
    private val slop = ViewConfiguration.get(view.context).scaledTouchSlop

    // 单指状态
    private var downX = 0f
    private var downY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var downAt = 0L
    private var moved = false
    private var leftDown = false
    private var rightDown = false
    private var longPressFired = false

    // 双指滚动状态
    private var scrollMode = false
    private var lastCx = 0f
    private var lastCy = 0f
    private var accX = 0f
    private var accY = 0f
    private var lastWheelAt = 0L

    // 双指双击检测
    private var lastTwoFingerDownAt = 0L
    private var suppressUntilAllUp = false

    private val longPressRunnable = Runnable {
        longPressFired = true
        val s = senderProvider() ?: return@Runnable
        view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
        s.button(InputSender.BTN_RIGHT, true, nx(lastX), ny(lastY))
        rightDown = true
        onInputActivity()
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouch(v: View, event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                resetGesture()
                downX = event.x; downY = event.y
                lastX = event.x; lastY = event.y
                downAt = SystemClock.uptimeMillis()
                handler.postDelayed(longPressRunnable, LONG_PRESS_MS)
            }

            MotionEvent.ACTION_MOVE -> {
                lastX = event.x; lastY = event.y
                if (scrollMode) {
                    if (event.pointerCount >= 2) handleScroll(event)
                } else {
                    handleSingleMove(event)
                }
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                if (event.pointerCount == 2) {
                    handler.removeCallbacks(longPressRunnable)
                    endSingleGesture(sendClick = false)
                    scrollMode = true
                    beginScroll(event)
                    val t = SystemClock.uptimeMillis()
                    val isStillGesture = abs(accX) + abs(accY) < 1f
                    if (!suppressUntilAllUp && isStillGesture &&
                        t - lastTwoFingerDownAt < DOUBLE_TAP_MS
                    ) {
                        suppressUntilAllUp = true
                        onExitGesture()
                    }
                    lastTwoFingerDownAt = t
                }
            }

            MotionEvent.ACTION_POINTER_UP -> Unit   // 滚动中抬一指：维持 scrollMode 直到全部抬起

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                handler.removeCallbacks(longPressRunnable)
                val isUp = event.actionMasked == MotionEvent.ACTION_UP
                if (!suppressUntilAllUp && !scrollMode) {
                    val isTap = !moved && !longPressFired &&
                        SystemClock.uptimeMillis() - downAt < TAP_MS
                    endSingleGesture(sendClick = isTap && isUp)
                }
                if (isUp) resetGesture()
            }
        }
        return true
    }

    private fun handleSingleMove(event: MotionEvent) {
        val s = senderProvider() ?: return
        if (!moved && dist(downX, downY, event.x, event.y) > slop) {
            moved = true
            handler.removeCallbacks(longPressRunnable)
            if (!leftDown && !longPressFired) {   // 拖拽开始：Mac 端 leftDown 状态会把后续 move 合成为拖拽
                s.move(nx(event.x), ny(event.y))
                s.button(InputSender.BTN_LEFT, true, nx(event.x), ny(event.y))
                leftDown = true
                onInputActivity()
            }
        }
        if (leftDown || rightDown) {
            s.move(nx(event.x), ny(event.y))
            onInputActivity()
        }
    }

    private fun endSingleGesture(sendClick: Boolean) {
        val s = senderProvider()
        val x = nx(lastX)
        val y = ny(lastY)
        if (s != null) {
            when {
                leftDown -> s.button(InputSender.BTN_LEFT, false, x, y)
                rightDown -> s.button(InputSender.BTN_RIGHT, false, x, y)
                sendClick -> {   // 快速点按 = 完整左键单击
                    s.move(x, y)
                    s.button(InputSender.BTN_LEFT, true, x, y)
                    s.button(InputSender.BTN_LEFT, false, x, y)
                }
            }
            if (leftDown || rightDown || sendClick) onInputActivity()
        }
        leftDown = false
        rightDown = false
    }

    private fun beginScroll(event: MotionEvent) {
        lastCx = event.getX(0) + event.getX(1)
        lastCy = event.getY(0) + event.getY(1)
        accX = 0f
        accY = 0f
    }

    private fun handleScroll(event: MotionEvent) {
        val s = senderProvider() ?: return
        val cx = event.getX(0) + event.getX(1)
        val cy = event.getY(0) + event.getY(1)
        accX += cx - lastCx
        accY += cy - lastCy
        lastCx = cx; lastCy = cy
        val now = SystemClock.uptimeMillis()
        if (now - lastWheelAt < WHEEL_INTERVAL_MS) return
        // 手指下滑 → accY>0 → dy>0 = 视图向上滚（对齐 iPad：velocity.y/500）
        val dy = accY * SCROLL_LINES_PER_PX
        val dx = -accX * SCROLL_LINES_PER_PX
        if (abs(dy) >= MIN_LINES || abs(dx) >= MIN_LINES) {
            s.wheel(dx, dy)
            accX = 0f
            accY = 0f
            lastWheelAt = now
            onInputActivity()
        }
    }

    private fun resetGesture() {
        downX = 0f; downY = 0f; lastX = 0f; lastY = 0f
        moved = false; leftDown = false; rightDown = false; longPressFired = false
        scrollMode = false
        suppressUntilAllUp = false
        accX = 0f; accY = 0f
    }

    private fun nx(x: Float): Float = if (view.width <= 0) 0f else (x / view.width).coerceIn(0f, 1f)
    private fun ny(y: Float): Float = if (view.height <= 0) 0f else (y / view.height).coerceIn(0f, 1f)

    private fun dist(x1: Float, y1: Float, x2: Float, y2: Float): Float =
        hypot((x2 - x1).toDouble(), (y2 - y1).toDouble()).toFloat()

    companion object {
        private const val TAP_MS = 200L
        private const val LONG_PRESS_MS = 500L
        private const val DOUBLE_TAP_MS = 350L
        private const val WHEEL_INTERVAL_MS = 16L
        private const val SCROLL_LINES_PER_PX = 0.02f
        private const val MIN_LINES = 0.04f
    }
}
