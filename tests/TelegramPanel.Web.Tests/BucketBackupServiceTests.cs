using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class BucketBackupServiceTests
{
    [Fact]
    public async Task RunAsync_AddsUnsignedPayloadHeaderForR2PutUpload()
    {
        var root = CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(root, "telegram-panel.db");
            await File.WriteAllTextAsync(databasePath, "db");

            var handler = new CapturingHandler();
            var service = CreateService(
                root,
                databasePath,
                handler,
                new BucketBackupOptions
                {
                    Enabled = true,
                    UploadUrl = "https://example.r2.cloudflarestorage.com/backups/tp.zip",
                    Method = "PUT",
                    TimeoutSeconds = 30
                });

            var result = await service.RunAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(HttpMethod.Put, handler.Method);
            Assert.Equal("application/zip", handler.ContentType);
            Assert.True(handler.SawUnsignedPayloadHeader, handler.FailureMessage);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("https://example.r2.cloudflarestorage.com/backups/tp.zip")]
    [InlineData("https://example.r2.cloudflarestorage.com/backups/tp.zip?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=test&X-Amz-Signature=abc")]
    public void AddS3ContentSha256HeaderIfNeeded_AddsUnsignedPayloadForR2Put(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url);

        BucketBackupService.AddS3ContentSha256HeaderIfNeeded(request, request.RequestUri!);

        Assert.True(request.Headers.TryGetValues(BucketBackupS3Headers.ContentSha256, out var values));
        Assert.Equal(BucketBackupS3Headers.UnsignedPayload, Assert.Single(values));
    }

    [Fact]
    public void AddS3ContentSha256HeaderIfNeeded_UsesSignedContentSha256QueryOnR2()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "https://example.r2.cloudflarestorage.com/backups/tp.zip?X-Amz-Content-Sha256=abc123");

        BucketBackupService.AddS3ContentSha256HeaderIfNeeded(request, request.RequestUri!);

        Assert.True(request.Headers.TryGetValues(BucketBackupS3Headers.ContentSha256, out var values));
        Assert.Equal("abc123", Assert.Single(values));
    }

    [Fact]
    public void AddS3ContentSha256HeaderIfNeeded_DoesNotAffectPlainPutUrl()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "https://backup.example.com/tp.zip");

        BucketBackupService.AddS3ContentSha256HeaderIfNeeded(request, request.RequestUri!);

        Assert.False(request.Headers.Contains(BucketBackupS3Headers.ContentSha256));
    }

    [Fact]
    public void AddS3ContentSha256HeaderIfNeeded_DoesNotAffectPost()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.r2.cloudflarestorage.com/backups/tp.zip");

        BucketBackupService.AddS3ContentSha256HeaderIfNeeded(request, request.RequestUri!);

        Assert.False(request.Headers.Contains(BucketBackupS3Headers.ContentSha256));
    }

    private static BucketBackupService CreateService(
        string root,
        string databasePath,
        HttpMessageHandler handler,
        BucketBackupOptions options)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
                ["AdminAuth:CredentialsPath"] = Path.Combine(root, "admin_auth.json"),
                ["Telegram:SessionsPath"] = Path.Combine(root, "sessions")
            })
            .Build();

        return new BucketBackupService(
            new TestHttpClientFactory(handler),
            configuration,
            new TestWebHostEnvironment(root),
            new TestOptionsMonitor<BucketBackupOptions>(options),
            NullLogger<BucketBackupService>.Instance);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "telegram-panel-bucket-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖断言结果。
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public bool SawUnsignedPayloadHeader { get; private set; }
        public string? FailureMessage { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            SawUnsignedPayloadHeader = request.Headers.TryGetValues(BucketBackupS3Headers.ContentSha256, out var values)
                && values.Contains(BucketBackupS3Headers.UnsignedPayload, StringComparer.Ordinal);
            FailureMessage = SawUnsignedPayloadHeader
                ? null
                : $"Missing {BucketBackupS3Headers.ContentSha256}";

            return Task.FromResult(new HttpResponseMessage(SawUnsignedPayloadHeader ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.BadRequest)
            {
                Content = SawUnsignedPayloadHeader
                    ? null
                    : new StringContent("<Error><Code>InvalidRequest</Code><Message>Missing x-amz-content-sha256</Message></Error>")
            });
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string ApplicationName { get; set; } = "TelegramPanel.Web.Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
