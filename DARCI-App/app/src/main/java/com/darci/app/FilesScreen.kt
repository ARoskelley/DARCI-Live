package com.darci.app

import android.content.Context
import android.content.Intent
import android.widget.Toast
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import com.darci.app.models.ResearchFile
import com.darci.app.models.ResearchSession
import com.darci.app.network.RetrofitClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

// ─── Files Screen ─────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FilesScreen(vm: DarciViewModel, onBack: () -> Unit) {
    val files    by vm.files.collectAsState()
    val sessions by vm.sessions.collectAsState()
    val context  = LocalContext.current
    val scope    = rememberCoroutineScope()

    var selectedSession by remember { mutableStateOf<String?>(null) }
    var downloading     by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) { vm.refreshFiles(selectedSession) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Research Files") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ArrowBack, "Back")
                    }
                },
                actions = {
                    IconButton(onClick = { vm.refreshFiles(selectedSession) }) {
                        Icon(Icons.Default.Refresh, "Refresh")
                    }
                }
            )
        }
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {

            // Session filter chips
            if (sessions.isNotEmpty()) {
                SessionFilterRow(
                    sessions       = sessions,
                    selectedId     = selectedSession,
                    onSelectSession = { id ->
                        selectedSession = if (selectedSession == id) null else id
                        vm.loadFiles(selectedSession)
                    }
                )
            }

            if (files.isEmpty()) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text("No files yet.", color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f))
                }
            } else {
                LazyColumn(
                    contentPadding = PaddingValues(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    items(files) { file ->
                        FileCard(
                            file        = file,
                            isDownloading = downloading == file.id,
                            onDownload  = {
                                scope.launch {
                                    downloading = file.id
                                    downloadAndOpen(context, file)
                                    downloading = null
                                }
                            }
                        )
                    }
                }
            }
        }
    }
}

@Composable
fun SessionFilterRow(
    sessions: List<ResearchSession>,
    selectedId: String?,
    onSelectSession: (String) -> Unit
) {
    androidx.compose.foundation.lazy.LazyRow(
        modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        items(sessions) { s ->
            FilterChip(
                selected = selectedId == s.id,
                onClick  = { onSelectSession(s.id) },
                label    = { Text(s.title, maxLines = 1) }
            )
        }
    }
}

@Composable
fun FileCard(file: ResearchFile, isDownloading: Boolean, onDownload: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Row(
            Modifier.padding(12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text(file.filename, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(2.dp))
                Text(
                    "${file.contentType}  ·  ${formatBytes(file.sizeBytes)}",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f)
                )
                Text(
                    file.createdAt.take(19).replace("T", " "),
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.4f)
                )
            }
            if (isDownloading) {
                CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
            } else {
                IconButton(onClick = onDownload) {
                    Icon(Icons.Default.Download, "Download ${file.filename}")
                }
            }
        }
    }
}

private fun formatBytes(bytes: Long): String = when {
    bytes < 1_024       -> "$bytes B"
    bytes < 1_048_576   -> "%.1f KB".format(bytes / 1_024f)
    else                -> "%.1f MB".format(bytes / 1_048_576f)
}

private suspend fun downloadAndOpen(context: Context, file: ResearchFile) {
    withContext(Dispatchers.IO) {
        runCatching {
            val response = RetrofitClient.api.downloadFile(file.id)
            if (!response.isSuccessful) {
                withContext(Dispatchers.Main) {
                    Toast.makeText(context, "Download failed: ${response.code()}", Toast.LENGTH_SHORT).show()
                }
                return@withContext
            }

            val dir  = File(context.cacheDir, "darci_files").also { it.mkdirs() }
            val dest = File(dir, file.filename)
            response.body()!!.byteStream().use { input ->
                dest.outputStream().use { output -> input.copyTo(output) }
            }

            val uri = FileProvider.getUriForFile(
                context, "${context.packageName}.fileprovider", dest)

            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, file.contentType)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }

            withContext(Dispatchers.Main) {
                runCatching { context.startActivity(intent) }
                    .onFailure {
                        Toast.makeText(context, "No app to open ${file.contentType}", Toast.LENGTH_SHORT).show()
                    }
            }
        }.onFailure {
            withContext(Dispatchers.Main) {
                Toast.makeText(context, "Error: ${it.message}", Toast.LENGTH_SHORT).show()
            }
        }
    }
}
