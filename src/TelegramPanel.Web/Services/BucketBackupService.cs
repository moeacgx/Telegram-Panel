using System.IO.Compression;
using Microsoft.Extensions.Options;

namespace TelegramPanel.Web.Services;

public sealed class BucketBackupOptions
{
    public bool Enabled { get; set; }
    public string UploadUrl { get; set; } = string.Empty;
    public string Method { get; set; } = "PUT";
    public string AuthorizationHeader { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 300;
}

internal static class BucketBackupS3Headers
{
    public const string ContentSha256 = "x-amz-content-sha256";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
}

public sealed record BucketBackupSettingsDto(bool Enabled, string UploadUrl, string Method, bool HasAuthorizationHeader, int TimeoutSeconds);
public sealed record SaveBucketBackupSettingsDto(bool Enabled, string? UploadUrl, string? Method, string? AuthorizationHeader, bool ClearAuthorizationHeader, int TimeoutSeconds);
public sealed record BucketBackupResultDto(bool Success, string Message, string? Url, long? SizeBytes, DateTimeOffset CompletedAtUtc);

public sealed class BucketBackupService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IOptionsMonitor<BucketBackupOptions> _options;
    private readonly ILogger<BucketBackupService> _logger;

    public BucketBackupService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IOptionsMonitor<BucketBackupOptions> options,
        ILogger<BucketBackupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _environment = environment;
        _options = options;
        _logger = logger;
    }

    public BucketBackupSettingsDto GetSettings()
    {
        var options = _options.CurrentValue;
        return new BucketBackupSettingsDto(
            options.Enabled,
            options.UploadUrl,
            NormalizeMethod(options.Method),
            !string.IsNullOrWhiteSpace(options.AuthorizationHeader),
            NormalizeTimeout(options.TimeoutSeconds));
    }

    public async Task<BucketBackupResultDto> RunAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
            return Fail("存储桶备份未启用");

        var uploadUrl = BuildUploadUrl(options.UploadUrl);
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Fail("存储桶上传 URL 无效");

        var method = NormalizeMethod(options.Method);
        var timeout = NormalizeTimeout(options.TimeoutSeconds);
        var tempPath = Path.Combine(Path.GetTempPath(), $"telegram-panel-backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.zip");

        try
        {
            var included = await CreateBackupZipAsync(tempPath, cancellationToken);
            if (included == 0)
                return Fail("没有找到可备份的数据文件");

            await using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var request = new HttpRequestMessage(new HttpMethod(method), uri)
            {
                Content = new StreamContent(stream)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            if (!string.IsNullOrWhiteSpace(options.AuthorizationHeader))
                request.Headers.TryAddWithoutValidation("Authorization", options.AuthorizationHeader.Trim());
            AddS3ContentSha256HeaderIfNeeded(request, uri);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            var client = _httpClientFactory.CreateClient(nameof(BucketBackupService));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return Fail($"存储桶上传失败：{(int)response.StatusCode} {response.ReasonPhrase} {TrimForMessage(body)}".Trim());
            }

            var size = new FileInfo(tempPath).Length;
            return new BucketBackupResultDto(true, $"已上传备份：{size} bytes", RedactUrl(uploadUrl), size, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bucket backup failed");
            return Fail($"存储桶备份失败：{ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private async Task<int> CreateBackupZipAsync(string path, CancellationToken cancellationToken)
    {
        var count = 0;
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

        count += await AddFileIfExistsAsync(zip, ResolveDatabasePath(), "telegram-panel.db", cancellationToken);
        count += await AddFileIfExistsAsync(zip, ResolveDatabasePath() + "-wal", "telegram-panel.db-wal", cancellationToken);
        count += await AddFileIfExistsAsync(zip, ResolveDatabasePath() + "-shm", "telegram-panel.db-shm", cancellationToken);
        count += await AddFileIfExistsAsync(zip, LocalConfigFile.ResolvePath(_configuration, _environment), "appsettings.local.json", cancellationToken);
        count += await AddFileIfExistsAsync(zip, _configuration["AdminAuth:CredentialsPath"] ?? "/data/admin_auth.json", "admin_auth.json", cancellationToken);

        var sessions = _configuration["Telegram:SessionsPath"] ?? Path.Combine(_environment.ContentRootPath, "sessions");
        if (Directory.Exists(sessions))
        {
            foreach (var file in Directory.EnumerateFiles(sessions, "*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                count += await AddFileIfExistsAsync(zip, file, $"sessions/{Path.GetFileName(file)}", cancellationToken);
            }
        }

        return count;
    }

    private string ResolveDatabasePath()
    {
        var cs = _configuration.GetConnectionString("DefaultConnection") ?? "Data Source=telegram-panel.db";
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs);
        var source = builder.DataSource;
        return Path.IsPathRooted(source) ? source : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, source));
    }

    private static async Task<int> AddFileIfExistsAsync(ZipArchive zip, string? file, string entryName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return 0;
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await input.CopyToAsync(entryStream, cancellationToken);
        return 1;
    }

    private string BuildUploadUrl(string template)
    {
        var now = DateTimeOffset.UtcNow;
        return (template ?? string.Empty).Trim()
            .Replace("{timestamp}", now.ToString("yyyyMMddHHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", VersionService.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMethod(string? method) =>
        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ? "POST" : "PUT";

    private static int NormalizeTimeout(int timeoutSeconds) => Math.Clamp(timeoutSeconds <= 0 ? 300 : timeoutSeconds, 30, 1800);

    internal static void AddS3ContentSha256HeaderIfNeeded(HttpRequestMessage request, Uri uri)
    {
        if (!string.Equals(request.Method.Method, "PUT", StringComparison.OrdinalIgnoreCase)
            || request.Headers.Contains(BucketBackupS3Headers.ContentSha256))
            return;

        if (!HostLooksLikeCloudflareR2(uri.Host))
            return;

        var contentSha256 = TryGetQueryValue(uri, "X-Amz-Content-Sha256");
        if (string.IsNullOrWhiteSpace(contentSha256))
            contentSha256 = BucketBackupS3Headers.UnsignedPayload;

        request.Headers.TryAddWithoutValidation(
            BucketBackupS3Headers.ContentSha256,
            contentSha256);
    }

    private static bool HostLooksLikeCloudflareR2(string host) =>
        host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase);


    private static string? TryGetQueryValue(Uri uri, string name)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
            return null;

        if (query[0] == '?')
            query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var rawName = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(Uri.UnescapeDataString(rawName), name, StringComparison.OrdinalIgnoreCase))
                continue;

            return separator >= 0
                ? Uri.UnescapeDataString(pair[(separator + 1)..])
                : string.Empty;
        }

        return null;
    }

    private static BucketBackupResultDto Fail(string message) => new(false, message, null, null, DateTimeOffset.UtcNow);

    private static string TrimForMessage(string? value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 160 ? value : value[..160];
    }

    private static string RedactUrl(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? url[..q] + "?***" : url;
    }
}
