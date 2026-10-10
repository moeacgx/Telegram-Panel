using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ProxyCatalogRegressionTests
{
    [Fact]
    public async Task 全局引用Resin会从数据库解析并按账号生成稳定用户名()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resin = fixture.NewProxy("resin-global", OutboundProxyKinds.Resin);
        resin.ResinPlatform = "telegram";
        resin.Username = "不应直接复用";
        fixture.Db.OutboundProxies.Add(resin);
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(resin.Id);

        var first = await new GlobalProxyResolver(fixture.Db, fixture.Configuration)
            .ResolveRequiredAsync("tg_account_42");
        var second = await new GlobalProxyResolver(fixture.Db, fixture.Configuration)
            .ResolveRequiredAsync("tg_account_42");

        Assert.Equal(resin.Id, first.ProxyId);
        Assert.Equal(OutboundProxyKinds.Resin, first.Kind);
        Assert.Equal("telegram.tg_account_42", first.Username);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task 全局代理计入使用中且禁止停用或删除()
    {
        await using var fixture = await Fixture.CreateAsync();
        var global = fixture.NewProxy("global", OutboundProxyKinds.Manual);
        var unused = fixture.NewProxy("unused", OutboundProxyKinds.Manual, port: 1081);
        fixture.Db.OutboundProxies.AddRange(global, unused);
        fixture.Db.Accounts.Add(new Account
        {
            Phone = "8613800000042",
            UserId = 42,
            SessionPath = "sessions/global-42.session",
            ApiId = 1,
            ApiHash = "hash",
            ProxyId = null,
            UseGlobalProxy = true
        });
        await fixture.Db.SaveChangesAsync();
        fixture.SetGlobalProxy(global.Id);

        var used = await fixture.Service.ListAsync("used", null);
        var unusedItems = await fixture.Service.ListAsync("unused", null);

        Assert.Equal(global.Id, Assert.Single(used).Id);
        Assert.Equal(unused.Id, Assert.Single(unusedItems).Id);
        Assert.Equal(1, await fixture.Service.GetGlobalFallbackAccountCountAsync());
        await Assert.ThrowsAsync<ProxyInUseException>(() => fixture.Service.DeleteAsync(global.Id));
        await Assert.ThrowsAsync<ProxyInUseException>(() => fixture.Service.UpdateAsync(
            global.Id,
            ToInput(global, isEnabled: false)));
    }

    [Fact]
    public async Task 代理分类支持批量分配筛选和删除后解除关联()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.NewProxy("first", OutboundProxyKinds.Manual);
        var second = fixture.NewProxy("second", OutboundProxyKinds.Manual, port: 1081);
        fixture.Db.OutboundProxies.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();

        var category = await fixture.Service.CreateCategoryAsync(
            new ProxyCategoryInput("住宅", "#16a34a", "住宅出口"));
        var changed = await fixture.Service.SetCategoriesAsync(
            new[] { first.Id, second.Id },
            category.Id);
        var filtered = await fixture.Service.ListAsync("all", category.Id);

        Assert.Equal(2, changed);
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, proxy => Assert.Equal(category.Id, proxy.Category?.Id));

        await fixture.Service.DeleteCategoryAsync(category.Id);
        fixture.Db.ChangeTracker.Clear();
        Assert.All(
            await fixture.Db.OutboundProxies.AsNoTracking().ToListAsync(),
            proxy => Assert.Null(proxy.CategoryId));
    }

    [Fact]
    public async Task 分类Id为零在创建更新和批量设置时统一持久化为空()
    {
        await using var fixture = await Fixture.CreateAsync();
        var category = await fixture.Service.CreateCategoryAsync(
            new ProxyCategoryInput("临时分类", "#2563eb", null));

        var created = await fixture.Service.CreateAsync(
            ToInput(
                fixture.NewProxy("category-zero", OutboundProxyKinds.Manual, port: 1082),
                isEnabled: true) with
            {
                CategoryId = 0
            });
        Assert.Null((await fixture.Db.OutboundProxies
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.Id)).CategoryId);

        var categorized = await fixture.Service.UpdateAsync(
            created.Id,
            ToInput(created, isEnabled: true) with
            {
                CategoryId = category.Id
            });
        Assert.Equal(category.Id, categorized.CategoryId);

        await fixture.Service.UpdateAsync(
            created.Id,
            ToInput(categorized, isEnabled: true) with
            {
                CategoryId = 0
            });
        Assert.Null((await fixture.Db.OutboundProxies
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.Id)).CategoryId);

        await fixture.Service.SetCategoriesAsync(new[] { created.Id }, category.Id);
        await fixture.Service.SetCategoriesAsync(new[] { created.Id }, 0);
        Assert.Null((await fixture.Db.OutboundProxies
            .AsNoTracking()
            .SingleAsync(x => x.Id == created.Id)).CategoryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 全局已有代理切换会在变更锁内拒绝不可用代理且不执行回调(
        bool useDisabledProxy)
    {
        await using var fixture = await Fixture.CreateAsync();
        var available = fixture.NewProxy("available-global", OutboundProxyKinds.Manual);
        var disabled = fixture.NewProxy(
            "disabled-global",
            OutboundProxyKinds.Manual,
            port: 1081);
        disabled.IsEnabled = false;
        fixture.Db.OutboundProxies.AddRange(available, disabled);
        await fixture.Db.SaveChangesAsync();

        var lockEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingChange = fixture.Service.ExecuteGlobalProxyChangeAsync(
            true,
            "existing",
            available.Id,
            async cancellationToken =>
            {
                lockEntered.TrySetResult(true);
                await releaseLock.Task.WaitAsync(cancellationToken);
            });

        await lockEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var callbackExecuted = false;
        var unavailableProxyId = useDisabledProxy ? disabled.Id : int.MaxValue;
        var rejectedChange = fixture.Service.ExecuteGlobalProxyChangeAsync(
            true,
            "existing",
            unavailableProxyId,
            _ =>
            {
                callbackExecuted = true;
                return Task.CompletedTask;
            });

        try
        {
            Assert.False(rejectedChange.IsCompleted);
        }
        finally
        {
            releaseLock.TrySetResult(true);
        }

        await holdingChange;
        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => rejectedChange);
        Assert.Contains("不存在或已停用", error.Message);
        Assert.False(callbackExecuted);
    }

    [Fact]
    public async Task 正式绑定会在变更锁内拒绝与首连快照不同的代理()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = fixture.NewProxy("frozen-route", OutboundProxyKinds.Manual);
        var account = new Account
        {
            Phone = "8613800000043",
            UserId = 43,
            SessionPath = "sessions/frozen-route.session",
            ApiId = 1,
            ApiHash = "hash",
            IsActive = false,
            UseGlobalProxy = false
        };
        fixture.Db.AddRange(proxy, account);
        await fixture.Db.SaveChangesAsync();
        var frozen = AccountProxyResolver.BuildConnectionOptions(
            proxy,
            "tg_import_frozen");

        proxy.Host = "127.0.0.2";
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ProxyBindingConflictException>(() =>
            fixture.Service.BindAccountsAsync(
                new[] { account.Id },
                new AccountProxyBindingInput("existing", proxy.Id),
                expectedConnection: frozen));

        Assert.Contains("连接参数已变化", error.Message);
        var unchanged = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Null(unchanged.ProxyId);
        Assert.False(unchanged.IsActive);
    }

    private static OutboundProxyInput ToInput(OutboundProxy proxy, bool isEnabled) => new(
        proxy.Name,
        proxy.Kind,
        proxy.Protocol,
        proxy.Host,
        proxy.Port,
        proxy.Username,
        proxy.Password,
        proxy.Secret,
        proxy.ResinPlatform,
        proxy.ResinAdminUrl,
        proxy.ResinAdminToken,
        isEnabled,
        CategoryId: proxy.CategoryId);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IConfigurationRoot _configuration;

        private Fixture(
            SqliteConnection connection,
            AppDbContext db,
            IConfigurationRoot configuration,
            ProxyManagementService service)
        {
            _connection = connection;
            Db = db;
            _configuration = configuration;
            Configuration = configuration;
            Service = service;
        }

        public AppDbContext Db { get; }
        public IConfiguration Configuration { get; }
        public ProxyManagementService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();
            var probe = new ProxyEgressProbeService();
            var service = new ProxyManagementService(
                db,
                new EmptyClientPool(),
                probe,
                NullLogger<ProxyManagementService>.Instance,
                configuration);
            return new Fixture(connection, db, configuration, service);
        }

        public OutboundProxy NewProxy(string name, string kind, int port = 1080) => new()
        {
            Name = name,
            Kind = kind,
            Protocol = OutboundProxyProtocols.Socks5,
            Host = "127.0.0.1",
            Port = port,
            Password = kind == OutboundProxyKinds.Resin ? "token" : null,
            IsEnabled = true
        };

        public void SetGlobalProxy(int proxyId)
        {
            _configuration["Telegram:Proxy:Enabled"] = "true";
            _configuration["Telegram:Proxy:SourceMode"] = "existing";
            _configuration["Telegram:Proxy:ProxyId"] = proxyId.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class EmptyClientPool : ITelegramClientPool
    {
        public int ActiveClientCount => 0;
        public Task<Client> GetOrCreateClientAsync(
            int accountId,
            int apiId,
            string apiHash,
            string sessionPath,
            string? sessionKey = null,
            string? phoneNumber = null,
            long? userId = null) => throw new NotSupportedException();
        public Client? GetClient(int accountId) => null;
        public Task RemoveClientAsync(int accountId) => Task.CompletedTask;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }
}
