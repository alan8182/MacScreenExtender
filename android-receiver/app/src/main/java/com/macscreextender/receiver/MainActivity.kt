package com.macscreextender.receiver

import android.Manifest
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.macscreextender.receiver.session.SessionController
import com.macscreextender.receiver.ui.ConnectionScreen
import com.macscreextender.receiver.ui.FullscreenHost

class MainActivity : ComponentActivity() {

    private val notifPermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { /* 拒绝也不阻塞投屏 */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        SessionController.init(applicationContext)
        enableEdgeToEdge()
        setContent {
            MaterialTheme {
                MacScreenApp(
                    onRequestNotifPermission = {
                        if (Build.VERSION.SDK_INT >= 33) {
                            notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        }
                    },
                )
            }
        }
    }
}

@Composable
private fun MacScreenApp(onRequestNotifPermission: () -> Unit) {
    val state by SessionController.state.collectAsState()
    val toast by SessionController.toast.collectAsState(initial = null as String?)
    val context = LocalContext.current

    LaunchedEffect(toast) {
        toast?.let { Toast.makeText(context, it, Toast.LENGTH_SHORT).show() }
    }

    Surface(Modifier.fillMaxSize()) {
        when (val s = state) {
            is SessionController.State.Idle ->
                ConnectionScreen(pairingHost = null, errorText = null, onConnect = connectAction(onRequestNotifPermission))

            is SessionController.State.Connecting -> ConnectingScreen(s.host)

            is SessionController.State.PairingRequired ->
                ConnectionScreen(pairingHost = s.host, errorText = null, onConnect = connectAction(onRequestNotifPermission))

            is SessionController.State.Streaming -> FullscreenHost(s)

            is SessionController.State.Failed ->
                ConnectionScreen(pairingHost = null, errorText = s.message, onConnect = connectAction(onRequestNotifPermission))
        }
    }
}

private fun connectAction(requestPermission: () -> Unit): (String, Int, String) -> Unit =
    { host, port, name ->
        requestPermission()
        SessionController.startSession(host, port, name)
    }

@Composable
private fun ConnectingScreen(host: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(16.dp),
            modifier = Modifier.padding(32.dp),
        ) {
            CircularProgressIndicator()
            Text("正在连接 $host …", style = MaterialTheme.typography.bodyLarge)
            TextButton(onClick = { SessionController.disconnect() }) {
                Text("取消")
            }
        }
    }
}
