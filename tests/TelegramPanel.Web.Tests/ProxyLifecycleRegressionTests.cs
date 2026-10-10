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
/// 代理生命周期回归测试，覆盖账号删除、凭据清理和客户端池竞态。
/// </summary>
public sealed class ProxyLifecycleRegressionTests
{
    [Fact]
    public async Task 旧容器记录不能连接检测启用或重新绑定且未使用记录可删除()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var legacy = NewWarpProxy("retired", 42080);
        db.OutboundProxies.Add(legacy);
        await db.SaveChangesAsync();
        var service = CreateProxyService(db, new RecordingClientPool());
        Assert.Throws<InvalidOperationException>(() => AccountProxyResolver.BuildConnectionOptions(legacy, "tg_account_42"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TestAsync(legacy.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(legacy.Id,
            NewInput("retired", "warp", "http", legacy.Host, legacy.Port)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateBindingInputAsync(new("existing", legacy.Id)));
        var applied = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteGlobalProxyChangeAsync(
            true, "existing", legacy.Id, _ => { applied = true; return Task.CompletedTask; }));
        Assert.False(applied);
        Assert.Equal("active", (await db.WarpProfiles.AsNoTracking().SingleAsync()).Status);
        await service.DeleteAsync(legacy.Id);
        Assert.Empty(await db.OutboundProxies.ToListAsync());
        Assert.Empty(await db.WarpProfiles.ToListAsync());
    }

    [Fact]
    public async Task 直连与全局代理设置会持久化为不同路由()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var account = NewAccount(NewProxy("routing-mode"));
        account.Proxy = null;
        account.IsActive = true;
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var service = CreateProxyService(
            db,
            new RecordingClientPool(),
            GlobalProxyConfiguration());

        await service.BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("direct"));
        Assert.False((await db.Accounts.AsNoTracking().SingleAsync()).UseGlobalProxy);
        Assert.True((await db.Accounts.AsNoTracking().SingleAsync()).IsActive);

        await service.BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("global"));
        Assert.True((await db.Accounts.AsNoTracking().SingleAsync()).UseGlobalProxy);
        Assert.True((await db.Accounts.AsNoTracking().SingleAsync()).IsActive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task 缺少代理策略时拒绝且不会静默切换为直连(string? strategy)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("missing-strategy-guard");
        var account = NewAccount(proxy);
        account.IsActive = true;
        account.UseGlobalProxy = false;
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var service = CreateProxyService(db, new RecordingClientPool());

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.BindAccountsAsync(
                new[] { account.Id },
                new AccountProxyBindingInput(strategy!)));

        Assert.Contains("必须显式选择 direct", error.Message);
        var unchanged = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(proxy.Id, unchanged.ProxyId);
        Assert.False(unchanged.UseGlobalProxy);
        Assert.True(unchanged.IsActive);
    }

    [Fact]
    public async Task 旧客户端无法确认断开时不会提交新代理绑定()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var oldProxy = NewProxy("strict-switch-old");
        var targetProxy = NewProxy("strict-switch-target");
        var account = NewAccount(oldProxy);
        account.UseGlobalProxy = false;
        account.IsActive = true;
        db.AddRange(oldProxy, targetProxy, account);
        await db.SaveChangesAsync();

        var pool = new RecordingClientPool
        {
            StrictRemovalError = new InvalidOperationException("旧客户端仍可能在线")
        };
        var service = CreateProxyService(db, pool);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BindAccountsAsync(
                new[] { account.Id },
                new AccountProxyBindingInput("existing", targetProxy.Id)));

        Assert.Contains("旧客户端仍可能在线", error.Message);
        var unchanged = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(oldProxy.Id, unchanged.ProxyId);
        Assert.False(unchanged.UseGlobalProxy);
        Assert.False(unchanged.IsActive);
    }

    [Fact]
    public async Task 显式直连不会回退全局代理且停用代理会阻止降级()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Accounts.Add(new Account
            {
                Phone = "8613800000999",
                UserId = 999,
                SessionPath = "sessions/direct.session",
                ApiId = 1,
                ApiHash = "hash",
                UseGlobalProxy = false
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var resolver = new AccountProxyResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build());
        var direct = await resolver.ResolveAsync(1);

        Assert.Null(direct.Proxy);
        Assert.False(direct.UseGlobalProxy);

        await using (var db = new AppDbContext(options))
        {
            var account = await db.Accounts.SingleAsync();
            var disabledProxy = NewProxy("disabled");
            disabledProxy.IsEnabled = false;
            account.Proxy = disabledProxy;
            account.UseGlobalProxy = false;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(1));
    }

    [Fact]
    public async Task 全局代理解析会生成显式快照且缺失配置时闭锁()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Accounts.Add(new Account
            {
                Phone = "8613800000998",
                UserId = 998,
                SessionPath = "sessions/global.session",
                ApiId = 1,
                ApiHash = "hash",
                UseGlobalProxy = true
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuredResolver = new AccountProxyResolver(
            scopeFactory,
            GlobalProxyConfiguration());
        var resolved = await configuredResolver.ResolveAsync(1);

        Assert.NotNull(resolved.Proxy);
        Assert.Equal("127.0.0.9", resolved.Proxy!.Host);
        Assert.Equal(19080, resolved.Proxy.Port);
        Assert.False(resolved.UseGlobalProxy);

        var invalidIdError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            configuredResolver.ResolveAsync(0));
        Assert.Contains("未知账号", invalidIdError.Message);
        var missingAccountError = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            configuredResolver.ResolveAsync(999));
        Assert.Contains("不存在", missingAccountError.Message);


        var missingResolver = new AccountProxyResolver(
            scopeFactory,
            new ConfigurationBuilder().Build());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            missingResolver.ResolveAsync(1));
        Assert.Contains("阻止降级为直连", error.Message);
    }

    [Fact]
    public async Task 未配置全局代理时绑定和出口检测都不会退回面板直连()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var account = NewAccount(NewProxy("global-fail-closed"));
        account.Proxy = null;
        account.UseGlobalProxy = false;
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var service = CreateProxyService(db, new RecordingClientPool());

        var bindError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BindAccountsAsync(
                new[] { account.Id },
                new AccountProxyBindingInput("global")));
        Assert.Contains("阻止降级为直连", bindError.Message);
        var unchanged = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(unchanged.UseGlobalProxy);
        Assert.Null(unchanged.ProxyId);

        account.UseGlobalProxy = true;
        await db.SaveChangesAsync();
        var probeError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProbeAccountAsync(account.Id));
        Assert.Contains("阻止降级为直连", probeError.Message);
    }

    [Fact]
    public async Task 删除账号会先解绑代理并释放客户端()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("account-delete");
        var account = NewAccount(proxy);
        db.OutboundProxies.Add(proxy);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var pool = new RecordingClientPool();
        var proxyService = CreateProxyService(db, pool);
        var accountService = new AccountManagementService(
            new AccountRepository(db),
            new ChannelRepository(db),
            new GroupRepository(db),
            pool,
            new ConfigurationBuilder().Build(),
            NullLogger<AccountManagementService>.Instance,
            proxyService,
            new SessionPathResolver(new ConfigurationBuilder().Build()));

        await accountService.DeleteAccountAsync(account.Id);

        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Single(await db.OutboundProxies.ToListAsync());
        Assert.Equal(new[] { account.Id }, pool.RemovedAccountIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 删除和废号清理在旧客户端无法断开时保留绑定并停用账号(bool purge)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy($"strict-account-delete-{purge}");
        var account = NewAccount(proxy);
        account.IsActive = true;
        db.OutboundProxies.Add(proxy);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var pool = new RecordingClientPool
        {
            StrictRemovalError = new InvalidOperationException("旧客户端仍在线")
        };
        var proxyService = CreateProxyService(db, pool);
        var accountService = new AccountManagementService(
            new AccountRepository(db),
            new ChannelRepository(db),
            new GroupRepository(db),
            pool,
            new ConfigurationBuilder().Build(),
            NullLogger<AccountManagementService>.Instance,
            proxyService,
            new SessionPathResolver(new ConfigurationBuilder().Build()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => purge
            ? accountService.PurgeAccountAsync(account.Id)
            : accountService.DeleteAccountAsync(account.Id));

        Assert.Contains("旧客户端仍在线", error.Message);
        var preserved = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(proxy.Id, preserved.ProxyId);
        Assert.False(preserved.IsActive);
        Assert.True(await db.OutboundProxies.AnyAsync(x => x.Id == proxy.Id));
    }



    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 删除和废号清理会持有代理变更锁直到账号记录删除(bool purge)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var oldProxy = NewProxy($"delete-race-old-{purge}");
        var targetProxy = NewProxy($"delete-race-target-{purge}");
        targetProxy.Port = 8090;
        var account = NewAccount(oldProxy);
        account.SessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-delete-race-{Guid.NewGuid():N}.session");
        db.AddRange(account, targetProxy);
        await db.SaveChangesAsync();

        var repository = new BlockingDeleteAccountRepository(db);
        var pool = new RecordingClientPool();
        var proxyService = CreateProxyService(db, pool);
        var accountService = new AccountManagementService(
            repository,
            new ChannelRepository(db),
            new GroupRepository(db),
            pool,
            new ConfigurationBuilder().Build(),
            NullLogger<AccountManagementService>.Instance,
            proxyService,
            new SessionPathResolver(new ConfigurationBuilder().Build()));

        var deleteTask = purge
            ? accountService.PurgeAccountAsync(account.Id)
            : accountService.DeleteAccountAsync(account.Id);
        await repository.DeleteStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var bindTask = proxyService.BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("existing", targetProxy.Id));
        try
        {
            Assert.False(bindTask.IsCompleted);
        }
        finally
        {
            repository.AllowDelete();
        }

        await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));
        var bindResult = await bindTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(await db.Accounts.AsNoTracking().ToListAsync());
        Assert.Equal(0, bindResult.Success);
        Assert.Equal(1, bindResult.Failed);
        Assert.Equal("账号不存在", bindResult.Items.Single().Summary);
    }

    [Fact]
    public async Task 代理切换到非Resin和非MTProto会清除旧凭据()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("credential-clear");
        proxy.Kind = OutboundProxyKinds.Resin;
        proxy.ResinAdminToken = "resin-token";
        proxy.Secret = "stale-secret";
        proxy.TestStatus = "ok";
        proxy.EgressIp = "203.0.113.10";
        proxy.EgressCountry = "US";
        proxy.LastLatencyMs = 42;
        proxy.LastTestedAtUtc = DateTime.UtcNow;
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();

        var service = CreateProxyService(db, new RecordingClientPool());
        await service.UpdateAsync(
            proxy.Id,
            NewInput(
                name: proxy.Name,
                kind: OutboundProxyKinds.Manual,
                protocol: OutboundProxyProtocols.Http,
                host: proxy.Host,
                port: proxy.Port));

        var updated = await db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Null(updated.ResinAdminToken);
        Assert.Null(updated.Secret);
        Assert.Equal("unknown", updated.TestStatus);
        Assert.Null(updated.EgressIp);
        Assert.Null(updated.EgressCountry);
        Assert.Null(updated.LastLatencyMs);
        Assert.Null(updated.LastTestedAtUtc);
    }

    [Fact]
    public async Task 普通代理不能通过编辑伪装成受管WARP()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("managed-warp-update-guard");
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();
        var service = CreateProxyService(db, new RecordingClientPool());

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(
                proxy.Id,
                NewInput(
                    name: proxy.Name,
                    kind: OutboundProxyKinds.Warp,
                    protocol: OutboundProxyProtocols.Http,
                    host: proxy.Host,
                    port: proxy.Port)));

        Assert.Contains("一键创建", error.Message);
        var unchanged = await db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Equal(OutboundProxyKinds.Manual, unchanged.Kind);
        Assert.Empty(await db.WarpProfiles.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 代理连接参数编辑前旧客户端无法断开则保留旧配置()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("strict-proxy-update");
        var originalHost = proxy.Host;
        var account = NewAccount(proxy);
        account.IsActive = true;
        db.OutboundProxies.Add(proxy);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var pool = new RecordingClientPool
        {
            StrictRemovalError = new InvalidOperationException("旧客户端仍在线")
        };
        var service = CreateProxyService(db, pool);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                proxy.Id,
                NewInput(
                    name: proxy.Name,
                    kind: OutboundProxyKinds.Manual,
                    protocol: OutboundProxyProtocols.Http,
                    host: "new-proxy.example",
                    port: proxy.Port)));

        Assert.Contains("旧客户端仍在线", error.Message);
        var unchangedProxy = await db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Equal(originalHost, unchangedProxy.Host);
        var unchangedAccount = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(proxy.Id, unchangedAccount.ProxyId);
        Assert.False(unchangedAccount.IsActive);
    }
    [Fact]
    public async Task 代理连接参数编辑时先停用账号并在新配置提交时恢复()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("proxy-update-quiesce");
        var originalHost = proxy.Host;
        var account = NewAccount(proxy);
        account.IsActive = true;
        db.OutboundProxies.Add(proxy);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var strictRemovalObserved = false;
        var pool = new RecordingClientPool
        {
            OnStrictRemoveAsync = async accountId =>
            {
                var storedAccount = await db.Accounts
                    .AsNoTracking()
                    .SingleAsync(x => x.Id == accountId);
                var storedProxy = await db.OutboundProxies
                    .AsNoTracking()
                    .SingleAsync(x => x.Id == proxy.Id);
                Assert.False(storedAccount.IsActive);
                Assert.Equal(originalHost, storedProxy.Host);
                strictRemovalObserved = true;
            }
        };
        var service = CreateProxyService(db, pool);

        await service.UpdateAsync(
            proxy.Id,
            NewInput(
                name: proxy.Name,
                kind: OutboundProxyKinds.Manual,
                protocol: OutboundProxyProtocols.Http,
                host: "new-proxy.example",
                port: proxy.Port));

        Assert.True(strictRemovalObserved);
        var updatedProxy = await db.OutboundProxies.AsNoTracking().SingleAsync();
        var restoredAccount = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal("new-proxy.example", updatedProxy.Host);
        Assert.True(restoredAccount.IsActive);
    }


    [Fact]
    public async Task 停用的账号代理出口检测不会降级为面板直连()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = NewProxy("disabled-egress");
        proxy.IsEnabled = false;
        var account = NewAccount(proxy);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var service = CreateProxyService(db, new RecordingClientPool());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProbeAccountAsync(account.Id));

        Assert.Contains("已停用", error.Message);
    }

    [Fact]
    public async Task 继承全局代理的账号出口检测使用全局MTProxy配置()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var account = NewAccount(NewProxy("global-egress"));
        account.Proxy = null;
        account.UseGlobalProxy = true;
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Server"] = "127.0.0.1",
                ["Telegram:Proxy:Port"] = "443",
                ["Telegram:Proxy:Secret"] = "abcdef"
            })
            .Build();
        var service = CreateProxyService(db, new RecordingClientPool(), configuration);

        var result = await service.ProbeAccountAsync(account.Id);

        Assert.False(result.Success);
        Assert.Contains("MTProxy", result.Error);
    }

    [Fact]
    public async Task 编辑代理时密码留空不会误判连接变化或断开客户端()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("blank-password");
        proxy.Password = "existing-password";
        var account = NewAccount(proxy);
        db.OutboundProxies.Add(proxy);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var pool = new RecordingClientPool
        {
            StrictRemovalError = new InvalidOperationException("不应断开客户端")
        };
        var service = CreateProxyService(db, pool);

        await service.UpdateAsync(
            proxy.Id,
            NewInput(
                name: proxy.Name,
                kind: proxy.Kind,
                protocol: proxy.Protocol,
                host: proxy.Host,
                port: proxy.Port) with
            {
                Password = "   "
            });

        Assert.Equal(
            "existing-password",
            (await db.OutboundProxies.AsNoTracking().SingleAsync()).Password);
        Assert.Empty(pool.RemovedAccountIds);
        Assert.True((await db.Accounts.AsNoTracking().SingleAsync()).IsActive);
    }

    [Fact]
    public async Task 留空时保留适用凭据但切出协议后清除Secret()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = NewProxy("credential-preserve");
        proxy.Protocol = OutboundProxyProtocols.MtProto;
        proxy.Secret = "mt-secret";
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();

        var service = CreateProxyService(db, new RecordingClientPool());
        await service.UpdateAsync(
            proxy.Id,
            NewInput(
                name: proxy.Name,
                kind: OutboundProxyKinds.Manual,
                protocol: OutboundProxyProtocols.MtProto,
                host: proxy.Host,
                port: proxy.Port,
                secret: string.Empty));
        Assert.Equal("mt-secret", (await db.OutboundProxies.AsNoTracking().SingleAsync()).Secret);

        await service.UpdateAsync(
            proxy.Id,
            NewInput(
                name: proxy.Name,
                kind: OutboundProxyKinds.Manual,
                protocol: OutboundProxyProtocols.Http,
                host: proxy.Host,
                port: proxy.Port,
                secret: string.Empty));
        Assert.Null((await db.OutboundProxies.AsNoTracking().SingleAsync()).Secret);
    }


    [Fact]
    public async Task 客户端创建期间移除会等待账号锁并清理新客户端()
    {
        var resolver = new BlockingProxyResolver();
        var pool = new TelegramClientPool(
            new ConfigurationBuilder().Build(),
            NullLogger<TelegramClientPool>.Instance,
            new TelegramAccountUpdateHub(),
            resolver,
            new SessionPathResolver(new ConfigurationBuilder().Build()));
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-pool-{Guid.NewGuid():N}.session");

        try
        {
            var createTask = pool.GetOrCreateClientAsync(
                9001,
                12345,
                "0123456789abcdef0123456789abcdef",
                sessionPath,
                sessionKey: "0123456789abcdef0123456789abcdef",
                phoneNumber: "8613800000000");
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var removeTask = pool.RemoveClientAsync(9001);
            Assert.False(removeTask.IsCompleted);
            resolver.Release();

            try
            {
                await createTask;
            }
            catch
            {
                // 不同 WTelegram 版本可能在无有效 session 时拒绝构造，
                // 这里只验证移除不会与创建并发交叉。
            }
            await removeTask;
            Assert.Null(pool.GetClient(9001));
        }
        finally
        {
            pool.Dispose();
            TryDelete(sessionPath);
        }
    }

    [Fact]
    public async Task 全池失效会拒绝并发创建的旧出口客户端写回()
    {
        var resolver = new BlockingProxyResolver();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "true",
                ["Telegram:Proxy:Protocol"] = "socks5",
                ["Telegram:Proxy:Server"] = "127.0.0.1",
                ["Telegram:Proxy:Port"] = "1080"
            })
            .Build();
        using var pool = new TelegramClientPool(
            configuration,
            NullLogger<TelegramClientPool>.Instance,
            new TelegramAccountUpdateHub(),
            resolver,
            new SessionPathResolver(configuration));
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-pool-generation-{Guid.NewGuid():N}.session");

        try
        {
            var createTask = pool.GetOrCreateClientAsync(
                9004,
                12345,
                "0123456789abcdef0123456789abcdef",
                sessionPath,
                sessionKey: "0123456789abcdef0123456789abcdef",
                phoneNumber: "8613800000004");
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await pool.RemoveAllClientsAsync();
            resolver.Release();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => createTask);
            Assert.Contains("旧出口", error.Message);
            Assert.Null(pool.GetClient(9004));
        }
        finally
        {
            resolver.Release();
            TryDelete(sessionPath);
        }
    }

    [Fact]
    public async Task 客户端池收到全局模式但配置缺失时会在构造客户端前闭锁()
    {
        var resolver = new FixedProxyResolver(new AccountProxyResolution(null, true));
        using var pool = new TelegramClientPool(
            new ConfigurationBuilder().Build(),
            NullLogger<TelegramClientPool>.Instance,
            new TelegramAccountUpdateHub(),
            resolver,
            new SessionPathResolver(new ConfigurationBuilder().Build()));
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-global-fail-closed-{Guid.NewGuid():N}.session");

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pool.GetOrCreateClientAsync(
                    9003,
                    12345,
                    "0123456789abcdef0123456789abcdef",
                    sessionPath,
                    sessionKey: "0123456789abcdef0123456789abcdef",
                    phoneNumber: "8613800000002"));

            Assert.Contains("阻止降级为直连", error.Message);
            Assert.Null(pool.GetClient(9003));
            Assert.False(File.Exists(sessionPath));
        }
        finally
        {
            TryDelete(sessionPath);
        }
    }

    [Fact]
    public async Task 客户端创建期间关闭池不会把新客户端写回()
    {
        var resolver = new BlockingProxyResolver();
        var pool = new TelegramClientPool(
            new ConfigurationBuilder().Build(),
            NullLogger<TelegramClientPool>.Instance,
            new TelegramAccountUpdateHub(),
            resolver,
            new SessionPathResolver(new ConfigurationBuilder().Build()));
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-dispose-{Guid.NewGuid():N}.session");

        try
        {
            var createTask = pool.GetOrCreateClientAsync(
                9002,
                12345,
                "0123456789abcdef0123456789abcdef",
                sessionPath,
                sessionKey: "0123456789abcdef0123456789abcdef",
                phoneNumber: "8613800000001");
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            pool.Dispose();
            resolver.Release();
            try
            {
                await createTask;
            }
            catch
            {
                // 关闭期间创建必须失败，具体异常取决于 WTelegram 构造阶段。
            }

            Assert.Null(pool.GetClient(9002));
        }
        finally
        {
            pool.Dispose();
            TryDelete(sessionPath);
        }
    }

    private static ProxyManagementService CreateProxyService(
        AppDbContext db,
        RecordingClientPool pool,
        IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().Build();
        var probe = new ProxyEgressProbeService();
        return new ProxyManagementService(
            db,
            pool,
            probe,
            NullLogger<ProxyManagementService>.Instance,
            configuration);
    }

    private static IConfiguration GlobalProxyConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Server"] = "127.0.0.9",
                ["Telegram:Proxy:Port"] = "19080",
                ["Telegram:Proxy:Username"] = "global-user",
                ["Telegram:Proxy:Password"] = "global-password"
            })
            .Build();

    private static OutboundProxy NewProxy(string suffix) => new()
    {
        Name = $"proxy-{suffix}",
        Kind = OutboundProxyKinds.Manual,
        Protocol = OutboundProxyProtocols.Http,
        Host = "127.0.0.1",
        Port = 8080,
        IsEnabled = true,
        TestStatus = "unknown"
    };

    private static OutboundProxy NewWarpProxy(string suffix, int hostPort)
    {
        var proxy = NewProxy(suffix);
        proxy.Kind = OutboundProxyKinds.Warp;
        proxy.WarpProfile = new WarpProfile
        {
            ProfileId = $"profile-{suffix}",
            ContainerId = $"container-id-{suffix}",
            ContainerName = $"container-{suffix}",
            VolumeName = $"volume-{suffix}",
            HostPort = hostPort,
            Status = "active",
            DesiredEnabled = true,
            Proxy = proxy
        };
        return proxy;
    }

    private static Account NewAccount(OutboundProxy proxy) => new()
    {
        Phone = "8613800000000" + Random.Shared.Next(100, 999),
        UserId = Random.Shared.NextInt64(1000, 9999),
        SessionPath = "sessions/lifecycle.session",
        ApiId = 1,
        ApiHash = "hash",
        Proxy = proxy
    };

    private static OutboundProxyInput NewInput(
        string name,
        string kind,
        string protocol,
        string host,
        int port,
        string? secret = null,
        bool isEnabled = true) =>
        new(
            name,
            kind,
            protocol,
            host,
            port,
            Username: null,
            Password: null,
            Secret: secret,
            ResinPlatform: null,
            ResinAdminUrl: null,
            ResinAdminToken: null,
            IsEnabled: isEnabled,
            TestAfterSave: false);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 测试清理失败不影响断言结果。
        }
    }

    private sealed class RecordingClientPool : ITelegramClientPool
    {
        private readonly List<int> _removed = new();

        public IReadOnlyList<int> RemovedAccountIds => _removed;
        public Exception? StrictRemovalError { get; init; }
        public Func<int, Task>? OnStrictRemoveAsync { get; init; }
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

        public Task RemoveClientAsync(int accountId)
        {
            lock (_removed)
                _removed.Add(accountId);
            return Task.CompletedTask;
        }

        public async Task RemoveClientStrictAsync(int accountId)
        {
            if (OnStrictRemoveAsync != null)
                await OnStrictRemoveAsync(accountId);
            if (StrictRemovalError != null)
                throw StrictRemovalError;
            await RemoveClientAsync(accountId);
        }

        public Task RemoveAllClientsAsync() => Task.CompletedTask;

        public bool IsClientConnected(int accountId) => false;
    }

    private sealed class BlockingDeleteAccountRepository : AccountRepository
    {
        private readonly TaskCompletionSource _deleteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDelete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingDeleteAccountRepository(AppDbContext context) : base(context)
        {
        }

        public Task DeleteStarted => _deleteStarted.Task;

        public void AllowDelete() => _allowDelete.TrySetResult();

        public override async Task DeleteAsync(Account entity)
        {
            _deleteStarted.TrySetResult();
            await _allowDelete.Task;
            await base.DeleteAsync(entity);
        }
    }

    private sealed class BlockingProxyResolver : IAccountProxyResolver
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AccountProxyResolution> ResolveAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();

        private async Task<AccountProxyResolution> WaitAsync(CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            return new AccountProxyResolution(null, true);
        }
    }

    private sealed class FixedProxyResolver : IAccountProxyResolver
    {
        private readonly AccountProxyResolution _resolution;

        public FixedProxyResolver(AccountProxyResolution resolution)
        {
            _resolution = resolution;
        }

        public Task<AccountProxyResolution> ResolveAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_resolution);
        }
    }
}
