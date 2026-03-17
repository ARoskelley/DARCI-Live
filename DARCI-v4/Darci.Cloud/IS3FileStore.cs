namespace Darci.Cloud;

/// <summary>
/// Uploads research files to S3 and generates pre-signed download URLs.
/// </summary>
public interface IS3FileStore
{
    /// <summary>
    /// Uploads a local file to S3.
    /// Returns the S3 key that was used.
    /// </summary>
    Task<string> UploadFileAsync(string localPath, string filename, string contentType, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Generates a time-limited pre-signed URL for a previously uploaded key.
    /// </summary>
    string GetPresignedUrl(string s3Key);

    /// <summary>
    /// Lists all objects in the files bucket under the given session prefix.
    /// </summary>
    Task<IReadOnlyList<S3FileEntry>> ListFilesAsync(string? sessionId = null, CancellationToken ct = default);

    /// <summary>Downloads an S3 object to a local path.</summary>
    Task DownloadFileAsync(string s3Key, string destinationPath, CancellationToken ct = default);
}

public record S3FileEntry(string Key, string Filename, long SizeBytes, DateTime LastModified, string PresignedUrl);
