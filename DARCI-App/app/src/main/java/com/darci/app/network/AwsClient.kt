package com.darci.app.network

import android.util.Log
import com.amazonaws.auth.BasicAWSCredentials
import com.amazonaws.regions.Region
import com.amazonaws.regions.Regions
import com.amazonaws.services.s3.AmazonS3Client
import com.amazonaws.services.s3.model.ListObjectsV2Request
import com.amazonaws.services.s3.model.GeneratePresignedUrlRequest
import com.amazonaws.services.sqs.AmazonSQSClient
import com.amazonaws.services.sqs.model.DeleteMessageRequest
import com.amazonaws.services.sqs.model.ReceiveMessageRequest
import com.amazonaws.services.sqs.model.SendMessageRequest
import com.darci.app.BuildConfig
import com.darci.app.models.DarciResponse
import com.darci.app.models.ResearchFile
import com.google.gson.Gson
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.Date

private const val TAG = "DarciAws"

/**
 * Wraps AWS SQS (message relay) and S3 (file storage).
 *
 * Credentials are read from BuildConfig, which is populated from local.properties:
 *   DARCI_AWS_KEY_ID=...
 *   DARCI_AWS_KEY_SECRET=...
 *   DARCI_AWS_REGION=us-east-1
 *   DARCI_SQS_INBOX=https://sqs.us-east-1.amazonaws.com/...
 *   DARCI_SQS_OUTBOX=https://sqs.us-east-1.amazonaws.com/...
 *   DARCI_S3_BUCKET=darci-files-...
 */
class AwsClient {

    private val creds = BasicAWSCredentials(
        BuildConfig.DARCI_AWS_KEY_ID,
        BuildConfig.DARCI_AWS_KEY_SECRET
    )
    private val region = Regions.fromName(BuildConfig.DARCI_AWS_REGION)

    private val sqs = AmazonSQSClient(creds).also {
        it.setRegion(Region.getRegion(region))
    }
    private val s3 = AmazonS3Client(creds).also {
        it.setRegion(Region.getRegion(region))
    }

    private val gson = Gson()

    // ─── SQS ──────────────────────────────────────────────────────────────────

    /**
     * Sends a message to DARCI's SQS inbox queue.
     * Non-blocking — returns immediately after enqueuing.
     */
    suspend fun sendMessage(content: String, userId: String = "Tinman", urgent: Boolean = false) =
        withContext(Dispatchers.IO) {
            val body = gson.toJson(mapOf(
                "content" to content,
                "userId"  to userId,
                "urgent"  to urgent
            ))
            sqs.sendMessage(SendMessageRequest(BuildConfig.DARCI_SQS_INBOX, body))
            Log.d(TAG, "Sent to SQS inbox: ${content.take(60)}")
        }

    /**
     * Polls DARCI's SQS outbox for responses.
     * Uses long-polling (10 s wait) to minimise empty calls.
     * Deletes consumed messages automatically.
     * Returns however many messages were waiting (0–10).
     */
    suspend fun pollResponses(): List<DarciResponse> = withContext(Dispatchers.IO) {
        val result = sqs.receiveMessage(
            ReceiveMessageRequest(BuildConfig.DARCI_SQS_OUTBOX)
                .withMaxNumberOfMessages(10)
                .withWaitTimeSeconds(10)  // long-poll
        )

        result.messages.mapNotNull { sqsMsg ->
            runCatching {
                val env = gson.fromJson(sqsMsg.body, SqsResponseEnvelope::class.java)
                DarciResponse(
                    userId   = env.userId ?: "DARCI",
                    content  = env.content ?: "",
                    createdAt = env.createdAt ?: ""
                ).also {
                    // Ack — remove from queue
                    sqs.deleteMessage(
                        DeleteMessageRequest(BuildConfig.DARCI_SQS_OUTBOX, sqsMsg.receiptHandle))
                }
            }.getOrNull()
        }
    }

    // ─── S3 ───────────────────────────────────────────────────────────────────

    /**
     * Lists research files in S3, optionally filtered to a session prefix.
     */
    suspend fun listFiles(sessionId: String? = null): List<ResearchFile> = withContext(Dispatchers.IO) {
        val prefix = if (sessionId != null) "sessions/$sessionId/" else "sessions/"
        val req = ListObjectsV2Request()
            .withBucketName(BuildConfig.DARCI_S3_BUCKET)
            .withPrefix(prefix)

        s3.listObjectsV2(req).objectSummaries.map { obj ->
            val presigned = s3.generatePresignedUrl(
                GeneratePresignedUrlRequest(BuildConfig.DARCI_S3_BUCKET, obj.key)
                    .withExpiration(Date(System.currentTimeMillis() + 60 * 60 * 1000L)) // 1 hour
            ).toString()

            val sessionIdFromKey = obj.key.split("/").getOrNull(1) ?: ""

            ResearchFile(
                id          = obj.key.hashCode().toString(),
                sessionId   = sessionIdFromKey,
                filename    = obj.key.split("/").last(),
                contentType = guessContentType(obj.key),
                sizeBytes   = obj.size,
                createdAt   = obj.lastModified.toString(),
                downloadUrl = presigned
            )
        }
    }

    private fun guessContentType(filename: String): String = when {
        filename.endsWith(".json") -> "application/json"
        filename.endsWith(".pdf")  -> "application/pdf"
        filename.endsWith(".md")   -> "text/markdown"
        filename.endsWith(".txt")  -> "text/plain"
        filename.endsWith(".csv")  -> "text/csv"
        filename.endsWith(".png")  -> "image/png"
        filename.endsWith(".jpg") || filename.endsWith(".jpeg") -> "image/jpeg"
        else                       -> "application/octet-stream"
    }
}

private data class SqsResponseEnvelope(
    val userId: String?,
    val content: String?,
    val createdAt: String?
)
