using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

public sealed class ResinBindingRegressionTests
{
    [Fact]
    public async Task Resin平台名称遵循官方V1规则并允许合法符号()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = CreateProxyService(db);

        var proxy = await service.CreateAsync(NewResinInput("Telegram+Pool(1)"));

        Assert.Equal("Telegram+Pool(1)", proxy.ResinPlatform);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("bad.name")]
    [InlineData("bad name")]
    public async Task Resin平台名称拒绝官方V1保留名称和分隔字符(string platform)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = CreateProxyService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(NewResinInput(platform)));
    }

    [Fact]
    public async Task Resin的Socks5身份在保存时校验UTF8字节上限()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = CreateProxyService(db);
        var longPlatform = new string('中', 74);
        var input = NewResinInput(longPlatform) with
        {
            Protocol = OutboundProxyProtocols.Socks5
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(input));

        Assert.Contains("255 字节", error.Message);
    }

    [Fact]
    public async Task Resin的Socks5ProxyToken在保存时校验UTF8字节上限()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = CreateProxyService(db);
        var input = NewResinInput("Default") with
        {
            Protocol = OutboundProxyProtocols.Socks5,
            Password = new string('密', 86)
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(input));

        Assert.Contains("255 字节", error.Message);
    }

    [Fact]
    public async Task 重复绑定同一Resin代理不会释放当前粘性Lease()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = new OutboundProxy
        {
            Name = "resin-sticky",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000100",
            UserId = 10001,
            SessionPath = "sessions/resin-sticky.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = proxy
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var service = CreateProxyService(db);
        var result = await service.BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("existing", proxy.Id));

        Assert.Equal(1, result.Success);
        Assert.Equal(proxy.Id, result.Items.Single().ProxyId);
        Assert.Equal(proxy.Id, await db.Accounts
            .Where(x => x.Id == account.Id)
            .Select(x => x.ProxyId)
            .SingleAsync());
        Assert.Empty(resin.Requests);
    }

    [Fact]
    public async Task Resin释放失败时代理切换保留原绑定且允许重试()
    {
        await using var resin = new ResinControlPlaneStub(HttpStatusCode.InternalServerError);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var resinProxy = new OutboundProxy
        {
            Name = "resin-retry",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var targetProxy = new OutboundProxy
        {
            Name = "manual-target",
            Kind = OutboundProxyKinds.Manual,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8081,
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000101",
            UserId = 10002,
            SessionPath = "sessions/resin-retry.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = resinProxy,
            UseGlobalProxy = true
        };
        db.AddRange(account, targetProxy);
        await db.SaveChangesAsync();

        var service = CreateProxyService(db);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BindAccountsAsync(
                new[] { account.Id },
                new AccountProxyBindingInput("existing", targetProxy.Id)));

        Assert.Contains("Resin Lease", error.Message);
        var unchanged = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(resinProxy.Id, unchanged.ProxyId);
        Assert.True(unchanged.UseGlobalProxy);

        resin.LeaseDeleteStatus = HttpStatusCode.OK;
        var result = await service.BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("existing", targetProxy.Id));

        Assert.Equal(1, result.Success);
        var switched = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(targetProxy.Id, switched.ProxyId);
        Assert.False(switched.UseGlobalProxy);
        Assert.Equal(2, resin.Requests.Count(x =>
            x.StartsWith(
                "DELETE /api/v1/platforms/default-id/leases/tg_account_",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Resin释放失败时删除账号会保留账号和代理绑定()
    {
        await using var resin = new ResinControlPlaneStub(HttpStatusCode.ServiceUnavailable);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var proxy = new OutboundProxy
        {
            Name = "resin-delete-failure",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000102",
            UserId = 10003,
            SessionPath = "sessions/resin-delete-failure.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = proxy,
            UseGlobalProxy = true
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var pool = new NoopClientPool();
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

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accountService.DeleteAccountAsync(account.Id));

        Assert.Contains("Resin Lease", error.Message);
        var preserved = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(proxy.Id, preserved.ProxyId);
        Assert.True(preserved.UseGlobalProxy);
        Assert.True(await db.OutboundProxies.AnyAsync(x => x.Id == proxy.Id));
    }

    [Fact]
    public async Task Resin释放失败时废号清理不会先删除Session文件()
    {
        await using var resin = new ResinControlPlaneStub(HttpStatusCode.ServiceUnavailable);
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"resin-purge-{Guid.NewGuid():N}.session");
        await File.WriteAllTextAsync(sessionPath, "session-data");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var proxy = new OutboundProxy
            {
                Name = "resin-purge-failure",
                Kind = OutboundProxyKinds.Resin,
                Protocol = OutboundProxyProtocols.Http,
                Host = "127.0.0.1",
                Port = 8080,
                ResinPlatform = "Default",
                ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
                ResinAdminToken = "admin-token",
                IsEnabled = true,
                TestStatus = "unknown"
            };
            var account = new Account
            {
                Phone = "8613800000103",
                UserId = 10004,
                SessionPath = sessionPath,
                ApiId = 1,
                ApiHash = "hash",
                Proxy = proxy
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            var pool = new NoopClientPool();
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

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                accountService.PurgeAccountAsync(account.Id));

            Assert.True(File.Exists(sessionPath));
            Assert.Equal(proxy.Id, (await db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
        }
        finally
        {
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
        }
    }

    [Fact]
    public async Task Resin平台已删除时释放Lease按幂等成功处理()
    {
        await using var resin = new ResinControlPlaneStub { IncludePlatform = false };
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-missing-platform",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000104",
            UserId = 10005,
            SessionPath = "sessions/resin-missing-platform.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = proxy
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var result = await CreateProxyService(db).BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("direct"));

        Assert.Equal(1, result.Success);
        Assert.Null((await db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
        Assert.DoesNotContain(
            resin.Requests,
            x => x.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resin平台查询会翻页定位目标并释放Lease()
    {
        await using var resin = new ResinControlPlaneStub
        {
            TargetPlatformOnSecondPage = true
        };
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-platform-page",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000105",
            UserId = 10006,
            SessionPath = "sessions/resin-platform-page.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = proxy
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var result = await CreateProxyService(db).BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("direct"));

        Assert.Equal(1, result.Success);
        Assert.Contains(
            resin.Requests,
            request => request.Contains("offset=100", StringComparison.Ordinal));
        Assert.Contains(
            $"DELETE /api/v1/platforms/default-id/leases/tg_account_{account.Id} HTTP/1.1",
            resin.Requests);
    }

    [Fact]
    public async Task Resin管理地址路径前缀会保留到平台查询和Lease删除()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-admin-prefix",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = new Uri(resin.BaseAddress, "resin-admin").AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000108",
            UserId = 10008,
            SessionPath = "sessions/resin-admin-prefix.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = proxy
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var result = await CreateProxyService(db).BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("direct"));

        Assert.Equal(1, result.Success);
        Assert.Contains(
            resin.Requests,
            request => request.StartsWith(
                "GET /resin-admin/api/v1/platforms?",
                StringComparison.Ordinal));
        Assert.Contains(
            $"DELETE /resin-admin/api/v1/platforms/default-id/leases/"
            + $"tg_account_{account.Id} HTTP/1.1",
            resin.Requests);
    }

    [Fact]
    public async Task Resin代理删除后导入临时Lease仍使用创建时快照回收()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-import",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();

        var snapshot = new ResinLeaseControlSnapshot(
            proxy.Id,
            proxy.ResinAdminUrl,
            proxy.ResinAdminToken,
            proxy.ResinPlatform);
        db.OutboundProxies.Remove(proxy);
        await db.SaveChangesAsync();

        await CreateProxyService(db).ReleaseImportResinLeaseBestEffortAsync(
            snapshot,
            "tg_import_0123456789abcdef");

        Assert.Contains(
            "DELETE /api/v1/platforms/default-id/leases/tg_import_0123456789abcdef HTTP/1.1",
            resin.Requests);
    }

    [Fact]
    public async Task Resin代理出口检测后会回收探测Lease()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-probe",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 1,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();

        await CreateProxyService(db).TestAsync(proxy.Id);

        Assert.Contains(
            $"DELETE /api/v1/platforms/default-id/leases/telegram_panel_probe_{proxy.Id} HTTP/1.1",
            resin.Requests);
    }

    [Theory]
    [InlineData("proxy-token", "proxy-token")]
    [InlineData(null, "telegram-panel")]
    public async Task Resin导入临时Lease会通过数据面Token继承给稳定账号(
        string? proxyToken,
        string expectedTokenPath)
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var service = CreateProxyService(db);
        var proxyConnection = new ProxyConnectionOptions(
            7,
            "resin-import-inherit",
            OutboundProxyKinds.Resin,
            OutboundProxyProtocols.Http,
            resin.BaseAddress.Host,
            resin.BaseAddress.Port,
            "Default.tg_import_parent",
            proxyToken,
            null);

        var inherited = await service.InheritImportResinLeaseBestEffortAsync(
            proxyConnection,
            "Default",
            "tg_import_parent",
            "tg_account_42");

        Assert.True(inherited);
        Assert.Contains(
            $"POST /{expectedTokenPath}/api/v1/Default/actions/inherit-lease HTTP/1.1",
            resin.Requests);
    }

    [Fact]
    public async Task Resin控制面明确报告旧认证模式时给出V1迁移提示()
    {
        await using var resin = new ResinControlPlaneStub
        {
            AuthVersion = "LEGACY_V0"
        };
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-legacy-auth",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 1,
            Password = "proxy-token",
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();

        var tested = await CreateProxyService(db).TestAsync(proxy.Id);

        Assert.Equal("fail", tested.TestStatus);
        Assert.Contains("RESIN_AUTH_VERSION=V1", tested.LastError);
    }

    [Fact]
    public async Task Resin平台仅大小写变化也会释放旧身份Lease()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-platform-case",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            Password = "proxy-token",
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000106",
            UserId = 10007,
            SessionPath = "sessions/resin-platform-case.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = proxy
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var updated = await CreateProxyService(db).UpdateAsync(
            proxy.Id,
            NewResinInput("default"));

        Assert.Equal("default", updated.ResinPlatform);
        Assert.Contains(
            $"DELETE /api/v1/platforms/default-id/leases/tg_account_{account.Id} HTTP/1.1",
            resin.Requests);
    }

    [Fact]
    public async Task Resin已保存的ProxyToken和管理令牌可以显式清除()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-clear-token",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 2260,
            Password = "old-proxy-token",
            ResinPlatform = "Default",
            ResinAdminUrl = "http://127.0.0.1:2260",
            ResinAdminToken = "old-admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();
        var input = NewResinInput("Default") with
        {
            ResinAdminUrl = proxy.ResinAdminUrl,
            ClearPassword = true,
            ClearResinAdminToken = true
        };

        var updated = await CreateProxyService(db).UpdateAsync(proxy.Id, input);

        Assert.Null(updated.Password);
        Assert.Null(updated.ResinAdminToken);
    }

    [Fact]
    public async Task 编辑Resin身份时释放失败会保留旧控制面凭据()
    {
        await using var resin = new ResinControlPlaneStub(HttpStatusCode.ServiceUnavailable);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-edit-failure",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "old-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        var account = new Account
        {
            Phone = "8613800000199",
            UserId = 10199,
            SessionPath = "sessions/resin-edit-failure.session",
            ApiId = 1,
            ApiHash = "hash",
            Proxy = proxy
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var service = CreateProxyService(db);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                proxy.Id,
                new OutboundProxyInput(
                    proxy.Name,
                    OutboundProxyKinds.Resin,
                    OutboundProxyProtocols.Http,
                    proxy.Host,
                    proxy.Port,
                    proxy.Username,
                    Password: null,
                    Secret: null,
                    ResinPlatform: "NewPlatform",
                    ResinAdminUrl: "http://127.0.0.1:65534/",
                    ResinAdminToken: "new-token",
                    IsEnabled: true,
                    TestAfterSave: false)));

        Assert.Contains("Resin Lease", error.Message);
        var preserved = await db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Equal("Default", preserved.ResinPlatform);
        Assert.Equal(resin.BaseAddress.AbsoluteUri, preserved.ResinAdminUrl);
        Assert.Equal("old-token", preserved.ResinAdminToken);
        Assert.Equal(proxy.Id, (await db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
    }

    [Fact]
    public async Task Resin平台查询响应超过限制时会安全失败()
    {
        await using var resin = new ResinControlPlaneStub
        {
            PlatformContentLengthOverride = 1024 * 1024 + 1
        };
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = new OutboundProxy
        {
            Name = "resin-oversized-control-response",
            Kind = OutboundProxyKinds.Resin,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 1,
            ResinPlatform = "Default",
            ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
            ResinAdminToken = "admin-token",
            IsEnabled = true,
            TestStatus = "unknown"
        };
        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync();

        await CreateProxyService(db).TestAsync(proxy.Id);

        Assert.Equal("fail", proxy.TestStatus);
        Assert.Contains("超过 1024KB 限制", proxy.LastError);
    }

    [Fact]
    public async Task 全局Resin账号切换直连会释放稳定Lease()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = NewGlobalResinProxy(resin);
        var account = NewGlobalAccount("8613800000201", 10201);
        db.AddRange(proxy, account);
        await db.SaveChangesAsync();
        var configuration = GlobalResinConfiguration(proxy.Id);

        var result = await CreateProxyService(
                db,
                configuration: configuration)
            .BindAccountsAsync(
                new[] { account.Id },
                new AccountProxyBindingInput("direct"));

        Assert.Equal(1, result.Success);
        var switched = await db.Accounts.AsNoTracking().SingleAsync();
        Assert.Null(switched.ProxyId);
        Assert.False(switched.UseGlobalProxy);
        Assert.Contains(
            $"DELETE /api/v1/platforms/default-id/leases/tg_account_{account.Id} HTTP/1.1",
            resin.Requests);
    }

    [Fact]
    public async Task 删除全局Resin账号会释放稳定Lease()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = NewGlobalResinProxy(resin);
        var account = NewGlobalAccount("8613800000202", 10202);
        db.AddRange(proxy, account);
        await db.SaveChangesAsync();
        var configuration = GlobalResinConfiguration(proxy.Id);
        var pool = new NoopClientPool();
        var proxyService = CreateProxyService(db, pool, configuration);
        var accountService = new AccountManagementService(
            new AccountRepository(db),
            new ChannelRepository(db),
            new GroupRepository(db),
            pool,
            configuration,
            NullLogger<AccountManagementService>.Instance,
            proxyService,
            new SessionPathResolver(configuration));

        await accountService.DeleteAccountAsync(account.Id);

        Assert.False(await db.Accounts.AnyAsync());
        Assert.Contains(
            $"DELETE /api/v1/platforms/default-id/leases/tg_account_{account.Id} HTTP/1.1",
            resin.Requests);
    }

    [Fact]
    public async Task 关闭全局Resin会先释放全部Lease再提交配置并恢复账号()
    {
        await using var resin = new ResinControlPlaneStub();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = NewGlobalResinProxy(resin);
        var first = NewGlobalAccount("8613800000203", 10203);
        var second = NewGlobalAccount("8613800000204", 10204);
        db.AddRange(proxy, first, second);
        await db.SaveChangesAsync();
        var configuration = GlobalResinConfiguration(proxy.Id);
        var service = CreateProxyService(db, configuration: configuration);
        var applied = false;

        await service.ExecuteGlobalProxyChangeAsync(
            nextEnabled: false,
            nextSourceMode: GlobalTelegramProxyConfiguration.ExistingSourceMode,
            nextProxyId: proxy.Id,
            async _ =>
            {
                Assert.All(
                    await db.Accounts.AsNoTracking().ToListAsync(),
                    item => Assert.False(item.IsActive));
                applied = true;
                configuration["Telegram:Proxy:Enabled"] = "false";
            });

        Assert.True(applied);
        Assert.Equal(2, resin.Requests.Count(request => request.StartsWith(
            "DELETE /api/v1/platforms/default-id/leases/tg_account_",
            StringComparison.Ordinal)));
        Assert.All(
            await db.Accounts.AsNoTracking().ToListAsync(),
            item => Assert.True(item.IsActive));
        Assert.False(GlobalTelegramProxyConfiguration.IsEnabled(configuration));
    }

    [Fact]
    public async Task 全局Resin释放失败时不会提交新配置并保持账号停用()
    {
        await using var resin = new ResinControlPlaneStub(HttpStatusCode.ServiceUnavailable);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var proxy = NewGlobalResinProxy(resin);
        var account = NewGlobalAccount("8613800000205", 10205);
        db.AddRange(proxy, account);
        await db.SaveChangesAsync();
        var configuration = GlobalResinConfiguration(proxy.Id);
        var applied = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProxyService(db, configuration: configuration)
                .ExecuteGlobalProxyChangeAsync(
                    nextEnabled: false,
                    nextSourceMode: GlobalTelegramProxyConfiguration.ExistingSourceMode,
                    nextProxyId: proxy.Id,
                    _ =>
                    {
                        applied = true;
                        configuration["Telegram:Proxy:Enabled"] = "false";
                        return Task.CompletedTask;
                    }));

        Assert.Contains("Resin Lease", error.Message);
        Assert.False(applied);
        Assert.True(GlobalTelegramProxyConfiguration.IsEnabled(configuration));
        Assert.False((await db.Accounts.AsNoTracking().SingleAsync()).IsActive);
    }

    private static ProxyManagementService CreateProxyService(
        AppDbContext db,
        ITelegramClientPool? pool = null,
        IConfiguration? configuration = null)
    {
        pool ??= new NoopClientPool();
        configuration ??= new ConfigurationBuilder().Build();
        var probe = new ProxyEgressProbeService();
        return new ProxyManagementService(
            db,
            pool,
            probe,
            NullLogger<ProxyManagementService>.Instance,
            configuration);
    }

    private static OutboundProxy NewGlobalResinProxy(ResinControlPlaneStub resin) => new()
    {
        Name = "resin-global",
        Kind = OutboundProxyKinds.Resin,
        Protocol = OutboundProxyProtocols.Http,
        Host = "127.0.0.1",
        Port = 8080,
        ResinPlatform = "Default",
        ResinAdminUrl = resin.BaseAddress.AbsoluteUri,
        ResinAdminToken = "admin-token",
        IsEnabled = true,
        TestStatus = "unknown"
    };

    private static Account NewGlobalAccount(string phone, long userId) => new()
    {
        Phone = phone,
        UserId = userId,
        SessionPath = $"sessions/{phone}.session",
        ApiId = 1,
        ApiHash = "hash",
        UseGlobalProxy = true,
        IsActive = true
    };

    private static ConfigurationManager GlobalResinConfiguration(int proxyId)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:Proxy:Enabled"] = "true",
            ["Telegram:Proxy:SourceMode"] = GlobalTelegramProxyConfiguration.ExistingSourceMode,
            ["Telegram:Proxy:ProxyId"] = proxyId.ToString()
        });
        return configuration;
    }

    private static OutboundProxyInput NewResinInput(string platform) => new(
        "resin-validation",
        OutboundProxyKinds.Resin,
        OutboundProxyProtocols.Http,
        "127.0.0.1",
        2260,
        Username: null,
        Password: "proxy-token",
        Secret: null,
        ResinPlatform: platform,
        ResinAdminUrl: null,
        ResinAdminToken: null,
        IsEnabled: true,
        TestAfterSave: false);

    private sealed class NoopClientPool : ITelegramClientPool
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

    private sealed class ResinControlPlaneStub : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly Task _serveTask;

        public ResinControlPlaneStub(
            HttpStatusCode leaseDeleteStatus = HttpStatusCode.OK)
        {
            LeaseDeleteStatus = leaseDeleteStatus;
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            _serveTask = ServeAsync();
        }

        public Uri BaseAddress { get; }

        public HttpStatusCode LeaseDeleteStatus { get; set; }

        public bool IncludePlatform { get; set; } = true;

        public long? PlatformContentLengthOverride { get; set; }

        public bool TargetPlatformOnSecondPage { get; set; }

        public string? AuthVersion { get; set; }

        public IReadOnlyCollection<string> Requests => _requests.ToArray();

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _serveTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _stop.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await HandleAsync(client, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            _requests.Enqueue(requestLine);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var routeLine = requestLine.Replace(" /resin-admin/", " /", StringComparison.Ordinal);
            var body = routeLine.StartsWith("GET /api/v1/platforms", StringComparison.Ordinal)
                ? BuildPlatformBody(routeLine)
                : routeLine.StartsWith("GET /api/v1/system/info", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(AuthVersion)
                    ? $"{{\"auth_version\":\"{AuthVersion}\"}}"
                    : "{}";
            var status = routeLine.StartsWith(
                "DELETE /api/v1/platforms/",
                StringComparison.Ordinal)
                ? LeaseDeleteStatus
                : HttpStatusCode.OK;
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var contentLength = requestLine.StartsWith("GET /api/v1/platforms", StringComparison.Ordinal)
                ? PlatformContentLengthOverride ?? bodyBytes.Length
                : bodyBytes.Length;
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)status} {status}\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {contentLength}\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(bodyBytes, cancellationToken);
        }

        private string BuildPlatformBody(string requestLine)
        {
            if (!IncludePlatform)
                return "{\"items\":[]}";
            if (!TargetPlatformOnSecondPage)
                return "{\"items\":[{\"id\":\"default-id\",\"name\":\"Default\"}]}";
            if (requestLine.Contains("offset=100", StringComparison.Ordinal))
            {
                return "{\"total\":101,\"items\":["
                       + "{\"id\":\"default-id\",\"name\":\"Default\"}]}";
            }

            var items = Enumerable.Range(0, 100)
                .Select(index => new
                {
                    id = $"other-{index}",
                    name = $"Default-{index}"
                });
            return JsonSerializer.Serialize(new
            {
                total = 101,
                items
            });
        }
    }
}
