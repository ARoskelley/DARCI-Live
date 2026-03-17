package com.darci.app

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.darci.app.BuildConfig
import com.darci.app.models.*
import com.darci.app.network.AwsClient
import com.darci.app.network.RetrofitClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Primary data source: AWS SQS (messages) + S3 (files).
 * Falls back to direct DARCI REST API when AWS is not configured and on local network.
 */
class DarciViewModel : ViewModel() {

    // ─── Clients ──────────────────────────────────────────────────────────────

    private val awsEnabled = BuildConfig.DARCI_AWS_ENABLED &&
        BuildConfig.DARCI_AWS_KEY_ID.isNotBlank() &&
        BuildConfig.DARCI_SQS_INBOX.isNotBlank()

    private val aws  = if (awsEnabled) AwsClient() else null
    private val rest = RetrofitClient.api

    // ─── State ────────────────────────────────────────────────────────────────

    private val _messages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val messages: StateFlow<List<ChatMessage>> = _messages.asStateFlow()

    private val _files = MutableStateFlow<List<ResearchFile>>(emptyList())
    val files: StateFlow<List<ResearchFile>> = _files.asStateFlow()

    private val _sessions = MutableStateFlow<List<ResearchSession>>(emptyList())
    val sessions: StateFlow<List<ResearchSession>> = _sessions.asStateFlow()

    private val _status = MutableStateFlow<DarciStatusResponse?>(null)
    val status: StateFlow<DarciStatusResponse?> = _status.asStateFlow()

    private val _channelLabel = MutableStateFlow(if (awsEnabled) "AWS" else "REST")
    val channelLabel: StateFlow<String> = _channelLabel.asStateFlow()

    private val _notifications = MutableSharedFlow<String>(extraBufferCapacity = 16)
    val notifications: SharedFlow<String> = _notifications

    // ─── Init ─────────────────────────────────────────────────────────────────

    init {
        if (awsEnabled) {
            startSqsPolling()
            loadFilesFromS3()
        } else {
            startRestPolling()
            loadFilesFromRest()
        }
        loadSessions()
        refreshStatus()
    }

    // ─── AWS path ─────────────────────────────────────────────────────────────

    private fun startSqsPolling() {
        viewModelScope.launch(Dispatchers.IO) {
            while (true) {
                runCatching {
                    val responses = aws!!.pollResponses()
                    responses.forEach { resp ->
                        appendMessage(ChatMessage(text = resp.content, fromDarci = true))
                    }
                }.onFailure {
                    _notifications.emit("SQS error: ${it.message}")
                }
                // No extra delay needed — SQS long-poll already waits 10 s when idle
            }
        }
    }

    private fun loadFilesFromS3(sessionId: String? = null) {
        viewModelScope.launch(Dispatchers.IO) {
            runCatching { aws!!.listFiles(sessionId) }
                .onSuccess { _files.value = it }
        }
    }

    // ─── REST fallback path ───────────────────────────────────────────────────

    private fun startRestPolling() {
        viewModelScope.launch {
            while (true) {
                runCatching {
                    val resp = withContext(Dispatchers.IO) { rest.pollResponses() }
                    resp.body()?.forEach { msg ->
                        appendMessage(ChatMessage(text = msg.content, fromDarci = true))
                    }
                }
                kotlinx.coroutines.delay(3_000)
            }
        }
    }

    private fun loadFilesFromRest(sessionId: String? = null) {
        viewModelScope.launch(Dispatchers.IO) {
            runCatching { rest.getFiles(sessionId) }
                .onSuccess { resp -> resp.body()?.let { _files.value = it } }
        }
    }

    // ─── Public actions ───────────────────────────────────────────────────────

    fun sendMessage(text: String, urgent: Boolean = false) {
        if (text.isBlank()) return
        appendMessage(ChatMessage(text = text, fromDarci = false))

        viewModelScope.launch(Dispatchers.IO) {
            if (awsEnabled) {
                runCatching { aws!!.sendMessage(text, urgent = urgent) }
                    .onFailure { _notifications.emit("Send failed: ${it.message}") }
            } else {
                runCatching { rest.sendMessage(MessageRequest(message = text, urgent = urgent)) }
            }
        }
    }

    fun refreshStatus() {
        viewModelScope.launch(Dispatchers.IO) {
            runCatching { rest.getStatus() }
                .onSuccess { resp -> resp.body()?.let { _status.value = it } }
        }
    }

    fun refreshFiles(sessionId: String? = null) {
        if (awsEnabled) loadFilesFromS3(sessionId)
        else loadFilesFromRest(sessionId)
    }

    fun loadSessions() {
        viewModelScope.launch(Dispatchers.IO) {
            runCatching { rest.getSessions() }
                .onSuccess { resp -> resp.body()?.let { _sessions.value = it } }
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private fun appendMessage(msg: ChatMessage) {
        _messages.value = _messages.value + msg
    }
}

data class ChatMessage(val text: String, val fromDarci: Boolean)
