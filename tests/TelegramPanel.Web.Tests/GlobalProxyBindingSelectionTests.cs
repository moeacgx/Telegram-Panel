using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

/// <summary>
/// 验证全局代理引用已有代理时的解析，以及导入后的长期账号路由。
/// </summary>
public sealed class GlobalProxyBindingSelectionTests
{
    [Fact]
    public async Task 旧容器全局代理会闭锁且不能降级直连()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var legacy = fixture.AddProxy("retired", OutboundProxyKinds.Warp, OutboundProxyProtocols.Socks5);
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(legacy.Id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GlobalProxyResolver(fixture.Db, fixture.Configuration).ResolveRequiredAsync("tg_account_42"));
        Assert.Contains("已退役", error.Message);
        Assert.Contains("阻止", error.Message);
    }

    [Fact]
    public async Task 导入existing代理会持久化专属ProxyId且不受全局切换影响()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var globalProxy = fixture.AddProxy(
            "global",
            OutboundProxyKinds.Warp,
            OutboundProxyProtocols.Socks5);
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(globalProxy.Id);

        var result = await fixture.ImportService.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput(
                "existing",
                fixture.SelectedProxy.Id));

        Assert.True(result.Success, result.Error);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(fixture.SelectedProxy.Id, account.ProxyId);
        Assert.False(account.UseGlobalProxy);

        var replacementGlobal = fixture.AddProxy(
            "replacement-global",
            OutboundProxyKinds.Warp,
            OutboundProxyProtocols.Socks5);
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(replacementGlobal.Id);
        var resolver = fixture.CreateAccountProxyResolver();
        var resolved = await resolver.ResolveAsync(account.Id);

        Assert.NotNull(resolved.Proxy);
        Assert.Equal(fixture.SelectedProxy.Id, resolved.Proxy!.ProxyId);
        Assert.False(resolved.UseGlobalProxy);
    }

    [Fact]
    public async Task 导入global代理会持久化为空ProxyId且继承全局出口()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var globalProxy = fixture.AddProxy(
            "global-manual",
            OutboundProxyKinds.Manual,
            OutboundProxyProtocols.Socks5);
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(globalProxy.Id);

        var result = await fixture.ImportService.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("global"));

        Assert.True(result.Success, result.Error);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Null(account.ProxyId);
        Assert.True(account.UseGlobalProxy);

        var resolver = fixture.CreateAccountProxyResolver();
        var resolved = await resolver.ResolveAsync(account.Id);
        Assert.NotNull(resolved.Proxy);
        Assert.Equal(globalProxy.Id, resolved.Proxy!.ProxyId);
    }

    [Theory]
    [InlineData(OutboundProxyKinds.Manual, OutboundProxyProtocols.Socks5)]
    [InlineData(OutboundProxyKinds.Resin, OutboundProxyProtocols.Http)]
    public async Task 全局existing模式按ProxyId解析普通代理或Resin(string kind, string protocol)
    {
        await using var fixture = await TestFixture.CreateAsync();
        var proxy = fixture.AddProxy("selected", kind, protocol);
        if (kind == OutboundProxyKinds.Resin)
            proxy.ResinPlatform = "telegram-panel";
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(proxy.Id);

        var resolver = new GlobalProxyResolver(fixture.Db, fixture.Configuration);
        var options = await resolver.ResolveRequiredAsync("tg_account_42");

        Assert.Equal(proxy.Id, options.ProxyId);
        Assert.Equal(kind, options.Kind);
        Assert.Equal(protocol, options.Protocol);
        if (kind == OutboundProxyKinds.Resin)
            Assert.Equal("telegram-panel.tg_account_42", options.Username);
    }

    [Fact]
    public async Task 全局引用已停用代理时闭锁而不降级直连()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var proxy = fixture.AddProxy(
            "disabled",
            OutboundProxyKinds.Warp,
            OutboundProxyProtocols.Socks5);
        proxy.IsEnabled = false;
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(proxy.Id);

        var resolver = new GlobalProxyResolver(fixture.Db, fixture.Configuration);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveRequiredAsync("tg_account_42"));

        Assert.Contains("停用", error.Message);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly StubClientPool _clientPool;
        private readonly ProxyManagementService _proxyManagement;
        private readonly ServiceProvider _resolverProvider;

        private TestFixture(
            SqliteConnection connection,
            AppDbContext db,
            IConfiguration configuration,
            AccountImportService importService,
            OutboundProxy selectedProxy,
            StubClientPool clientPool,
            ProxyManagementService proxyManagement,
            ServiceProvider resolverProvider)
        {
            _connection = connection;
            Db = db;
            Configuration = configuration;
            ImportService = importService;
            SelectedProxy = selectedProxy;
            _clientPool = clientPool;
            _proxyManagement = proxyManagement;
            _resolverProvider = resolverProvider;
        }

        public AppDbContext Db { get; }
        public IConfiguration Configuration { get; }
        public AccountImportService ImportService { get; }
        public OutboundProxy SelectedProxy { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var selectedProxy = new OutboundProxy
            {
                Name = "selected",
                Kind = OutboundProxyKinds.Manual,
                Protocol = OutboundProxyProtocols.Http,
                Host = "127.0.0.1",
                Port = 1080,
                IsEnabled = true
            };
            db.OutboundProxies.Add(selectedProxy);
            await db.SaveChangesAsync();

            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "true",
                ["Telegram:Proxy:SourceMode"] = GlobalTelegramProxyConfiguration.ExistingSourceMode,
                ["Telegram:Proxy:ProxyId"] = string.Empty
            });

            var clientPool = new StubClientPool();
            var probe = new ProxyEgressProbeService();
            var proxyManagement = new ProxyManagementService(
                db,
                clientPool,
                probe,
                NullLogger<ProxyManagementService>.Instance,
                configuration);
            var accountManagement = new AccountManagementService(
                new AccountRepository(db),
                new ChannelRepository(db),
                new GroupRepository(db),
                clientPool,
                configuration,
                NullLogger<AccountManagementService>.Instance,
                proxyManagement,
                new SessionPathResolver(configuration));
            var importService = new AccountImportService(
                new StubSessionImporter(),
                db,
                accountManagement,
                NullLogger<AccountImportService>.Instance,
                configuration,
                proxyManagement,
                new TemporaryWarpClaimStore());

            var resolverProvider = new ServiceCollection()
                .AddScoped(_ => db)
                .BuildServiceProvider();
            return new TestFixture(
                connection,
                db,
                configuration,
                importService,
                selectedProxy,
                clientPool,
                proxyManagement,
                resolverProvider);
        }

        public OutboundProxy AddProxy(string name, string kind, string protocol)
        {
            var proxy = new OutboundProxy
            {
                Name = name,
                Kind = kind,
                Protocol = protocol,
                Host = "127.0.0.2",
                Port = 1081,
                IsEnabled = true
            };
            Db.OutboundProxies.Add(proxy);
            return proxy;
        }

        public void SetGlobalProxy(int proxyId)
        {
            ((IConfigurationRoot)Configuration)["Telegram:Proxy:ProxyId"] = proxyId.ToString();
        }

        public AccountProxyResolver CreateAccountProxyResolver() =>
            new(_resolverProvider.GetRequiredService<IServiceScopeFactory>(), Configuration);

        public async ValueTask DisposeAsync()
        {
            _resolverProvider.Dispose();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubSessionImporter : ISessionImporter
    {
        public Task<ImportResult> ImportFromSessionFileAsync(
            string filePath,
            int apiId,
            string apiHash,
            long? userId = null,
            string? phoneHint = null,
            string? sessionKey = null,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<List<ImportResult>> BatchImportSessionFilesAsync(
            string[] filePaths,
            int apiId,
            string apiHash,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ImportResult> { Success() });

        public Task<ImportResult> ImportFromStringSessionAsync(
            string sessionString,
            int apiId,
            string apiHash,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<bool> ValidateSessionAsync(string sessionPath) =>
            Task.FromResult(true);

        private static ImportResult Success() => new(
            true,
            "8613800000000",
            10001,
            "test-account",
            "sessions/8613800000000.session");
    }

    private sealed class StubClientPool : ITelegramClientPool
    {
        public int ActiveClientCount => 0;

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

        public Task RemoveAllClientsAsync() => Task.CompletedTask;

        public bool IsClientConnected(int accountId) => false;
    }
}
