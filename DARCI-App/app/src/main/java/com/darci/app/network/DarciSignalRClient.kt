package com.darci.app.network

import android.util.Log
import com.darci.app.models.DarciResponse
import com.darci.app.models.HubFileReady
import com.darci.app.models.HubStatus
import com.google.gson.Gson
import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.HubConnectionState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.launch

private const val TAG = "DarciSignalR"

/**
 * Wraps the Microsoft SignalR Java client.
 *
 * Exposes coroutine-friendly SharedFlows for incoming events.
 * The connection is started/stopped via [connect] / [disconnect].
 *
 * Hub URL:  {baseUrl}/hub
 */
class DarciSignalRClient(private val baseUrl: String) {

    private val gson = Gson()
    private val scope = CoroutineScope(Dispatchers.IO)

    private val _responses  = MutableSharedFlow<DarciResponse>(extraBufferCapacity = 64)
    private val _status     = MutableSharedFlow<HubStatus>(replay = 1)
    private val _files      = MutableSharedFlow<HubFileReady>(extraBufferCapacity = 16)
    private val _notifications = MutableSharedFlow<String>(extraBufferCapacity = 32)

    val responses:      SharedFlow<DarciResponse>  = _responses
    val statusUpdates:  SharedFlow<HubStatus>       = _status
    val fileReady:      SharedFlow<HubFileReady>    = _files
    val notifications:  SharedFlow<String>           = _notifications

    private var connection: HubConnection? = null

    fun connect() {
        val hubUrl = baseUrl.trimEnd('/') + "/hub"
        Log.d(TAG, "Connecting to $hubUrl")

        val conn = HubConnectionBuilder.create(hubUrl).build()

        conn.on("ReceiveResponse", { json: String ->
            runCatching { gson.fromJson(json, DarciResponse::class.java) }
                .onSuccess  { scope.launch { _responses.emit(it) } }
                .onFailure  { Log.e(TAG, "ReceiveResponse parse error", it) }
        }, String::class.java)

        conn.on("StatusUpdate", { json: String ->
            runCatching { gson.fromJson(json, HubStatus::class.java) }
                .onSuccess  { scope.launch { _status.emit(it) } }
                .onFailure  { Log.e(TAG, "StatusUpdate parse error", it) }
        }, String::class.java)

        conn.on("FileReady", { json: String ->
            runCatching { gson.fromJson(json, HubFileReady::class.java) }
                .onSuccess  { scope.launch { _files.emit(it) } }
                .onFailure  { Log.e(TAG, "FileReady parse error", it) }
        }, String::class.java)

        conn.on("Notification", { text: String ->
            scope.launch { _notifications.emit(text) }
        }, String::class.java)

        conn.onClosed { error ->
            Log.w(TAG, "Hub connection closed", error)
        }

        connection = conn
        conn.start().blockingAwait()
        Log.d(TAG, "Hub connected (state=${conn.connectionState})")
    }

    fun disconnect() {
        connection?.stop()
        connection = null
    }

    val isConnected: Boolean
        get() = connection?.connectionState == HubConnectionState.CONNECTED

    /** Send a message to DARCI via the hub. */
    fun sendMessage(message: String, userId: String? = null, urgent: Boolean = false) {
        val conn = connection ?: return
        conn.send("SendMessage", message, userId, urgent)
    }

    /** Ask the hub to push the latest file list. */
    fun requestFiles(sessionId: String? = null) {
        connection?.send("ListFiles", sessionId)
    }

    /** Ask the hub to push DARCI's current status. */
    fun requestStatus() {
        connection?.send("RequestStatus")
    }
}
