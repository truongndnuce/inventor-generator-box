using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Cadflow.Api;

/// <summary>Cloudflare R2 configuration. Set it through user-secrets or environment variables (<c>R2__AccountId</c>...), not in appsettings.json.</summary>
public sealed class R2Options
{
    public string? AccountId { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    /// <summary>Default <c>https://&lt;AccountId&gt;.r2.cloudflarestorage.com</c>; change it when using the EU/FedRAMP jurisdiction.</summary>
    public string? Endpoint { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Bucket) && !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey)
        && (!string.IsNullOrWhiteSpace(AccountId) || !string.IsNullOrWhiteSpace(Endpoint));
}

/// <summary>
/// Document store on R2 (S3 API). The worker uploads straight to it with a presigned PUT, users download with a short-lived presigned GET;
/// the bucket stays private and the R2 key lives only on the server.
/// </summary>
public sealed class R2Store : IDisposable
{
    public static readonly TimeSpan PutLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan GetLifetime = TimeSpan.FromMinutes(5);

    private readonly R2Options _options;
    private readonly AmazonS3Client? _s3;

    public R2Store(R2Options options)
    {
        _options = options;
        if (!options.IsConfigured) return;
        var config = new AmazonS3Config
        {
            ServiceURL = options.Endpoint?.TrimEnd('/') ?? $"https://{options.AccountId}.r2.cloudflarestorage.com",
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            // R2 does not support the SDK's new default checksum (CRC32 trailer): compute it only when the protocol requires it.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    public bool IsConfigured => _s3 != null;

    public static string KeyFor(string jobId, string name) => $"jobs/{jobId}/{name}";

    /// <summary>Short-lived PUT URL for the worker to upload a file. The worker sends the file body as is, no special header needed.</summary>
    public string PresignPut(string key, DateTime utcNow) => Presign(key, HttpVerb.PUT, PutLifetime, utcNow, null);

    /// <summary>Short-lived GET URL. With <paramref name="download"/> = true the browser saves the file instead of displaying it.</summary>
    public string PresignGet(string key, string fileName, string contentType, bool download, DateTime utcNow) =>
        Presign(key, HttpVerb.GET, GetLifetime, utcNow, new ResponseHeaderOverrides
        {
            ContentType = contentType,
            ContentDisposition = $"{(download ? "attachment" : "inline")}; filename=\"{fileName}\"",
        });

    private string Presign(string key, HttpVerb verb, TimeSpan lifetime, DateTime utcNow, ResponseHeaderOverrides? overrides)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket, Key = key, Verb = verb, Expires = utcNow + lifetime, Protocol = Protocol.HTTPS,
        };
        if (overrides != null) request.ResponseHeaderOverrides = overrides;
        return _s3!.GetPreSignedURL(request);
    }

    /// <summary>Size of the object, or null if it does not exist yet.</summary>
    public async Task<long?> SizeAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var meta = await _s3!.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = key }, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Key of a document-vault file. Outside <c>jobs/</c>, so the retention policy never touches it.</summary>
    public static string KeyForVault(string itemId, int version, string name) => $"vault/{itemId}/v{version}/{name}";

    /// <summary>Uploads a local file (the server has the bytes, e.g. a vault upload).</summary>
    public async Task PutFileAsync(string key, string path, string contentType, CancellationToken ct = default) =>
        await _s3!.PutObjectAsync(new PutObjectRequest { BucketName = _options.Bucket, Key = key, FilePath = path, ContentType = contentType }, ct);

    /// <summary>Copies an object inside the bucket without downloading it (job result -> vault).</summary>
    public async Task CopyAsync(string sourceKey, string destKey, CancellationToken ct = default) =>
        await _s3!.CopyObjectAsync(new CopyObjectRequest { SourceBucket = _options.Bucket, SourceKey = sourceKey, DestinationBucket = _options.Bucket, DestinationKey = destKey }, ct);

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _s3!.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.Bucket, Key = key }, ct);

    public void Dispose() => _s3?.Dispose();
}
