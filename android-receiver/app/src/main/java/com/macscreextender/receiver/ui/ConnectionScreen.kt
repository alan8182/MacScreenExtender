package com.macscreextender.receiver.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.macscreextender.receiver.session.SessionController
import com.macscreextender.receiver.store.ConfigStore

/**
 * 连接页：UDP 自动发现列表 + 手动 IP 兜底 + 4 位配对码弹窗。
 * （对应 ipad-receiver 的 ConnectionView + PairSheet）
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectionScreen(
    pairingHost: String?,
    errorText: String?,
    onConnect: (host: String, httpPort: Int, name: String) -> Unit,
) {
    val discovered by SessionController.discovered.collectAsState()
    var manualIp by remember { mutableStateOf(ConfigStore.host) }

    LaunchedEffect(Unit) { SessionController.startDiscovery() }
    DisposableEffect(Unit) {
        onDispose { SessionController.stopDiscovery() }
    }

    Scaffold(
        topBar = { TopAppBar(title = { Text("Mac 扩展屏") }) },
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            if (errorText != null) {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    colors = androidx.compose.material3.CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer,
                    ),
                ) {
                    Text(
                        errorText,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        modifier = Modifier.padding(14.dp),
                    )
                }
            }

            Text("局域网设备", style = MaterialTheme.typography.titleSmall)

            if (discovered.isEmpty()) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.width(10.dp))
                    Text("正在搜索局域网中的 Mac…", style = MaterialTheme.typography.bodySmall)
                }
            }
            discovered.forEach { mac ->
                Card(onClick = { onConnect(mac.ip, mac.httpPort, mac.name) }) {
                    Column(Modifier.fillMaxWidth().padding(16.dp)) {
                        Text(mac.name, style = MaterialTheme.typography.titleMedium)
                        Text(
                            "${mac.ip}:${mac.httpPort}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            HorizontalDivider()
            Text("手动连接", style = MaterialTheme.typography.titleSmall)

            OutlinedTextField(
                value = manualIp,
                onValueChange = { manualIp = it.trim() },
                label = { Text("Mac 的 IP 地址") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth(),
            )
            Button(
                onClick = {
                    val ip = manualIp.trim()
                    if (ip.isNotEmpty()) onConnect(ip, 8080, ip)
                },
                enabled = manualIp.isNotBlank(),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("连接")
            }

            Text(
                "提示：确保 Mac 菜单栏的 MacScreenSender 正在运行，且与本设备处于同一局域网（建议 5GHz Wi-Fi）。" +
                    "首次连接需输入 Mac 菜单栏显示的 4 位配对码。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(24.dp))
        }
    }

    if (pairingHost != null) {
        PairDialog(
            onSubmit = { SessionController.submitPairCode(it) },
            onCancel = { SessionController.cancelPairing() },
        )
    }
}

@Composable
private fun PairDialog(onSubmit: (String) -> Unit, onCancel: () -> Unit) {
    var code by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onCancel,
        title = { Text("输入配对码") },
        text = {
            Column {
                Text("请输入 Mac 菜单栏显示的 4 位配对码", style = MaterialTheme.typography.bodySmall)
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    value = code,
                    onValueChange = { code = it.filter(Char::isDigit).take(4) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onSubmit(code) }, enabled = code.length == 4) { Text("配对") }
        },
        dismissButton = {
            TextButton(onClick = onCancel) { Text("取消") }
        },
    )
}
