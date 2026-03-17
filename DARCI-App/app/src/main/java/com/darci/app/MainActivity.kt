package com.darci.app

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Send
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.darci.app.ui.theme.DarciAppTheme
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    private val vm: DarciViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            DarciAppTheme {
                DarciApp(vm)
            }
        }
    }
}

@Composable
fun DarciApp(vm: DarciViewModel) {
    val nav = rememberNavController()
    NavHost(nav, startDestination = "chat") {
        composable("chat")  { ChatScreen(vm, onOpenFiles = { nav.navigate("files") }) }
        composable("files") { FilesScreen(vm, onBack = { nav.popBackStack() }) }
    }
}

// ─── Chat Screen ──────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(vm: DarciViewModel, onOpenFiles: () -> Unit) {
    val messages      by vm.messages.collectAsState()
    val status        by vm.status.collectAsState()
    val channelLabel  by vm.channelLabel.collectAsState()
    val listState     = rememberLazyListState()
    val scope      = rememberCoroutineScope()

    var inputText  by remember { mutableStateOf("") }
    var urgent     by remember { mutableStateOf(false) }

    // Scroll to bottom whenever new message arrives
    LaunchedEffect(messages.size) {
        if (messages.isNotEmpty()) listState.animateScrollToItem(messages.size - 1)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("DARCI", fontWeight = FontWeight.Bold)
                        Text(
                            status?.currentMood ?: "—",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                        )
                    }
                },
                actions = {
                    // Channel indicator: AWS (cloud relay) or REST (local)
                    Badge(
                        containerColor = if (channelLabel == "AWS") Color(0xFF4CAF50) else Color(0xFFE57373)
                    ) { Text(channelLabel) }
                    Spacer(Modifier.width(8.dp))
                    IconButton(onClick = { vm.refreshStatus() }) {
                        Icon(Icons.Default.Refresh, "Refresh status")
                    }
                    IconButton(onClick = onOpenFiles) {
                        Icon(Icons.Default.Folder, "Research files")
                    }
                }
            )
        },
        bottomBar = {
            Column(Modifier.fillMaxWidth().padding(8.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(checked = urgent, onCheckedChange = { urgent = it })
                    Text("Urgent", fontSize = 13.sp)
                }
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = inputText,
                        onValueChange = { inputText = it },
                        modifier = Modifier.weight(1f),
                        placeholder = { Text("Message DARCI…") },
                        maxLines = 4
                    )
                    Spacer(Modifier.width(8.dp))
                    FilledIconButton(
                        onClick = {
                            if (inputText.isNotBlank()) {
                                vm.sendMessage(inputText.trim(), urgent)
                                inputText = ""
                            }
                        }
                    ) {
                        Icon(Icons.Default.Send, "Send")
                    }
                }
            }
        }
    ) { padding ->
        LazyColumn(
            state     = listState,
            modifier  = Modifier.fillMaxSize().padding(padding).padding(horizontal = 12.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            items(messages) { msg ->
                ChatBubble(msg)
            }
        }
    }
}

@Composable
fun ChatBubble(msg: ChatMessage) {
    val alignment = if (msg.fromDarci) Alignment.Start else Alignment.End
    val bubbleColor = if (msg.fromDarci)
        MaterialTheme.colorScheme.surfaceVariant
    else
        MaterialTheme.colorScheme.primary

    val textColor = if (msg.fromDarci)
        MaterialTheme.colorScheme.onSurfaceVariant
    else
        MaterialTheme.colorScheme.onPrimary

    Box(Modifier.fillMaxWidth(), contentAlignment = alignment) {
        Surface(
            shape = RoundedCornerShape(
                topStart = 16.dp, topEnd = 16.dp,
                bottomStart = if (msg.fromDarci) 4.dp else 16.dp,
                bottomEnd   = if (msg.fromDarci) 16.dp else 4.dp
            ),
            color = bubbleColor,
            modifier = Modifier.widthIn(max = 300.dp)
        ) {
            Text(
                text     = msg.text,
                color    = textColor,
                modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
                fontSize = 15.sp
            )
        }
    }
}
