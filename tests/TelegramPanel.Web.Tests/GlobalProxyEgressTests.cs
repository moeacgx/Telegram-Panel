using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Api;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class GlobalProxyEgressTests
{
    [Fact]
    public async Task 选择已有代理只保存引用且不会复制代理凭据()
    {
        var (localPath, configuration, environment) = await CreateSettingsContextAsync(
            new Dictionary<string, string?>());
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        var selected = new OutboundProxy
        {
            Name = "已有 Resin",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Socks5,
            Host = "resin.internal",
            Port = 1080,
            Password = "proxy-token",
            ResinAdminToken = "admin-token",
            ResinPlatform = "telegram",
            IsEnabled = true
        };
        db.OutboundProxies.Add(selected);
        await db.SaveChangesAsync();
        var probe = new ProxyEgressProbeService();
        var pool = new EmptyClientPool
        {
            OnRemoveAll = () =>
            {
                Assert.Equal(
                    GlobalTelegramProxyConfiguration.ExistingSourceMode,
                    configuration["Telegram:Proxy:SourceMode"]);
                Assert.Equal(selected.Id.ToString(), configuration["Telegram:Proxy:ProxyId"]);
                return Task.CompletedTask;
            }
        };
        var service = new ProxyManagementService(
            db,
            pool,
            probe,
            NullLogger<ProxyManagementService>.Instance,
            configuration);

        try
        {
            await PanelAdminApiEndpoints.SaveGlobalProxySettingsAsync(
                new SaveGlobalProxySettingsRequestDto(
                    true,
                    "socks5",
                    "should-not-be-saved",
                    9999,
                    "should-not-be-saved",
                    "should-not-be-saved",
                    "",
                    SourceMode: "existing",
                    ProxyId: selected.Id),
                configuration,
                environment,
                pool,
                CancellationToken.None,
                service);

            var saved = await ReadSavedProxyAsync(localPath);
            Assert.True(saved["Enabled"]?.GetValue<bool>());
            Assert.Equal("existing", saved["SourceMode"]?.GetValue<string>());
            Assert.Equal(selected.Id, saved["ProxyId"]?.GetValue<int>());
            Assert.False(saved.ContainsKey("Server"));
            Assert.False(saved.ContainsKey("Password"));
            Assert.False(saved.ContainsKey("Secret"));
        }
        finally
        {
            await db.DisposeAsync();
            TryDelete(localPath);
        }
    }

    [Fact]
    public void 全局代理设置响应只暴露凭据存在标记()
    {
        var propertyNames = typeof(GlobalProxySettingsDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("HasPassword", propertyNames);
        Assert.Contains("HasSecret", propertyNames);
        Assert.DoesNotContain("Password", propertyNames);
        Assert.DoesNotContain("Secret", propertyNames);
    }

    [Fact]
    public async Task 留空保持不会把环境变量代理密码写入本地配置()
    {
        var (localPath, configuration, environment) = await CreateSettingsContextAsync(
            new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "true",
                ["Telegram:Proxy:Protocol"] = "socks5",
                ["Telegram:Proxy:Server"] = "proxy.example.com",
                ["Telegram:Proxy:Port"] = "1080",
                ["Telegram:Proxy:Password"] = "environment-password"
            });

        try
        {
            await PanelAdminApiEndpoints.SaveGlobalProxySettingsAsync(
                new SaveGlobalProxySettingsRequestDto(
                    true,
                    "socks5",
                    "proxy.example.com",
                    1080,
                    "",
                    "",
                    ""),
                configuration,
                environment,
                new EmptyClientPool(),
                CancellationToken.None);

            var proxy = await ReadSavedProxyAsync(localPath);
            Assert.False(proxy.ContainsKey("Password"));
            Assert.Equal("environment-password", configuration["Telegram:Proxy:Password"]);
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    [Fact]
    public async Task 留空保持不会把环境变量MTProxySecret写入本地配置()
    {
        var (localPath, configuration, environment) = await CreateSettingsContextAsync(
            new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "true",
                ["Telegram:Proxy:Protocol"] = "mtproto",
                ["Telegram:Proxy:Server"] = "proxy.example.com",
                ["Telegram:Proxy:Port"] = "443",
                ["Telegram:Proxy:Secret"] = "environment-secret"
            });

        try
        {
            await PanelAdminApiEndpoints.SaveGlobalProxySettingsAsync(
                new SaveGlobalProxySettingsRequestDto(
                    true,
                    "mtproto",
                    "proxy.example.com",
                    443,
                    "",
                    "",
                    ""),
                configuration,
                environment,
                new EmptyClientPool(),
                CancellationToken.None);

            var proxy = await ReadSavedProxyAsync(localPath);
            Assert.False(proxy.ContainsKey("Secret"));
            Assert.Equal("environment-secret", configuration["Telegram:Proxy:Secret"]);
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    [Fact]
    public async Task 显式清除密码会用空值覆盖环境变量凭据()
    {
        var (localPath, configuration, environment) = await CreateSettingsContextAsync(
            new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "true",
                ["Telegram:Proxy:Protocol"] = "socks5",
                ["Telegram:Proxy:Server"] = "proxy.example.com",
                ["Telegram:Proxy:Port"] = "1080",
                ["Telegram:Proxy:Password"] = "environment-password"
            });

        try
        {
            await PanelAdminApiEndpoints.SaveGlobalProxySettingsAsync(
                new SaveGlobalProxySettingsRequestDto(
                    true,
                    "socks5",
                    "proxy.example.com",
                    1080,
                    "",
                    "",
                    "",
                    ClearPassword: true),
                configuration,
                environment,
                new EmptyClientPool(),
                CancellationToken.None);

            var proxy = await ReadSavedProxyAsync(localPath);
            Assert.Equal(string.Empty, proxy["Password"]?.GetValue<string>());
            Assert.Equal(string.Empty, configuration["Telegram:Proxy:Password"]);
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    [Fact]
    public async Task 停用全局代理会保留本地连接参数()
    {
        var initialJson = """
                          {
                            "Telegram": {
                              "Proxy": {
                                "Enabled": true,
                                "Protocol": "socks5",
                                "Server": "proxy.example.com",
                                "Port": 1080,
                                "Password": "local-password"
                              }
                            }
                          }
                          """;
        var (localPath, configuration, environment) = await CreateSettingsContextAsync(
            new Dictionary<string, string?>(),
            initialJson);

        try
        {
            await PanelAdminApiEndpoints.SaveGlobalProxySettingsAsync(
                new SaveGlobalProxySettingsRequestDto(
                    false,
                    "socks5",
                    "",
                    0,
                    "",
                    "",
                    ""),
                configuration,
                environment,
                new EmptyClientPool(),
                CancellationToken.None);

            var proxy = await ReadSavedProxyAsync(localPath);
            Assert.False(proxy["Enabled"]?.GetValue<bool>());
            Assert.Equal("proxy.example.com", proxy["Server"]?.GetValue<string>());
            Assert.Equal("local-password", proxy["Password"]?.GetValue<string>());
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    [Fact]
    public void 显式HTTP全局代理不会被遗留Secret误判为MTProxy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Protocol"] = "http",
                ["Telegram:Proxy:Server"] = "proxy.example.com",
                ["Telegram:Proxy:Port"] = "8080",
                ["Telegram:Proxy:Username"] = "user",
                ["Telegram:Proxy:Password"] = "password",
                ["Telegram:Proxy:Secret"] = "legacy-secret"
            })
            .Build();

        var proxy = GlobalTelegramProxyConfiguration.BuildRequired(configuration);

        Assert.Equal("http", proxy.Protocol);
        Assert.Equal("proxy.example.com", proxy.Host);
        Assert.Equal(8080, proxy.Port);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("password", proxy.Password);
        Assert.Null(proxy.Secret);
    }

    [Fact]
    public void 显式MTProxy全局代理缺少Secret时闭锁()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Protocol"] = "mtproto",
                ["Telegram:Proxy:Server"] = "proxy.example.com",
                ["Telegram:Proxy:Port"] = "443"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            GlobalTelegramProxyConfiguration.BuildRequired(configuration));

        Assert.Contains("Secret", error.Message);
    }

    [Fact]
    public void 无效全局代理协议不会降级为直连或其他协议()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Protocol"] = "ftp",
                ["Telegram:Proxy:Server"] = "proxy.example.com",
                ["Telegram:Proxy:Port"] = "21"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            GlobalTelegramProxyConfiguration.BuildRequired(configuration));

        Assert.Contains("协议无效", error.Message);
    }

    [Fact]
    public void 显式关闭全局代理会覆盖遗留连接配置()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "false",
                ["Telegram:Proxy:Protocol"] = "socks5",
                ["Telegram:Proxy:Server"] = "proxy.example.com",
                ["Telegram:Proxy:Port"] = "1080"
            })
            .Build();

        Assert.Null(GlobalTelegramProxyConfiguration.Build(configuration));
        var error = Assert.Throws<InvalidOperationException>(() =>
            GlobalTelegramProxyConfiguration.BuildRequired(configuration));
        Assert.Contains("尚未配置", error.Message);
    }

    [Fact]
    public void 显式启用但缺少地址时不会被当作未配置静默忽略()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "true",
                ["Telegram:Proxy:Protocol"] = "socks5"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            GlobalTelegramProxyConfiguration.Build(configuration));
        Assert.Contains("地址或端口", error.Message);
    }

    [Fact]
    public async Task 全局代理账号检测使用Telegram实际代理而不是面板直连()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Accounts.Add(new Account
        {
            Phone = "8613800000099",
            UserId = 90099,
            SessionPath = "sessions/global-proxy.session",
            ApiId = 1,
            ApiHash = "hash",
            UseGlobalProxy = true
        });
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Server"] = "mtproxy.example.com",
                ["Telegram:Proxy:Port"] = "443",
                ["Telegram:Proxy:Secret"] = "ee0123456789abcdef0123456789abcdef"
            })
            .Build();
        var probe = new ProxyEgressProbeService();
        var service = new ProxyManagementService(
            db,
            new EmptyClientPool(),
            probe,
            NullLogger<ProxyManagementService>.Instance,
            configuration);

        var result = await service.ProbeAccountAsync(1);

        Assert.False(result.Success);
        Assert.Contains("MTProxy", result.Error);
    }

    private sealed class EmptyClientPool : ITelegramClientPool
    {
        public int ActiveClientCount => 0;
        public Func<Task>? OnRemoveAll { get; init; }

        public Task<Client> GetOrCreateClientAsync(
            int accountId,
            int apiId,
            string apiHash,
            string sessionPath,
            string? sessionKey = null,
            string? phoneNumber = null,
            long? userId = null) =>
            throw new NotSupportedException();

        public Client? GetClient(int accountId) => null;
        public Task RemoveClientAsync(int accountId) => Task.CompletedTask;
        public Task RemoveAllClientsAsync() => OnRemoveAll?.Invoke() ?? Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }

    private static async Task<(string Path, ConfigurationManager Configuration, TestWebHostEnvironment Environment)>
        CreateSettingsContextAsync(
            IDictionary<string, string?> values,
            string initialJson = "{}")
    {
        var localPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-global-proxy-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(localPath, initialJson);

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values.Concat(
            new[] { new KeyValuePair<string, string?>("LocalConfig:Path", localPath) }));
        configuration.AddJsonFile(localPath, optional: false, reloadOnChange: false);
        var environment = new TestWebHostEnvironment
        {
            ContentRootPath = Path.GetDirectoryName(localPath)!
        };
        return (localPath, configuration, environment);
    }

    private static async Task<JsonObject> ReadSavedProxyAsync(string localPath)
    {
        var json = await File.ReadAllTextAsync(localPath);
        return JsonNode.Parse(json)!["Telegram"]!["Proxy"]!.AsObject();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 测试临时文件的清理失败不应掩盖断言结果。
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "TelegramPanel.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
