using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Cadflow.Api;

/// <summary>Cấu hình Cloudflare R2. Đặt qua user-secrets hoặc biến môi trường (<c>R2__AccountId</c>...), không ghi vào appsettings.json.</summary>
public sealed class R2Options
{
    public string? AccountId { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    /// <summary>Mặc định <c>https://&lt;AccountId&gt;.r2.cloudflarestorage.com</c>; đổi khi dùng jurisdiction EU/FedRAMP.</summary>
    public string? Endpoint { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Bucket) && !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey)
        && (!string.IsNullOrWhiteSpace(AccountId) || !string.IsNullOrWhiteSpace(Endpoint));
}

/// <summary>
/// Kho tài liệu trên R2 (S3 API). Worker tải thẳng lên bằng presigned PUT, người dùng tải xuống bằng presigned GET có hạn ngắn;
/// bucket để private và khóa R2 chỉ nằm ở server.
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
            // R2 chưa hỗ trợ checksum mặc định mới của SDK (CRC32 trailer): chỉ tính khi giao thức bắt buộc.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    public bool IsConfigured => _s3 != null;

    public static string KeyFor(string jobId, string name) => $"jobs/{jobId}/{name}";

    /// <summary>URL PUT có hạn để worker tải file lên. Worker gửi đúng thân file, không cần header đặc biệt.</summary>
    public string PresignPut(string key, DateTime utcNow) => Presign(key, HttpVerb.PUT, PutLifetime, utcNow, null);

    /// <summary>URL GET có hạn. <paramref name="download"/> = true thì trình duyệt lưu file thay vì hiển thị.</summary>
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

    /// <summary>Kích thước object, hoặc null nếu chưa có.</summary>
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

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _s3!.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.Bucket, Key = key }, ct);

    public void Dispose() => _s3?.Dispose();
}
