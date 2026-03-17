package com.darci.app.network

import com.darci.app.models.*
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.*

interface DarciApiService {

    // ── Core ──────────────────────────────────────────────────────────────────

    @GET("/")
    suspend fun ping(): Response<String>

    @GET("/status")
    suspend fun getStatus(): Response<DarciStatusResponse>

    @POST("/message")
    suspend fun sendMessage(@Body request: MessageRequest): Response<Map<String, String>>

    @GET("/responses")
    suspend fun pollResponses(): Response<List<DarciResponse>>

    // ── Research Sessions ──────────────────────────────────────────────────────

    @GET("/research/sessions")
    suspend fun getSessions(
        @Query("status") status: String? = null,
        @Query("limit")  limit: Int    = 50
    ): Response<List<ResearchSession>>

    @GET("/research/sessions/{id}")
    suspend fun getSession(@Path("id") id: String): Response<Map<String, Any>>

    // ── Research Files ─────────────────────────────────────────────────────────

    @GET("/research/files")
    suspend fun getFiles(
        @Query("sessionId") sessionId: String? = null
    ): Response<List<ResearchFile>>

    @GET("/research/files/{id}/download")
    @Streaming
    suspend fun downloadFile(@Path("id") id: String): Response<ResponseBody>

    // ── Results search ─────────────────────────────────────────────────────────

    @GET("/research/search")
    suspend fun searchResults(
        @Query("q")     query: String,
        @Query("limit") limit: Int = 20
    ): Response<List<ResearchResult>>
}
