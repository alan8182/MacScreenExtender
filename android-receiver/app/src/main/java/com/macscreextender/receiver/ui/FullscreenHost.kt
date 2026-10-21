package com.macscreextender.receiver.ui

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.macscreextender.receiver.input.CursorOverlayView
import com.macscreextender.receiver.input.TouchMapper
import com.macscreextender.receiver.session.SessionController
import kotlinx.coroutines.delay

/**
 * 全屏拓展模式：SurfaceView 硬解直出 + 本地光标叠加层 + 触摸控制。
 * 双指双击呼出顶部退出栏（4 秒自动收起）。对应 ipad-receiver 的 DisplayView。
 */
@Composable
fun FullscreenHost(session: SessionController.State.Streaming) {
    val view = LocalView.current
    val activity = view.context.findActivity()
    var exitBarVisible by remember { mutableStateOf(false) }

    // 沉浸全屏 + 屏幕常亮；离开本界面时恢复
    DisposableEffect(Unit) {
        val window = activity?.window
        window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val insetsController = window?.let {
            WindowCompat.getInsetsController(it, view).apply {
                systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                hide(WindowInsetsCompat.Type.systemBars())
            }
        }
        onDispose {
            window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            insetsController?.show(WindowInsetsCompat.Type.systemBars())
            SessionController.registerCursorOverlay(null)
        }
    }

    LaunchedEffect(exitBarVisible) {
        if (exitBarVisible) {
            delay(4000)
            exitBarVisible = false
        }
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { ctx ->
                FrameLayoutWithSurface(ctx) { exitBarVisible = !exitBarVisible }
            },
        )

        AnimatedVisibility(
            visible = exitBarVisible,
            enter = fadeIn() + slideInVertically { -it },
            exit = fadeOut() + slideOutVertically { -it },
            modifier = Modifier.align(Alignment.TopCenter),
        ) {
            Surface(
                shape = RoundedCornerShape(bottomStart = 20.dp, bottomEnd = 20.dp),
                tonalElevation = 6.dp,
                color = MaterialTheme.colorScheme.surface,
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 20.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Column {
                        Text("扩展屏运行中", style = MaterialTheme.typography.labelLarge)
                        Text(session.label, style = MaterialTheme.typography.bodySmall)
                    }
                    Button(onClick = { SessionController.disconnect() }) {
                        Text("断开连接")
                    }
                }
            }
        }
    }
}

/**
 * SurfaceView（硬解输出 + 触摸入口）+ 光标叠加层。
 * 单独成类以便在非 Compose 世界里持有 SurfaceHolder 回调与 TouchMapper。
 */
private class FrameLayoutWithSurface(
    context: Context,
    onExitGesture: () -> Unit,
) : android.widget.FrameLayout(context) {

    init {
        val surfaceView = SurfaceView(context)
        val cursorOverlay = CursorOverlayView(context)
        addView(
            surfaceView,
            LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
        addView(
            cursorOverlay,
            LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) = Unit

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
                SessionController.onSurfaceAvailable(holder.surface, width, height)
            }

            override fun surfaceDestroyed(holder: SurfaceHolder) {
                SessionController.onSurfaceDestroyed()
            }
        })
        SessionController.registerCursorOverlay(cursorOverlay)
        setOnTouchListener(
            TouchMapper(
                view = this,
                senderProvider = { SessionController.input },
                onInputActivity = { /* 光标活跃窗口由 SessionController.input 内部处理 */ },
                onExitGesture = onExitGesture,
            )
        )
    }
}

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}
