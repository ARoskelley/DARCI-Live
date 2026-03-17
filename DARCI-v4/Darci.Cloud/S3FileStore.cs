using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Cloud;

public sealed class S3FileStore : IS3FileStore
{
    private readonly AmazonS3Client _s3;
    private readonly CloudConfig _config;
    private readonly ILogger<S3FileStore> _logger;

    public S3FileStore(CloudConfig config, ILogger<S3FileStore>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<S3FileStore>.Instance;
        _s3 = new AmazonS3Client(
            new BasicAWSCredentials(config.AccessKeyId, config.SecretAccessKey),
            RegionEndpoint.GetBySystemName(config.Region));
    }

    public async Task<string> UploadFileAsync(
        string localPath, string filename, string contentType, string sessionId, CancellationToken ct = default)
    {
        var key = $"sessions/{sessionId}/{filename}";

        using var transfer = new TransferUtility(_s3);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            FilePath    = localPath,
            BucketName  = _config.FilesBucket,
            Key         = key,
            ContentType = contentType
        }, ct);

        _logger.LogInformation("Uploaded {Filename} → s3://{Bucket}/{Key}", filename, _config.FilesBucket, key);
        return key;
    }

    public string GetPresignedUrl(string s3Key)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _config.FilesBucket,
            Key        = s3Key,
            Expires    = DateTime.UtcNow.AddMinutes(_config.PresignedUrlExpiryMinutes),
            Verb       = HttpVerb.GET
        };
        return _s3.GetPreSignedURL(req);
    }

    public async Task<IReadOnlyList<S3FileEntry>> ListFilesAsync(string? sessionId = null, CancellationToken ct = default)
    {
        var prefix = sessionId is null ? "sessions/" : $"sessions/{sessionId}/";

        var request  = new ListObjectsV2Request { BucketName = _config.FilesBucket, Prefix = prefix };
        var response = await _s3.ListObjectsV2Async(request, ct);

        return response.S3Objects.Select(obj => new S3FileEntry(
            obj.Key,
            obj.Key.Split('/').Last(),
            obj.Size,
            obj.LastModified,
            GetPresignedUrl(obj.Key)
        )).ToList();
    }

    public async Task DownloadFileAsync(string s3Key, string destinationPath, CancellationToken ct = default)
    {
        using var transfer = new TransferUtility(_s3);
        await transfer.DownloadAsync(destinationPath, _config.FilesBucket, s3Key, ct);
    }
}
