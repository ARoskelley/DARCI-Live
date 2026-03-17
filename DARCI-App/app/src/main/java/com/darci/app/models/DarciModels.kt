package com.darci.app.models

import com.google.gson.annotations.SerializedName

// ─── Outgoing (app → DARCI) ──────────────────────────────────────────────────

data class MessageRequest(
    val message: String,
    val userId: String? = null,
    val urgent: Boolean = false
)

// ─── Incoming (DARCI → app) ──────────────────────────────────────────────────

data class DarciResponse(
    val userId: String,
    val content: String,
    val createdAt: String,
    val externalNotify: Boolean = false
)

data class DarciStatusResponse(
    val isAlive: Boolean,
    val uptime: String?,
    val cycleCount: Long,
    val currentMood: String,
    val energy: Float,
    val currentActivity: String?
)

// ─── Research ────────────────────────────────────────────────────────────────

data class ResearchSession(
    val id: String,
    val title: String,
    val description: String,
    val status: String,
    val createdBy: String,
    val createdAt: String,
    val completedAt: String?
)

data class ResearchFile(
    val id: String,
    val sessionId: String,
    val filename: String,
    val contentType: String,
    val sizeBytes: Long,
    val createdAt: String,
    val downloadUrl: String
)

data class ResearchResult(
    val id: String,
    val sessionId: String,
    val source: String,
    val content: String,
    val resultType: String,
    val relevanceScore: Float,
    val createdAt: String
)

// ─── SignalR hub payloads ─────────────────────────────────────────────────────

data class HubStatus(
    val currentMood: String,
    val energy: Float,
    val isAlive: Boolean,
    val cycleCount: Long,
    val currentActivity: String?,
    val connectedAt: String?
)

data class HubFileReady(
    val fileId: String,
    val filename: String,
    val sessionId: String,
    val downloadUrl: String,
    val readyAt: String
)

// ─── UI State ─────────────────────────────────────────────────────────────────

sealed class UiState<out T> {
    object Loading : UiState<Nothing>()
    data class Success<T>(val data: T) : UiState<T>()
    data class Error(val message: String) : UiState<Nothing>()
}
