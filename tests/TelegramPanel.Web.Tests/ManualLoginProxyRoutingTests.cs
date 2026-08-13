using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Web.Services;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ManualLoginProxyRoutingTests
{
    [Fact]
    public void 二维码临时客户端在发起登录前已安装显式代理()
    {
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-qr-login-{Guid.NewGuid():N}.session");
        var service = new AccountService(
            new StubClientPool(),
            NullLogger<AccountService>.Instance,
            new ConfigurationBuilder().Build());
        var method = typeof(AccountService).GetMethod(
            "CreateStandaloneClient",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到二维码客户端创建方法");
        var proxy = new ProxyConnectionOptions(
            89,
            "qr-login",
            OutboundProxyKinds.Manual,
            OutboundProxyProtocols.Http,
            "127.0.0.1",
            8080,
            null,
            null,
            null);

        Client? client = null;
        try
        {
            client = (Client)method.Invoke(service, new object?[]
            {
                12345,
                "0123456789abcdef0123456789abcdef",
                sessionPath,
                "0123456789abcdef0123456789abcdef",
                null,
                new AccountProxyResolution(proxy, false)
            })!;

            Assert.NotNull(client.TcpHandler);
        }
        finally
        {
            client?.Dispose();
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
        }
    }

    [Fact]
    public void 二维码临时客户端缺少明确路由对象时拒绝隐式直连()
    {
        var service = new AccountService(
            new StubClientPool(),
            NullLogger<AccountService>.Instance,
            new ConfigurationBuilder().Build());
        var method = typeof(AccountService).GetMethod(
            "CreateStandaloneClient",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到二维码客户端创建方法");

        var error = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(service, new object?[]
            {
                12345,
                "0123456789abcdef0123456789abcdef",
                Path.Combine(Path.GetTempPath(), $"telegram-panel-null-route-{Guid.NewGuid():N}.session"),
                "0123456789abcdef0123456789abcdef",
                null,
                null
            }));

        Assert.IsType<ArgumentNullException>(error.InnerException);
    }

    [Fact]
    public async Task 临时登录客户端优先使用显式路由且不会查询不存在的账号()
    {
        var resolver = new CountingProxyResolver();
        using var pool = new TelegramClientPool(
            new ConfigurationBuilder().Build(),
            NullLogger<TelegramClientPool>.Instance,
            new TelegramAccountUpdateHub(),
            resolver,
            new SessionPathResolver(new ConfigurationBuilder().Build()));
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-manual-login-{Guid.NewGuid():N}.session");

        try
        {
            var proxy = new ProxyConnectionOptions(
                88,
                "manual-login",
                OutboundProxyKinds.Manual,
                OutboundProxyProtocols.Socks5,
                "127.0.0.1",
                1080,
                null,
                null,
                null);
            var client = await pool.GetOrCreateClientAsync(
                90001,
                12345,
                "0123456789abcdef0123456789abcdef",
                sessionPath,
                "0123456789abcdef0123456789abcdef",
                "8613800000000",
                null,
                new AccountProxyResolution(proxy, false));

            Assert.NotNull(client.TcpHandler);
            Assert.Equal(0, resolver.CallCount);
        }
        finally
        {
            await pool.RemoveClientAsync(90001);
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task 登录会话严格释放会传递底层断开失败()
    {
        var pool = new StubClientPool
        {
            StrictRemoveError = new IOException("模拟旧客户端释放失败")
        };
        var service = new AccountService(
            pool,
            NullLogger<AccountService>.Instance,
            new ConfigurationBuilder().Build());

        var error = await Assert.ThrowsAsync<IOException>(() =>
            service.ReleaseClientStrictAsync(90003));

        Assert.Contains("模拟旧客户端释放失败", error.Message);
        Assert.Contains(90003, pool.StrictlyRemovedAccountIds);
    }

    [Fact]
    public async Task 未明确选择代理时在首次连接前拒绝登录()
    {
        await using var fixture = await Fixture.CreateAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Coordinator.PrepareAsync(1001, null, null));

        Assert.Contains("明确选择登录代理", error.Message);
        Assert.False(fixture.Coordinator.HasState(1001));
    }

    [Fact]
    public async Task 未配置全局代理时不能把全局策略静默降级为直连()
    {
        await using var fixture = await Fixture.CreateAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.PrepareAsync(1002, "global", null));

        Assert.Contains("全局代理尚未配置", error.Message);
        Assert.False(fixture.Coordinator.HasState(1002));
    }

    [Fact]
    public async Task 同手机号重新登录前会停用并释放既有正式客户端()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.AddInactiveAccountAsync();
        account.IsActive = true;
        await fixture.Db.SaveChangesAsync();
        await fixture.Coordinator.PrepareAsync(1011, "direct", null);

        var existingId = await fixture.Coordinator.QuiesceExistingAccountAsync(
            1011,
            "+86 138-0000-0000");

        Assert.Equal(account.Id, existingId);
        Assert.False(await fixture.Db.Accounts.AsNoTracking()
            .Where(x => x.Id == account.Id)
            .Select(x => x.IsActive)
            .SingleAsync());
        Assert.Contains(account.Id, fixture.ClientPool.StrictlyRemovedAccountIds);
    }

    [Fact]
    public async Task 同手机号旧客户端无法严格释放时保持停用并拒绝重登()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.AddInactiveAccountAsync();
        account.IsActive = true;
        await fixture.Db.SaveChangesAsync();
        await fixture.Coordinator.PrepareAsync(1012, "direct", null);
        fixture.ClientPool.StrictRemoveError = new IOException("模拟旧客户端释放失败");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.QuiesceExistingAccountAsync(
                1012,
                "+86 138-0000-0000"));

        Assert.Contains("无法安全停止", error.Message);
        Assert.Contains(account.Id, fixture.ClientPool.StrictlyRemovedAccountIds);
        Assert.False(await fixture.Db.Accounts.AsNoTracking()
            .Where(x => x.Id == account.Id)
            .Select(x => x.IsActive)
            .SingleAsync());
    }

    [Fact]
    public async Task 同一手机号的第二个登录会话会在首次连接前拒绝()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Coordinator.PrepareAsync(1013, "direct", null);
        await fixture.Coordinator.PrepareAsync(1014, "direct", null);
        await fixture.Coordinator.QuiesceExistingAccountAsync(
            1013,
            "+86 138-0000-0000");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.QuiesceExistingAccountAsync(
                1014,
                "8613800000000"));

        Assert.Contains("已有登录会话", error.Message);

        await fixture.Coordinator.AbandonAsync(1013);
        await fixture.Coordinator.AbandonAsync(1014);
    }

    [Fact]
    public async Task 已有代理在登录前冻结并在账号启用前完成绑定()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync(OutboundProxyKinds.Manual);
        var account = await fixture.AddInactiveAccountAsync();

        var state = await fixture.Coordinator.PrepareAsync(
            1003,
            "existing",
            proxy.Id);

        Assert.Equal(proxy.Id, state.Resolution.Proxy?.ProxyId);
        Assert.Equal("127.0.0.1", state.Resolution.Proxy?.Host);
        Assert.False(account.IsActive);

        await fixture.Coordinator.CompleteAsync(1003, account.Id);

        var saved = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.True(saved.IsActive);
        Assert.Equal(proxy.Id, saved.ProxyId);
        Assert.False(saved.UseGlobalProxy);
        Assert.False(fixture.Coordinator.HasState(1003));
    }

    [Fact]
    public async Task 登录后代理失效时账号保持停用且不会降级为直连()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync(OutboundProxyKinds.Manual);
        var account = await fixture.AddInactiveAccountAsync();
        await fixture.Coordinator.PrepareAsync(1007, "existing", proxy.Id);

        proxy.IsEnabled = false;
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            fixture.Coordinator.CompleteAsync(1007, account.Id));

        var saved = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(saved.IsActive);
        Assert.Null(saved.ProxyId);
        Assert.False(fixture.Coordinator.HasState(1007));
    }

    [Fact]
    public async Task 登录期间代理地址变化时阻止账号切换出口后启用()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync(OutboundProxyKinds.Manual);
        var account = await fixture.AddInactiveAccountAsync();
        await fixture.Coordinator.PrepareAsync(1009, "existing", proxy.Id);

        proxy.Host = "127.0.0.2";
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.CompleteAsync(1009, account.Id));

        Assert.Contains("连接参数已变化", error.Message);
        var saved = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(saved.IsActive);
        Assert.Null(saved.ProxyId);
    }

    [Fact]
    public async Task Resin出口无法继承时正式账号保持停用避免切换IP()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync(OutboundProxyKinds.Resin);
        proxy.Port = 1;
        proxy.Password = "proxy-token";
        proxy.ResinPlatform = "Default";
        await fixture.Db.SaveChangesAsync();
        var account = await fixture.AddInactiveAccountAsync();
        await fixture.Coordinator.PrepareAsync(1010, "existing", proxy.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.CompleteAsync(1010, account.Id));

        Assert.Contains("避免切换 IP", error.Message);
        var saved = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(saved.IsActive);
        Assert.Equal(proxy.Id, saved.ProxyId);
    }

    [Fact]
    public async Task 自动分配已有WARP会选择空闲候选并绑定正式账号()
    {
        await using var fixture = await Fixture.CreateAsync();
        var busyWarp = await fixture.AddActiveWarpProxyAsync("busy-warp");
        var freeWarp = await fixture.AddActiveWarpProxyAsync("free-warp");
        var busyState = await fixture.Coordinator.PrepareAsync(1004, "existing", busyWarp.Id);
        Assert.Equal(busyWarp.Id, busyState.Resolution.Proxy?.ProxyId);
        var account = await fixture.AddInactiveAccountAsync();

        var state = await fixture.Coordinator.PrepareAsync(
            1005,
            "warp_pool",
            null);

        Assert.Equal("warp_pool", state.FrozenStrategy);
        Assert.Equal(freeWarp.Id, state.Resolution.Proxy?.ProxyId);
        await fixture.Coordinator.CompleteAsync(1005, account.Id);

        var saved = await fixture.Db.Accounts.AsNoTracking().SingleAsync(x => x.Id == account.Id);
        Assert.True(saved.IsActive);
        Assert.Equal(freeWarp.Id, saved.ProxyId);
    }

    [Fact]
    public async Task 无可分配已有WARP时不会登记登录会话或开始Telegram连接()
    {
        await using var fixture = await Fixture.CreateAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.PrepareAsync(1008, "warp_pool", null));

        Assert.Contains("没有可自动分配的已有 WARP", error.Message);
        Assert.False(fixture.Coordinator.HasState(1008));
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking()
            .Where(x => x.Kind == OutboundProxyKinds.Warp)
            .ToListAsync());
    }

    [Fact]
    public void 重启孤儿清理只选择过期且未被登录会话持有的WARP()
    {
        var store = new AccountLoginProxyStateStore();
        var temporaryWarpClaims = new TemporaryWarpClaimStore();
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        var proxy = new OutboundProxy
        {
            Id = 501,
            Kind = OutboundProxyKinds.Warp,
            Accounts = new List<Account>(),
            WarpProfile = new WarpProfile
            {
                RequestId = $"{AccountLoginProxyCoordinator.ManagedWarpRequestPrefix}123.abc",
                CreatedAtUtc = cutoff.UtcDateTime.AddMinutes(-1)
            }
        };

        Assert.True(AccountLoginProxyCleanupService.IsRestartOrphan(
            proxy,
            cutoff,
            store,
            temporaryWarpClaims));

        store.ClaimWarpProxy(proxy.Id);
        Assert.False(AccountLoginProxyCleanupService.IsRestartOrphan(
            proxy,
            cutoff,
            store,
            temporaryWarpClaims));

        store.ReleaseWarpProxyClaim(proxy.Id);
        proxy.WarpProfile.RequestId = "account-123";
        Assert.False(AccountLoginProxyCleanupService.IsRestartOrphan(
            proxy,
            cutoff,
            store,
            temporaryWarpClaims));

        Assert.True(AccountLoginProxyCoordinator.IsManagedWarpRequestId(
            $"{AccountLoginProxyCoordinator.ManagedWarpRequestPrefix}99.abc"));
        Assert.False(AccountLoginProxyCoordinator.IsManagedWarpRequestId("login-99-abc"));

        proxy.WarpProfile.RequestId = $"{AccountImportService.ManagedWarpRequestPrefix}abc";
        Assert.True(AccountImportService.IsManagedWarpRequestId(proxy.WarpProfile.RequestId));
        using (temporaryWarpClaims.ClaimRequest(proxy.WarpProfile.RequestId!))
        {
            Assert.False(AccountLoginProxyCleanupService.IsRestartOrphan(
                proxy,
                cutoff,
                store,
                temporaryWarpClaims));
        }
        Assert.True(AccountLoginProxyCleanupService.IsRestartOrphan(
            proxy,
            cutoff,
            store,
            temporaryWarpClaims));
    }

    [Fact]
    public void 正式绑定取得会话时WARP所有权无空窗切换为Claim()
    {
        var store = new AccountLoginProxyStateStore();
        var proxy = new ProxyConnectionOptions(
            601,
            "login-warp",
            OutboundProxyKinds.Warp,
            OutboundProxyProtocols.Socks5,
            "127.0.0.1",
            1080,
            null,
            null,
            null);
        Assert.True(store.TryAdd(new AccountLoginProxyState(
            1601,
            new AccountProxyBindingInput("existing", proxy.ProxyId),
            "existing",
            new AccountProxyResolution(proxy, false),
            null,
            null,
            null,
            DateTimeOffset.UtcNow)));

        Assert.True(store.OwnsWarpProxy(proxy.ProxyId));
        Assert.True(store.TryTakeForCompletion(1601, out var claimed));
        Assert.NotNull(claimed);
        Assert.False(store.TryTakeForCompletion(1601, out _));
        Assert.True(store.OwnsWarpProxy(proxy.ProxyId));

        store.ReleaseWarpProxyClaim(proxy.ProxyId);
        store.ReleaseLoginClaim(1601);
        Assert.False(store.OwnsWarpProxy(proxy.ProxyId));
    }

    [Fact]
    public void WARP维护租约与首次登录冻结状态双向互斥()
    {
        var store = new AccountLoginProxyStateStore();
        var proxy = new ProxyConnectionOptions(
            602,
            "maintenance-warp",
            OutboundProxyKinds.Warp,
            OutboundProxyProtocols.Http,
            "127.0.0.1",
            1080,
            null,
            null,
            null);
        var state = new AccountLoginProxyState(
            1602,
            new AccountProxyBindingInput("existing", proxy.ProxyId),
            "existing",
            new AccountProxyResolution(proxy, false),
            null,
            null,
            null,
            DateTimeOffset.UtcNow);

        var maintenanceLease = store.TryAcquireMaintenance(proxy.ProxyId);
        Assert.NotNull(maintenanceLease);
        Assert.False(store.TryAdd(state, out var error));
        Assert.Contains("正在维护", error);

        maintenanceLease!.Dispose();
        Assert.True(store.TryAdd(state, out error));
        Assert.Null(error);
        Assert.Null(store.TryAcquireMaintenance(proxy.ProxyId));
        Assert.Null(store.TryAcquireUsage(proxy.ProxyId));

        var concurrentState = state with { LoginId = 1603 };
        Assert.False(store.TryAdd(concurrentState, out error));
        Assert.Contains("另一个首次连接流程", error);

        var importFirstStore = new AccountLoginProxyStateStore();
        using var importUsage = importFirstStore.TryAcquireUsage(proxy.ProxyId);
        Assert.NotNull(importUsage);
        Assert.False(importFirstStore.TryAdd(state, out error));
        Assert.Contains("另一个首次连接流程", error);
    }

    [Fact]
    public async Task 已有WARP仍属另一临时创建流程时登录不会冻结该出口()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync(OutboundProxyKinds.Warp);
        var profile = new WarpProfile
        {
            ProfileId = "active-login-owner",
            RequestId = "telegram-panel.internal.login.active-owner",
            ContainerName = "active-login-owner-container",
            ContainerId = "active-login-owner-container-id",
            VolumeName = "active-login-owner-volume",
            HostPort = 42096,
            Status = "active",
            DesiredEnabled = true,
            Proxy = proxy
        };
        fixture.Db.WarpProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();

        using var ownerClaim = fixture.TemporaryWarpClaims.ClaimRequest(profile.RequestId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.PrepareAsync(1604, "existing", proxy.Id));

        Assert.Contains("另一个账号首次连接流程", error.Message);
        Assert.False(fixture.Coordinator.HasState(1604));
    }

    [Fact]
    public async Task 全局WARP仍属另一临时创建流程时登录不会冻结该出口()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync(OutboundProxyKinds.Warp);
        var profile = new WarpProfile
        {
            ProfileId = "active-global-login-owner",
            RequestId = "telegram-panel.internal.login.active-global-owner",
            ContainerName = "active-global-login-owner-container",
            ContainerId = "active-global-login-owner-container-id",
            VolumeName = "active-global-login-owner-volume",
            HostPort = 42097,
            Status = "active",
            DesiredEnabled = true,
            Proxy = proxy
        };
        fixture.Db.WarpProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();
        fixture.Configuration["Telegram:Proxy:Enabled"] = "true";
        fixture.Configuration["Telegram:Proxy:SourceMode"] =
            GlobalTelegramProxyConfiguration.ExistingSourceMode;
        fixture.Configuration["Telegram:Proxy:ProxyId"] = proxy.Id.ToString();

        using var ownerClaim = fixture.TemporaryWarpClaims.ClaimRequest(profile.RequestId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.PrepareAsync(1605, "global", null));

        Assert.Contains("另一个账号首次连接流程", error.Message);
        Assert.False(fixture.Coordinator.HasState(1605));
    }

    [Fact]
    public async Task 显式直连只有在用户明确选择后才会启用账号()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = await fixture.AddInactiveAccountAsync();

        var state = await fixture.Coordinator.PrepareAsync(
            1005,
            "direct",
            null);

        Assert.Null(state.Resolution.Proxy);
        Assert.False(state.Resolution.UseGlobalProxy);
        await fixture.Coordinator.CompleteAsync(1005, account.Id);

        var saved = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.True(saved.IsActive);
        Assert.Null(saved.ProxyId);
        Assert.False(saved.UseGlobalProxy);
    }

    [Fact]
    public async Task 全局策略使用登录开始时的代理快照并保存继承模式()
    {
        await using var fixture = await Fixture.CreateAsync(new Dictionary<string, string?>
        {
            ["Telegram:Proxy:Server"] = "127.0.0.9",
            ["Telegram:Proxy:Port"] = "1099",
            ["Telegram:Proxy:Username"] = "global-user"
        });
        var account = await fixture.AddInactiveAccountAsync();

        var state = await fixture.Coordinator.PrepareAsync(
            1006,
            "global",
            null);

        Assert.Equal("127.0.0.9", state.Resolution.Proxy?.Host);
        Assert.Equal(1099, state.Resolution.Proxy?.Port);
        await fixture.Coordinator.CompleteAsync(1006, account.Id);

        var saved = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.True(saved.IsActive);
        Assert.Null(saved.ProxyId);
        Assert.True(saved.UseGlobalProxy);
    }

    [Fact]
    public async Task 二维码重新生成复用首次创建的WARP且禁止中途切换出口()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = new ProxyConnectionOptions(
            701,
            "login-owned-warp",
            OutboundProxyKinds.Warp,
            OutboundProxyProtocols.Socks5,
            "127.0.0.1",
            1080,
            null,
            null,
            null);
        var frozen = new AccountLoginProxyState(
            1701,
            new AccountProxyBindingInput("existing", proxy.ProxyId),
            "warp_per_account",
            new AccountProxyResolution(proxy, false),
            proxy.ProxyId,
            null,
            null,
            DateTimeOffset.UtcNow);
        Assert.True(fixture.StateStore.TryAdd(frozen));

        using (var lease = fixture.Coordinator.ClaimFrozenState(
                   1701,
                   "warp_per_account",
                   null))
        {
            Assert.True(lease.State.CreatedAtUtc >= frozen.CreatedAtUtc);
            Assert.Same(frozen.Resolution, lease.State.Resolution);
            Assert.True(fixture.Coordinator.HasState(1701));
            Assert.False(fixture.StateStore.TryTakeExpired(
                DateTimeOffset.UtcNow.AddHours(1),
                out _));
        }

        var switchError = Assert.Throws<InvalidOperationException>(() =>
            fixture.Coordinator.ClaimFrozenState(1701, "direct", null));
        Assert.Contains("已经冻结", switchError.Message);

        using (fixture.Coordinator.ClaimFrozenState(
                   1701,
                   "warp_per_account",
                   null))
        {
            Assert.True(fixture.Coordinator.HasState(1701));
        }

        await fixture.Coordinator.AbandonAsync(1701);
        Assert.False(fixture.Coordinator.HasState(1701));
    }

    [Fact]
    public void 生命周期独占期间不能登记同ID新状态且失败后原子恢复()
    {
        var store = new AccountLoginProxyStateStore();
        var original = new AccountLoginProxyState(
            1702,
            new AccountProxyBindingInput("direct"),
            "direct",
            new AccountProxyResolution(null, false),
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
        var replacement = original with { CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };

        Assert.True(store.TryAdd(original));
        Assert.True(store.TryTakeForCompletion(1702, out var claimed));
        Assert.Same(original, claimed);
        Assert.False(store.TryAdd(replacement));
        Assert.True(store.RestoreClaimedState(original));
        Assert.True(store.Contains(1702));

        Assert.True(store.TryClaimExisting(1702, out var restored));
        Assert.Equal(original.LoginId, restored?.LoginId);
        Assert.Same(original.Resolution, restored?.Resolution);
        Assert.False(store.TryClaimExisting(1702, out _));
        store.ReleaseLoginClaim(1702);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(
            SqliteConnection connection,
            AppDbContext db,
            IConfigurationRoot configuration,
            AccountLoginProxyStateStore stateStore,
            AccountLoginProxyCoordinator coordinator,
            StubClientPool clientPool,
            TemporaryWarpClaimStore temporaryWarpClaims)
        {
            _connection = connection;
            Db = db;
            Configuration = configuration;
            StateStore = stateStore;
            Coordinator = coordinator;
            ClientPool = clientPool;
            TemporaryWarpClaims = temporaryWarpClaims;
        }

        public AppDbContext Db { get; }
        public IConfigurationRoot Configuration { get; }
        public AccountLoginProxyStateStore StateStore { get; }
        public AccountLoginProxyCoordinator Coordinator { get; }
        public StubClientPool ClientPool { get; }
        public TemporaryWarpClaimStore TemporaryWarpClaims { get; }

        public static async Task<Fixture> CreateAsync(
            IEnumerable<KeyValuePair<string, string?>>? values = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(
                values ?? Array.Empty<KeyValuePair<string, string?>>());
            var configuration = configurationBuilder.Build();
            var pool = new StubClientPool();
            var probe = new ProxyEgressProbeService();
            var stateStore = new AccountLoginProxyStateStore();
            var temporaryWarpClaims = new TemporaryWarpClaimStore();
            var warpManager = new WarpContainerManager(
                db,
                configuration,
                probe,
                NullLogger<WarpContainerManager>.Instance);
            var proxyManagement = new ProxyManagementService(
                db,
                pool,
                probe,
                warpManager,
                NullLogger<ProxyManagementService>.Instance,
                configuration,
                temporaryWarpClaims,
                stateStore);
            var accountManagement = new AccountManagementService(
                new AccountRepository(db),
                new ChannelRepository(db),
                new GroupRepository(db),
                pool,
                configuration,
                NullLogger<AccountManagementService>.Instance,
                proxyManagement,
                new SessionPathResolver(configuration));
            var accountService = new AccountService(
                pool,
                NullLogger<AccountService>.Instance,
                configuration);
            var coordinator = new AccountLoginProxyCoordinator(
                stateStore,
                proxyManagement,
                accountManagement,
                accountService,
                temporaryWarpClaims,
                configuration,
                NullLogger<AccountLoginProxyCoordinator>.Instance);

            return new Fixture(
                connection,
                db,
                configuration,
                stateStore,
                coordinator,
                pool,
                temporaryWarpClaims);
        }

        public async Task<OutboundProxy> AddProxyAsync(string kind)
        {
            var proxy = new OutboundProxy
            {
                Name = $"login-{kind}",
                Kind = kind,
                Protocol = OutboundProxyProtocols.Socks5,
                Host = "127.0.0.1",
                Port = 1080,
                IsEnabled = true,
                TestStatus = "unknown",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            Db.OutboundProxies.Add(proxy);
            await Db.SaveChangesAsync();
            return proxy;
        }

        public async Task<OutboundProxy> AddActiveWarpProxyAsync(string name)
        {
            var proxy = await AddProxyAsync(OutboundProxyKinds.Warp);
            proxy.Name = name;
            proxy.WarpProfile = new WarpProfile
            {
                ProfileId = $"profile-{name}",
                RequestId = $"request-{name}",
                ContainerName = $"container-{name}",
                ContainerId = $"container-id-{name}",
                VolumeName = $"volume-{name}",
                HostPort = 42000 + proxy.Id,
                Status = "active",
                DesiredEnabled = true,
                Proxy = proxy
            };
            await Db.SaveChangesAsync();
            return proxy;
        }

        public async Task<Account> AddInactiveAccountAsync()
        {
            var account = new Account
            {
                Phone = "8613800000000",
                SessionPath = "sessions/8613800000000.session",
                ApiId = 12345,
                ApiHash = "0123456789abcdef0123456789abcdef",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            };
            Db.Accounts.Add(account);
            await Db.SaveChangesAsync();
            return account;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubClientPool : ITelegramClientPool
    {
        public int ActiveClientCount => 0;
        public List<int> RemovedAccountIds { get; } = new();
        public List<int> StrictlyRemovedAccountIds { get; } = new();
        public Exception? StrictRemoveError { get; set; }

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
        public Task RemoveClientAsync(int accountId)
        {
            RemovedAccountIds.Add(accountId);
            return Task.CompletedTask;
        }

        public Task RemoveClientStrictAsync(int accountId)
        {
            StrictlyRemovedAccountIds.Add(accountId);
            return StrictRemoveError == null
                ? Task.CompletedTask
                : Task.FromException(StrictRemoveError);
        }

        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }

    private sealed class CountingProxyResolver : IAccountProxyResolver
    {
        public int CallCount { get; private set; }

        public Task<AccountProxyResolution> ResolveAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("显式登录路由不应查询账号代理");
        }
    }
}
