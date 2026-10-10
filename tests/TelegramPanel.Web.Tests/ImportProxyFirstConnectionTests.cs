using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
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
using TelegramPanel.Web.Services;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ImportProxyFirstConnectionTests
{
    [Fact]
    public async Task 全局WARP导入与自动维护双向互斥且不会提前连接()
    {
        var usageGuard = new AccountLoginProxyStateStore();
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = "true",
                ["Telegram:Proxy:SourceMode"] = GlobalTelegramProxyConfiguration.ExistingSourceMode,
                ["Telegram:Proxy:ProxyId"] = "1"
            },
            usageGuard);
        fixture.Proxy.Kind = OutboundProxyKinds.Warp;
        await fixture.Db.SaveChangesAsync();

        using (var maintenanceLease = usageGuard.TryAcquireMaintenance(fixture.Proxy.Id))
        {
            Assert.NotNull(maintenanceLease);
            var blocked = await fixture.Service.ImportFromStringSessionAsync(
                "session-data",
                12345,
                "0123456789abcdef0123456789abcdef",
                proxyBinding: new AccountProxyBindingInput("global"));

            Assert.False(blocked.Success);
            Assert.Contains("正在维护", blocked.Error);
            Assert.Equal(0, fixture.Importer.ImportCount);
        }

        fixture.Importer.BeforeImport = () =>
        {
            Assert.True(usageGuard.OwnsWarpProxy(fixture.Proxy.Id));
            Assert.Null(usageGuard.TryAcquireMaintenance(fixture.Proxy.Id));
        };
        var imported = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("global"));

        Assert.True(imported.Success, imported.Error);
        Assert.False(usageGuard.OwnsWarpProxy(fixture.Proxy.Id));
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Null(account.ProxyId);
        Assert.True(account.UseGlobalProxy);
    }

    [Fact]
    public async Task 已有WARP导入与自动维护双向互斥且不会提前连接()
    {
        var usageGuard = new AccountLoginProxyStateStore();
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            warpProxyUsageGuard: usageGuard);
        fixture.Proxy.Kind = OutboundProxyKinds.Warp;
        await fixture.Db.SaveChangesAsync();

        var maintenanceLease = usageGuard.TryAcquireMaintenance(fixture.Proxy.Id);
        Assert.NotNull(maintenanceLease);
        var blocked = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.False(blocked.Success);
        Assert.Contains("正在维护", blocked.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);

        maintenanceLease!.Dispose();
        fixture.Importer.BeforeImport = () =>
        {
            Assert.True(usageGuard.OwnsWarpProxy(fixture.Proxy.Id));
            Assert.Null(usageGuard.TryAcquireMaintenance(fixture.Proxy.Id));
        };
        var imported = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.True(imported.Success, imported.Error);
        Assert.False(usageGuard.OwnsWarpProxy(fixture.Proxy.Id));
    }

    [Fact]
    public async Task 已有WARP仍属另一临时创建流程时导入不会开始首次连接()
    {
        var temporaryWarpClaims = new TemporaryWarpClaimStore();
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            temporaryWarpClaims: temporaryWarpClaims);
        fixture.Proxy.Kind = OutboundProxyKinds.Warp;
        var profile = new WarpProfile
        {
            ProfileId = "active-import-owner",
            RequestId = "telegram-panel.internal.import.active-owner",
            ContainerName = "active-import-owner-container",
            ContainerId = "active-import-owner-container-id",
            VolumeName = "active-import-owner-volume",
            HostPort = 42095,
            Status = "active",
            DesiredEnabled = true,
            Proxy = fixture.Proxy
        };
        fixture.Db.WarpProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();

        using var ownerClaim = temporaryWarpClaims.ClaimRequest(profile.RequestId);
        var blocked = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.False(blocked.Success);
        Assert.Contains("另一个账号首次连接流程", blocked.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);
    }

    [Theory]
    [InlineData(OutboundProxyProtocols.Http)]
    [InlineData(OutboundProxyProtocols.Socks5)]
    [InlineData(OutboundProxyProtocols.MtProto)]
    public async Task 已有代理会在首次导入验证前传给SessionImporter(string protocol)
    {
        await using var fixture = await ImportFixture.CreateAsync(protocol);
        fixture.Importer.BeforeImport = () => Assert.Empty(fixture.Db.Accounts);
        fixture.ClientPool.OnRemoveClientAsync = async _ =>
        {
            var staged = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
            Assert.False(staged.IsActive);
        };

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(fixture.Importer.SeenProxy);
        Assert.Equal(fixture.Proxy.Id, fixture.Importer.SeenProxy!.ProxyId);
        Assert.Equal(protocol, fixture.Importer.SeenProxy.Protocol);
        Assert.Equal(
            fixture.Proxy.Id,
            await fixture.Db.Accounts.AsNoTracking().Select(x => x.ProxyId).SingleAsync());
        Assert.True(await fixture.Db.Accounts.AsNoTracking().Select(x => x.IsActive).SingleAsync());
    }

    [Fact]
    public async Task 绑定失败时导入账号保持停用且不会使用默认路由()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        var disabled = false;
        fixture.ClientPool.OnRemoveClientAsync = async _ =>
        {
            if (disabled)
                return;
            disabled = true;
            fixture.Proxy.IsEnabled = false;
            await fixture.Db.SaveChangesAsync();
        };

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.True(result.Success);
        Assert.Contains("保持停用", result.Error);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(account.IsActive);
        Assert.Null(account.ProxyId);
    }

    [Fact]
    public async Task 导入验证期间代理连接参数变化时保持停用并拒绝切换出口()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        var validationHost = fixture.Proxy.Host;
        fixture.ClientPool.OnRemoveClientAsync = async _ =>
        {
            fixture.Proxy.Host = "127.0.0.2";
            await fixture.Db.SaveChangesAsync();
        };

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.True(result.Success);
        Assert.Equal(validationHost, fixture.Importer.SeenProxy?.Host);
        Assert.Contains("连接参数已变化", result.Error);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(account.IsActive);
        Assert.Null(account.ProxyId);
    }

    [Fact]
    public async Task 同批重复账号首次绑定失败后后续条目会重试绑定()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        var failedOnce = false;
        fixture.ClientPool.OnRemoveClientAsync = _ =>
        {
            if (failedOnce)
                return Task.CompletedTask;

            failedOnce = true;
            throw new InvalidOperationException("模拟首次释放客户端失败");
        };
        var files = new[]
        {
            new AccountImportFile("first.session", new MemoryStream(new byte[] { 1 })),
            new AccountImportFile("second.session", new MemoryStream(new byte[] { 2 }))
        };

        try
        {
            var results = await fixture.Service.ImportFromSessionFileStreamsAsync(
                files,
                12345,
                "0123456789abcdef0123456789abcdef",
                proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

            Assert.Equal(2, results.Count);
            Assert.True(results[0].Success);
            Assert.Contains("保持停用", results[0].Error);
            Assert.True(results[1].Success, results[1].Error);
            var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
            Assert.True(account.IsActive);
            Assert.Equal(fixture.Proxy.Id, account.ProxyId);
        }
        finally
        {
            foreach (var file in files)
                await file.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task 同批重复账号会继承每次验证使用的最新Resin出口()
    {
        await using var resin = new ResinTokenActionStub();
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        fixture.Proxy.Kind = OutboundProxyKinds.Resin;
        fixture.Proxy.ResinPlatform = "Default";
        fixture.Proxy.Host = resin.BaseAddress.Host;
        fixture.Proxy.Port = resin.BaseAddress.Port;
        fixture.Proxy.Password = "proxy-token";
        await fixture.Db.SaveChangesAsync();

        var files = new[]
        {
            new AccountImportFile("first.session", new MemoryStream(new byte[] { 1 })),
            new AccountImportFile("second.session", new MemoryStream(new byte[] { 2 }))
        };

        try
        {
            var results = await fixture.Service.ImportFromSessionFileStreamsAsync(
                files,
                12345,
                "0123456789abcdef0123456789abcdef",
                proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

            Assert.Equal(2, results.Count);
            Assert.All(results, result => Assert.True(result.Success, result.Error));

            var temporaryIdentities = fixture.Importer.SeenProxies
                .Select(proxy => proxy?.Username)
                .Where(username => !string.IsNullOrWhiteSpace(username))
                .Select(username => username!["Default.".Length..])
                .ToArray();
            Assert.Equal(2, temporaryIdentities.Length);
            Assert.NotEqual(temporaryIdentities[0], temporaryIdentities[1]);

            var inheritRequests = resin.Requests.ToArray();
            Assert.Equal(2, inheritRequests.Length);
            Assert.Contains($"\"parent_account\":\"{temporaryIdentities[0]}\"", inheritRequests[0]);
            Assert.Contains($"\"parent_account\":\"{temporaryIdentities[1]}\"", inheritRequests[1]);

            var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
            Assert.True(account.IsActive);
            Assert.Equal(fixture.Proxy.Id, account.ProxyId);
        }
        finally
        {
            foreach (var file in files)
                await file.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task 显式直连首次验证不携带代理并在入库后关闭全局代理()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        fixture.Importer.BeforeImport = () => Assert.Empty(fixture.Db.Accounts);

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("direct"));

        Assert.True(result.Success, result.Error);
        Assert.Null(fixture.Importer.SeenProxy);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Null(account.ProxyId);
        Assert.False(account.UseGlobalProxy);
    }

    [Fact]
    public async Task 全局策略首次验证即使用Telegram全局代理并保留继承模式()
    {
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Server"] = "127.0.0.2",
                ["Telegram:Proxy:Port"] = "1088",
                ["Telegram:Proxy:Username"] = "global-user",
                ["Telegram:Proxy:Password"] = "global-pass"
            });

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("global"));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(fixture.Importer.SeenProxy);
        Assert.Equal(OutboundProxyProtocols.Socks5, fixture.Importer.SeenProxy!.Protocol);
        Assert.Equal("127.0.0.2", fixture.Importer.SeenProxy.Host);
        Assert.Equal(1088, fixture.Importer.SeenProxy.Port);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Null(account.ProxyId);
        Assert.True(account.UseGlobalProxy);
    }

    [Fact]
    public async Task 未配置全局代理时不会静默降级为直连导入()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("global"));

        Assert.False(result.Success);
        Assert.Contains("全局代理尚未配置", result.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Null(fixture.Importer.SeenProxy);
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 创建一对一WARP导入在环境未就绪时会在首次Telegram验证前拒绝且不创建容器记录()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("warp_per_account", AcceptWarpTerms: true));

        Assert.False(result.Success);
        Assert.True(IsWarpUnavailableError(result.Error), result.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking()
            .Where(x => x.Kind == OutboundProxyKinds.Warp)
            .ToListAsync());
    }

    [Fact]
    public async Task 未传代理策略时在首次连接前拒绝导入()
    {
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Server"] = "127.0.0.3",
                ["Telegram:Proxy:Port"] = "1090"
            });

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef");

        Assert.False(result.Success);
        Assert.Contains("明确选择", result.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Null(fixture.Importer.SeenProxy);
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 相同来源的两次Resin导入使用不同临时Lease身份()
    {
        await using var resin = new ResinTokenActionStub();
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        fixture.Proxy.Kind = OutboundProxyKinds.Resin;
        fixture.Proxy.ResinPlatform = "Default";
        fixture.Proxy.Host = resin.BaseAddress.Host;
        fixture.Proxy.Port = resin.BaseAddress.Port;
        fixture.Proxy.Password = "proxy-token";
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.ImportFromStringSessionAsync(
            "session-data-1",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));
        var second = await fixture.Service.ImportFromStringSessionAsync(
            "session-data-2",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        var usernames = fixture.Importer.SeenProxies
            .Select(x => x?.Username)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        Assert.Equal(2, usernames.Length);
        Assert.All(usernames, username => Assert.StartsWith("Default.tg_import_", username));
        Assert.NotEqual(usernames[0], usernames[1]);

        var accountId = await fixture.Db.Accounts.AsNoTracking().Select(x => x.Id).SingleAsync();
        var inheritRequests = resin.Requests.ToArray();
        Assert.Equal(2, inheritRequests.Length);
        var inheritRequest = inheritRequests[0];
        Assert.Contains(
            "POST /proxy-token/api/v1/Default/actions/inherit-lease HTTP/1.1",
            inheritRequest);
        var temporaryIdentity = usernames[0]!["Default.".Length..];
        Assert.Contains($"\"parent_account\":\"{temporaryIdentity}\"", inheritRequest);
        Assert.Contains($"\"new_account\":\"tg_account_{accountId}\"", inheritRequest);
        var secondTemporaryIdentity = usernames[1]!["Default.".Length..];
        Assert.Contains($"\"parent_account\":\"{secondTemporaryIdentity}\"", inheritRequests[1]);
        Assert.Contains($"\"new_account\":\"tg_account_{accountId}\"", inheritRequests[1]);
    }

    [Fact]
    public async Task Resin租约继承不可用时导入账号保持停用()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        fixture.Proxy.Kind = OutboundProxyKinds.Resin;
        fixture.Proxy.ResinPlatform = "Default";
        fixture.Proxy.Host = "127.0.0.1";
        fixture.Proxy.Port = 1;
        fixture.Proxy.Password = "proxy-token";
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));

        Assert.True(result.Success);
        Assert.Contains("无法保证正式连接沿用验证出口", result.Error);
        Assert.Contains("保持停用", result.Error);

        var retry = await fixture.Service.ImportFromStringSessionAsync(
            "session-data-retry",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("existing", fixture.Proxy.Id));
        Assert.True(retry.Success);
        Assert.Contains("无法保证正式连接沿用验证出口", retry.Error);
        Assert.Contains("保持停用", retry.Error);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(account.IsActive);
        Assert.Equal(fixture.Proxy.Id, account.ProxyId);
    }

    [Fact]
    public async Task 自动WARP池为空时不会开始首次Telegram验证或回退直连()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("warp_pool"));

        Assert.False(result.Success);
        Assert.Contains("没有可自动分配的已有 WARP", result.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 自动WARP池复用已有容器并优先分配当前绑定较少的出口()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        var firstWarp = await fixture.AddWarpAsync("WARP 1", 21080);
        var secondWarp = await fixture.AddWarpAsync("WARP 2", 21081);
        fixture.Importer.ResultFactory = count => new ImportResult(
            true,
            $"86138000000{count}",
            10000 + count,
            $"imported-{count}",
            $"sessions/86138000000{count}.session");
        var files = new[]
        {
            new AccountImportFile("first.session", new MemoryStream(new byte[] { 1 })),
            new AccountImportFile("second.session", new MemoryStream(new byte[] { 2 }))
        };

        try
        {
            var results = await fixture.Service.ImportFromSessionFileStreamsAsync(
                files,
                12345,
                "0123456789abcdef0123456789abcdef",
                proxyBinding: new AccountProxyBindingInput("warp_pool"));

            Assert.All(results, result => Assert.True(result.Success, result.Error));
            Assert.Equal(new[] { firstWarp.Id, secondWarp.Id },
                fixture.Importer.SeenProxies.Select(x => x!.ProxyId));
            Assert.Equal(2, await fixture.Db.OutboundProxies.CountAsync(
                x => x.Kind == OutboundProxyKinds.Warp));
            var bindings = await fixture.Db.Accounts.AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => x.ProxyId)
                .ToListAsync();
            Assert.Equal(new int?[] { firstWarp.Id, secondWarp.Id }, bindings);
        }
        finally
        {
            foreach (var file in files)
                await file.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task 创建一对一WARP导入会先创建轻量出口并把首连出口绑定到账号()
    {
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            warpDocker: new WarpLifecycleRegressionTests.FakeWarpDockerClient(),
            warpProbe: new SuccessfulWarpProbeService());
        fixture.Importer.ResultFactory = count => new ImportResult(
            true,
            $"86138000003{count}",
            10300 + count,
            $"warp-created-{count}",
            $"sessions/86138000003{count}.session");

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("warp_per_account", AcceptWarpTerms: true));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(fixture.Importer.SeenProxy);
        Assert.Equal(OutboundProxyKinds.WireGuardWarp, fixture.Importer.SeenProxy!.Kind);
        Assert.Equal(fixture.Importer.SeenProxy.ProxyId, result.ProxyId);
        Assert.Equal("2606:4700:100::90", result.ProxyEgressIp);
        var proxy = await fixture.Db.OutboundProxies
            .Include(x => x.WarpProfile)
            .AsNoTracking()
            .SingleAsync(x => x.Kind == OutboundProxyKinds.WireGuardWarp);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.True(account.IsActive);
        Assert.Equal(proxy.Id, account.ProxyId);
        Assert.NotNull(proxy.ManagedWgcfProfile);
        Assert.Empty(await fixture.Db.WarpProfiles.ToListAsync());
    }

    [Fact]
    public async Task 创建一对一WARP导入失败会停止出口且保留注册档案()
    {
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            warpDocker: new WarpLifecycleRegressionTests.FakeWarpDockerClient(),
            warpProbe: new SuccessfulWarpProbeService());
        fixture.Importer.ResultFactory = _ => new ImportResult(
            false,
            null,
            null,
            null,
            null,
            "模拟 Session 失效");

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new AccountProxyBindingInput("warp_per_account", AcceptWarpTerms: true));

        Assert.False(result.Success);
        Assert.Contains("模拟 Session 失效", result.Error);
        var proxyRows = await fixture.Db.OutboundProxies.AsNoTracking()
            .Where(x => x.Kind == OutboundProxyKinds.WireGuardWarp)
            .ToListAsync();
        var retained = Assert.Single(proxyRows);
        Assert.False(retained.IsEnabled);
        Assert.NotNull(retained.ManagedWgcfProfile);
        Assert.Equal(1, fixture.ManagedWarp!.StopCalls);
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 创建一对一WARP导入数量超过十个会在创建容器前拒绝()
    {
        var docker = new WarpLifecycleRegressionTests.FakeWarpDockerClient();
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            warpDocker: docker,
            warpProbe: new SuccessfulWarpProbeService());
        var files = Enumerable.Range(1, AccountImportService.MaxWarpPerAccountImportCount + 1)
            .Select(index => new AccountImportFile($"{index}.session", new MemoryStream(new byte[] { (byte)index })))
            .ToArray();

        try
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                fixture.Service.ImportFromSessionFileStreamsAsync(
                    files,
                    12345,
                    "0123456789abcdef0123456789abcdef",
                    proxyBinding: new AccountProxyBindingInput("warp_per_account")));

            Assert.Contains("单次最多导入 10 个账号", error.Message);
            Assert.Equal(0, docker.CreateVolumeCalls);
            Assert.Equal(0, fixture.Importer.ImportCount);
            Assert.Empty(await fixture.Db.OutboundProxies.AsNoTracking()
                .Where(x => x.Kind == OutboundProxyKinds.Warp)
                .ToListAsync());
        }
        finally
        {
            foreach (var file in files)
                await file.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task 轻量出口未检测就绪时不发起首次连接且停止保留材料()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http,
            warpDocker: new WarpLifecycleRegressionTests.FakeWarpDockerClient(), managedReady: false);
        var result = await fixture.Service.ImportFromStringSessionAsync("session-data", 12345,
            "0123456789abcdef0123456789abcdef",
            proxyBinding: new("warp_per_account", AcceptWarpTerms: true));
        Assert.False(result.Success);
        Assert.Equal(0, fixture.Importer.ImportCount);
        Assert.Equal(1, fixture.ManagedWarp!.StopCalls);
        var retained = await fixture.Db.OutboundProxies.AsNoTracking().SingleAsync(x => x.ManagedWgcfProfile != null);
        Assert.False(retained.IsEnabled);
        Assert.Empty(await fixture.Db.Accounts.ToListAsync());
    }

    [Fact]
    public async Task 导入未接受条款时不创建出口且不发起首次连接()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http,
            warpDocker: new WarpLifecycleRegressionTests.FakeWarpDockerClient());
        var result = await fixture.Service.ImportFromStringSessionAsync("session-data", 12345,
            "0123456789abcdef0123456789abcdef", proxyBinding: new("warp_per_account"));
        Assert.False(result.Success);
        Assert.Contains("条款", result.Error);
        Assert.Equal(0, fixture.ManagedWarp!.ProvisionCalls);
        Assert.Equal(0, fixture.Importer.ImportCount);
    }

    [Theory]
    [InlineData("warp_per_account")]
    [InlineData("warp_pool")]
    public async Task 导入严格断开失败保留轻量出口独占直到重启且拒绝他人抢绑定(string strategy)
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http,
            warpDocker: new WarpLifecycleRegressionTests.FakeWarpDockerClient());
        if (strategy == "warp_pool")
        {
            using var ready = await fixture.ManagedWarp!.ProvisionAsync("pool", Guid.NewGuid().ToString("D"), true);
        }
        fixture.Importer.ResultFactory = _ => new ImportResult(true, "8613800000400", 10400,
            "retained", "sessions/retained.session");
        fixture.ClientPool.OnRemoveClientAsync = _ => throw new InvalidOperationException("模拟客户端无法断开");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ImportFromStringSessionAsync(
            "session-data", 12345, "0123456789abcdef0123456789abcdef",
            proxyBinding: new(strategy, AcceptWarpTerms: true)));
        Assert.Contains("需重启面板", error.Message);
        var retained = await fixture.Db.OutboundProxies.AsNoTracking().SingleAsync(x => x.ManagedWgcfProfile != null);
        Assert.True(retained.IsEnabled);
        Assert.Equal(0, fixture.ManagedWarp!.StopCalls);
        var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.False(account.IsActive);
        Assert.Null(account.ProxyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ManagedWarp.AcquireAsync(retained.Id));
        fixture.ClientPool.OnRemoveClientAsync = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ProxyManagement!.BindAccountsAsync(
            new[] { account.Id }, new("existing", retained.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ProxyManagement!.SetManagedWgcfEnabledAsync(
            retained.ManagedWgcfProfile!, false));
        Assert.Null((await fixture.Db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
        Assert.True((await fixture.Db.OutboundProxies.AsNoTracking().SingleAsync(x => x.Id == retained.Id)).IsEnabled);
    }

    [Fact]
    public async Task 自动WARP池可独占未绑定轻量出口且第二账号不会复用同出口()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http,
            warpDocker: new WarpLifecycleRegressionTests.FakeWarpDockerClient());
        int managedId;
        using (var ready = await fixture.ManagedWarp!.ProvisionAsync("pool", Guid.NewGuid().ToString("D"), true))
            managedId = ready.Proxy.Id;
        fixture.Importer.BeforeImportAsync = async () =>
        {
            using var competing = await fixture.ManagedWarp!.AcquireAsync(managedId);
        };
        var blocked = await fixture.Service.ImportFromStringSessionAsync("session-data", 12345,
            "0123456789abcdef0123456789abcdef", proxyBinding: new("warp_pool"));
        Assert.False(blocked.Success);
        Assert.Contains("占用", blocked.Error);
        fixture.Importer.BeforeImportAsync = null;
        fixture.Importer.ResultFactory = _ => new ImportResult(true, "8613800000399", 10399, "pool", "sessions/pool.session");
        var bound = await fixture.Service.ImportFromStringSessionAsync("session-data", 12345,
            "0123456789abcdef0123456789abcdef", proxyBinding: new("warp_pool"));
        Assert.True(bound.Success, bound.Error);
        Assert.Equal(managedId, (await fixture.Db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
        var second = await fixture.Service.ImportFromStringSessionAsync("other-session", 12345,
            "0123456789abcdef0123456789abcdef", proxyBinding: new("warp_pool"));
        Assert.False(second.Success);
        Assert.Contains("没有可自动分配", second.Error);
        Assert.Equal(1, fixture.ManagedWarp!.ProvisionCalls);
    }

    [Fact]
    public async Task Zip条目数超限会在解压前整体拒绝()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        await using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < 5_001; index++)
                archive.CreateEntry($"empty/{index}.txt");
        }
        zipStream.Position = 0;

        var results = await fixture.Service.ImportFromZipStreamAsync("too-many.zip", zipStream);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("条目数超过上限", result.Error);
        Assert.Equal(0, fixture.Importer.ImportCount);
    }

    [Fact]
    public async Task 批量导入重复手机号不会再次覆盖已保存账号()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        var directory = Path.Combine(Path.GetTempPath(), $"telegram-panel-duplicate-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "8613800000200.session");
        fixture.Importer.ResultFactory = count =>
        {
            var replacement = AtomicSessionFileReplacement.Create(target);
            File.WriteAllText(replacement.StagingPath, $"session-{count}");
            replacement.Apply();
            return new ImportResult(
                true,
                "8613800000200",
                10200,
                $"imported-{count}",
                target)
            {
                PendingSessionReplacement = replacement
            };
        };
        var files = new[]
        {
            new AccountImportFile("first.session", new MemoryStream(new byte[] { 1 })),
            new AccountImportFile("second.session", new MemoryStream(new byte[] { 2 }))
        };

        try
        {
            var results = await fixture.Service.ImportFromSessionFileStreamsAsync(
                files,
                12345,
                "0123456789abcdef0123456789abcdef",
                proxyBinding: new AccountProxyBindingInput("direct"));

            Assert.Equal(2, results.Count);
            Assert.True(results[0].Success, results[0].Error);
            Assert.False(results[1].Success);
            Assert.Equal("重复账号已跳过", results[1].Error);
            var account = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
            Assert.Equal("imported-1", account.Username);
            Assert.Equal(target, account.SessionPath);
            Assert.Equal("session-1", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.rollback-*.session"));
        }
        finally
        {
            foreach (var file in files)
                await file.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task 数据库保存失败时会恢复被替换的旧Session()
    {
        await using var fixture = await ImportFixture.CreateAsync(OutboundProxyProtocols.Http);
        var directory = Path.Combine(Path.GetTempPath(), $"telegram-panel-atomic-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "8613800000201.session");
        await File.WriteAllTextAsync(target, "old-session");
        fixture.Importer.ResultFactory = _ =>
        {
            var replacement = AtomicSessionFileReplacement.Create(target);
            File.WriteAllText(replacement.StagingPath, "new-session");
            replacement.Apply();
            return new ImportResult(
                true,
                "8613800000201",
                10201,
                "atomic-import",
                target)
            {
                PendingSessionReplacement = replacement
            };
        };

        var result = await fixture.Service.ImportFromStringSessionAsync(
            "session-data",
            12345,
            "0123456789abcdef0123456789abcdef",
            categoryId: int.MaxValue,
            proxyBinding: new AccountProxyBindingInput("direct"));

        Assert.False(result.Success);
        Assert.Contains("文件已回滚", result.Error);
        Assert.Equal("old-session", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.rollback-*.session"));
        Assert.Empty(await fixture.Db.Accounts.AsNoTracking().ToListAsync());
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task 压缩包导入数据库失败时也会恢复旧Session()
    {
        var sessionsPath = Path.Combine(Path.GetTempPath(), $"telegram-panel-package-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionsPath);
        await using var fixture = await ImportFixture.CreateAsync(
            OutboundProxyProtocols.Http,
            new Dictionary<string, string?>
            {
                ["Telegram:SessionsPath"] = sessionsPath
            });
        var target = Path.Combine(sessionsPath, "8613800000202.session");
        await File.WriteAllTextAsync(target, "old-package-session");
        await using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var jsonEntry = archive.CreateEntry("account/account.json");
            await using (var json = jsonEntry.Open())
            {
                await json.WriteAsync(Encoding.UTF8.GetBytes(
                    "{\"api_id\":12345,\"api_hash\":\"0123456789abcdef0123456789abcdef\",\"phone\":\"+8613800000202\",\"user_id\":10202}"));
            }

            var sessionEntry = archive.CreateEntry("account/account.session");
            await using var session = sessionEntry.Open();
            await session.WriteAsync(Encoding.UTF8.GetBytes("new-package-session"));
        }
        zipStream.Position = 0;

        await fixture.Db.DisposeAsync();
        var results = await fixture.Service.ImportFromZipStreamAsync(
            "atomic-package.zip",
            zipStream,
            proxyBinding: new AccountProxyBindingInput("direct"));

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Equal("old-package-session", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.EnumerateFiles(sessionsPath, "*.rollback-*.session"));
    }

    [Theory]
    [InlineData(OutboundProxyProtocols.Http)]
    [InlineData(OutboundProxyProtocols.Socks5)]
    public void Http与Socks5导入验证使用统一Tcp连接器(string protocol)
    {
        using var client = CreateClient();
        ApplyImportProxy(client, NewOptions(protocol));

        Assert.NotNull(client.TcpHandler);
    }

    [Fact]
    public void MTProto导入验证配置MTProxyUrl()
    {
        using var client = CreateClient();
        ApplyImportProxy(client, NewOptions(OutboundProxyProtocols.MtProto));

        Assert.Equal(
            "https://t.me/proxy?server=127.0.0.1&port=1080&secret=abcdef",
            client.MTProxyUrl);
    }

    private static bool IsWarpUnavailableError(string? error)
    {
        return !string.IsNullOrWhiteSpace(error)
            && error.Contains("轻量 WARP 创建服务不可用", StringComparison.Ordinal);
    }

    private static Client CreateClient()
    {
        var sessionPath = Path.Combine(
            Path.GetTempPath(),
            $"telegram-panel-import-proxy-test-{Guid.NewGuid():N}.session");
        string Config(string what) => what switch
        {
            "api_id" => "12345",
            "api_hash" => "0123456789abcdef0123456789abcdef",
            "session_pathname" => sessionPath,
            "session_key" => "0123456789abcdef0123456789abcdef",
            _ => null!
        };
        return new Client(Config);
    }

    private static void ApplyImportProxy(Client client, ProxyConnectionOptions options)
    {
        var configurator = typeof(SessionImporter).Assembly.GetType(
            "TelegramPanel.Core.Services.Telegram.TelegramImportProxyConfigurator",
            throwOnError: true)!;
        var apply = configurator.GetMethod(
            "Apply",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("未找到导入代理配置方法");
        apply.Invoke(null, new object?[] { client, options, CancellationToken.None });
    }

    private static ProxyConnectionOptions NewOptions(string protocol) => new(
        7,
        "import-proxy",
        OutboundProxyKinds.Manual,
        protocol,
        "127.0.0.1",
        1080,
        "user",
        "password",
        protocol == OutboundProxyProtocols.MtProto ? "abcdef" : null);

    private sealed class ImportFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ImportFixture(
            SqliteConnection connection,
            AppDbContext db,
            RecordingSessionImporter importer,
            AccountImportService service,
            OutboundProxy proxy,
            StubClientPool clientPool)
        {
            _connection = connection;
            Db = db;
            Importer = importer;
            Service = service;
            Proxy = proxy;
            ClientPool = clientPool;
        }

        public AppDbContext Db { get; }
        public RecordingSessionImporter Importer { get; }
        public AccountImportService Service { get; }
        public OutboundProxy Proxy { get; }
        public StubClientPool ClientPool { get; }
        public FakeManagedWarpProvisioner? ManagedWarp { get; private set; }
        public ProxyManagementService? ProxyManagement { get; private set; }

        public async Task<OutboundProxy> AddWarpAsync(string name, int port)
        {
            var proxy = new OutboundProxy
            {
                Name = name,
                Kind = OutboundProxyKinds.Warp,
                Protocol = OutboundProxyProtocols.Http,
                Host = "127.0.0.1",
                Port = port,
                IsEnabled = true,
                WarpProfile = new WarpProfile
                {
                    ProfileId = Guid.NewGuid().ToString("N"),
                    ContainerName = $"test-warp-{port}",
                    ContainerId = Guid.NewGuid().ToString("N"),
                    VolumeName = $"test-warp-volume-{port}",
                    HostPort = port,
                    Status = "active",
                    DesiredEnabled = true
                }
            };
            Db.OutboundProxies.Add(proxy);
            await Db.SaveChangesAsync();
            return proxy;
        }

        public static async Task<ImportFixture> CreateAsync(
            string protocol,
            IEnumerable<KeyValuePair<string, string?>>? configurationValues = null,
            IWarpProxyUsageGuard? warpProxyUsageGuard = null,
            TemporaryWarpClaimStore? temporaryWarpClaims = null,
            WarpLifecycleRegressionTests.FakeWarpDockerClient? warpDocker = null,
            IProxyEgressProbeService? warpProbe = null,
            bool managedReady = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var proxy = new OutboundProxy
            {
                Name = $"import-{protocol}",
                Kind = OutboundProxyKinds.Manual,
                Protocol = protocol,
                Host = "127.0.0.1",
                Port = 1080,
                Secret = protocol == OutboundProxyProtocols.MtProto ? "abcdef" : null,
                IsEnabled = true
            };
            db.OutboundProxies.Add(proxy);
            await db.SaveChangesAsync();

            var configurationBuilder = new ConfigurationBuilder();
            var values = new Dictionary<string, string?>
            {
                ["Proxy:Warp:Enabled"] = warpDocker == null ? null : "true",
                ["Proxy:Warp:DockerSocketPath"] = Path.Combine(Path.GetTempPath(), "fake-docker.sock"),
                ["Proxy:Warp:HostPortStart"] = "42180"
            };
            if (configurationValues != null)
            {
                foreach (var pair in configurationValues)
                    values[pair.Key] = pair.Value;
            }
            configurationBuilder.AddInMemoryCollection(values);
            var configuration = configurationBuilder.Build();
            var pool = new StubClientPool();
            var probe = warpProbe ?? new ProxyEgressProbeService();
            temporaryWarpClaims ??= new TemporaryWarpClaimStore();
            var managedWarp = warpDocker == null ? null : new FakeManagedWarpProvisioner(db, temporaryWarpClaims)
            {
                Ready = managedReady
            };
            var warp = warpDocker == null
                ? new WarpContainerManager(
                    db,
                    configuration,
                    probe,
                    NullLogger<WarpContainerManager>.Instance)
                : new WarpContainerManager(
                    db,
                    configuration,
                    probe,
                    NullLogger<WarpContainerManager>.Instance,
                    new FakeDockerClientFactory(warpDocker));
            var proxyManagement = new ProxyManagementService(
                db,
                pool,
                probe,
                warp,
                NullLogger<ProxyManagementService>.Instance,
                configuration,
                temporaryWarpClaims: temporaryWarpClaims,
                warpProxyUsageGuard: warpProxyUsageGuard,
                managedWarpProvisioner: managedWarp);
            var accountManagement = new AccountManagementService(
                new AccountRepository(db),
                new ChannelRepository(db),
                new GroupRepository(db),
                pool,
                configuration,
                NullLogger<AccountManagementService>.Instance,
                proxyManagement,
                new SessionPathResolver(configuration));
            var importer = new RecordingSessionImporter();
            var service = new AccountImportService(
                importer,
                db,
                accountManagement,
                NullLogger<AccountImportService>.Instance,
                configuration,
                proxyManagement,
                temporaryWarpClaims,
                warpProxyUsageGuard);

            return new ImportFixture(connection, db, importer, service, proxy, pool)
            {
                ManagedWarp = managedWarp, ProxyManagement = proxyManagement
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private sealed class FakeDockerClientFactory : WarpContainerManager.IWarpDockerClientFactory
        {
            private readonly WarpLifecycleRegressionTests.FakeWarpDockerClient _client;

            public FakeDockerClientFactory(WarpLifecycleRegressionTests.FakeWarpDockerClient client)
            {
                _client = client;
            }

            public bool PlatformSupported => true;

            public WarpContainerManager.IWarpDockerClient Create(string socketPath) => _client;
        }
    }

    private sealed class RecordingSessionImporter : ISessionImporter
    {
        public Action? BeforeImport { get; set; }
        public Func<Task>? BeforeImportAsync { get; set; }
        public Func<int, ImportResult>? ResultFactory { get; set; }
        public ProxyConnectionOptions? SeenProxy { get; private set; }
        public List<ProxyConnectionOptions?> SeenProxies { get; } = new();
        public int ImportCount { get; private set; }

        public Task<ImportResult> ImportFromSessionFileAsync(
            string filePath,
            int apiId,
            string apiHash,
            long? userId = null,
            string? phoneHint = null,
            string? sessionKey = null,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            ImportAsync(proxy);

        public async Task<List<ImportResult>> BatchImportSessionFilesAsync(
            string[] filePaths,
            int apiId,
            string apiHash,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            new() { await ImportAsync(proxy) };

        public Task<ImportResult> ImportFromStringSessionAsync(
            string sessionString,
            int apiId,
            string apiHash,
            ProxyConnectionOptions? proxy = null,
            CancellationToken cancellationToken = default) =>
            ImportAsync(proxy);

        public Task<bool> ValidateSessionAsync(string sessionPath) => Task.FromResult(true);

        private async Task<ImportResult> ImportAsync(ProxyConnectionOptions? proxy)
        {
            ImportCount++;
            SeenProxy = proxy;
            SeenProxies.Add(proxy);
            BeforeImport?.Invoke();
            if (BeforeImportAsync != null) await BeforeImportAsync();
            return ResultFactory?.Invoke(ImportCount) ?? new ImportResult(
                true,
                "8613800000000",
                10001,
                "imported",
                "sessions/8613800000000.session");
        }
    }

    private sealed class SuccessfulWarpProbeService : IProxyEgressProbeService
    {
        public int CallCount { get; private set; }

        public Task<EgressProbeResult> ProbePanelAsync(CancellationToken cancellationToken = default) =>
            ProbeAsync(cancellationToken);

        public Task<EgressProbeResult> ProbeProxyAsync(
            OutboundProxy proxy,
            string stableAccountKey,
            CancellationToken cancellationToken = default) =>
            ProbeAsync(cancellationToken);

        public Task<EgressProbeResult> ProbeProxyAsync(
            ProxyConnectionOptions options,
            bool requireWarp = false,
            CancellationToken cancellationToken = default) =>
            ProbeAsync(cancellationToken);

        private Task<EgressProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new EgressProbeResult(
                true,
                "2606:4700:100::90",
                "US",
                null,
                null,
                "on",
                12,
                DateTime.UtcNow,
                null));
        }
    }

    private sealed class StubClientPool : ITelegramClientPool
    {
        public int ActiveClientCount => 0;
        public Func<int, Task>? OnRemoveClientAsync { get; set; }

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
        public Task RemoveClientAsync(int accountId) =>
            OnRemoveClientAsync?.Invoke(accountId) ?? Task.CompletedTask;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }

    private sealed class ResinTokenActionStub : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly Task _serveTask;

        public ResinTokenActionStub()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            _serveTask = ServeAsync();
        }

        public Uri BaseAddress { get; }

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
            catch (Exception ex) when (_stop.IsCancellationRequested
                                       && ex is OperationCanceledException
                                           or SocketException
                                           or ObjectDisposedException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            var contentLength = 0;
            string? header;
            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(cancellationToken)))
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(
                        header["Content-Length:".Length..].Trim(),
                        out contentLength);
                }
            }

            var bodyChars = new char[contentLength];
            var charsRead = 0;
            while (charsRead < bodyChars.Length)
            {
                var read = await reader.ReadAsync(
                    bodyChars.AsMemory(charsRead, bodyChars.Length - charsRead),
                    cancellationToken);
                if (read == 0)
                    break;
                charsRead += read;
            }
            _requests.Enqueue($"{requestLine}\n{new string(bodyChars, 0, charsRead)}");

            var responseBody = Encoding.UTF8.GetBytes("{}");
            var responseHeaders = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {responseBody.Length}\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(responseBody, cancellationToken);
        }
    }
}
