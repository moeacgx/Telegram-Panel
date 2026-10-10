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

public sealed class WireGuardWarpEndpointTests
{
    [Fact]
    public async Task 外部WireGuardWarp端点保存时复用HTTP代理记录且不创建受管WARP资料()
    {
        await using var fixture = await Fixture.CreateAsync();

        var proxy = await fixture.Service.CreateAsync(new OutboundProxyInput(
            Name: "wg-warp-sg-01",
            Kind: OutboundProxyKinds.WireGuardWarp,
            Protocol: OutboundProxyProtocols.Socks5,
            Host: "127.0.0.1",
            Port: 1080,
            Username: "warp-user",
            Password: "warp-pass",
            Secret: null,
            ResinPlatform: null,
            ResinAdminUrl: null,
            ResinAdminToken: null,
            IsEnabled: true,
            TestAfterSave: true));

        Assert.Equal(OutboundProxyKinds.WireGuardWarp, proxy.Kind);
        Assert.Equal(OutboundProxyProtocols.Socks5, proxy.Protocol);
        Assert.Equal("ok", proxy.TestStatus);
        Assert.Equal("203.0.113.57", proxy.EgressIp);
        Assert.Null(proxy.WarpProfile);
        Assert.Empty(await fixture.Db.WarpProfiles.AsNoTracking().ToListAsync());

        var stored = await fixture.Db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Equal(OutboundProxyKinds.WireGuardWarp, stored.Kind);
    }

    [Fact]
    public async Task 外部WireGuardWarp导入模板会规范化为可绑定的已有代理()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = new Account
        {
            Phone = "8613800000000",
            UserId = 10001,
            SessionPath = "sessions/wg-warp.session",
            ApiId = 1,
            ApiHash = "hash",
            IsActive = true
        };
        fixture.Db.Accounts.Add(account);
        await fixture.Db.SaveChangesAsync();

        var imported = await fixture.Service.ImportAsync(
            "wg-warp+socks5://warp-user:warp-pass@127.0.0.1:1081",
            testAfterImport: true);
        var proxy = Assert.Single(imported);

        Assert.Equal(OutboundProxyKinds.WireGuardWarp, proxy.Kind);
        Assert.Equal(OutboundProxyProtocols.Socks5, proxy.Protocol);
        Assert.Equal("WireGuard WARP socks5://127.0.0.1:1081", proxy.Name);
        Assert.Equal("ok", proxy.TestStatus);

        var result = await fixture.Service.BindAccountsAsync(
            new[] { account.Id },
            new AccountProxyBindingInput("existing", proxy.Id));

        Assert.Equal(1, result.Success);
        fixture.Db.ChangeTracker.Clear();
        var rebound = await fixture.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(proxy.Id, rebound.ProxyId);
        Assert.False(rebound.UseGlobalProxy);
    }

    [Fact]
    public async Task 未经Warp检测成功的外部WireGuardWarp端点不能绑定账号()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = new OutboundProxy
        {
            Name = "untested-wg-warp",
            Kind = OutboundProxyKinds.WireGuardWarp,
            Protocol = OutboundProxyProtocols.Http,
            Host = "127.0.0.1",
            Port = 8080,
            IsEnabled = true,
            TestStatus = "unknown",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var account = new Account
        {
            Phone = "8613800000001",
            UserId = 10002,
            SessionPath = "sessions/untested-wg-warp.session",
            ApiId = 1,
            ApiHash = "hash",
            IsActive = true
        };
        fixture.Db.OutboundProxies.Add(proxy);
        fixture.Db.Accounts.Add(account);
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BindAccountsAsync(
                new[] { account.Id },
                new AccountProxyBindingInput("existing", proxy.Id)));

        Assert.Contains("外部 WireGuard WARP", error.Message);
        Assert.Contains("检测成功", error.Message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, AppDbContext db, ProxyManagementService service)
        {
            _connection = connection;
            Db = db;
            Service = service;
        }

        public AppDbContext Db { get; }
        public ProxyManagementService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder().Build();
            var probe = new WarpOnProbe();
            var service = new ProxyManagementService(
                db,
                new EmptyClientPool(),
                probe,
                NullLogger<ProxyManagementService>.Instance,
                configuration);

            return new Fixture(connection, db, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class WarpOnProbe : IProxyEgressProbeService
    {
        public Task<EgressProbeResult> ProbePanelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<EgressProbeResult> ProbeProxyAsync(
            OutboundProxy proxy,
            string stableAccountKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<EgressProbeResult> ProbeProxyAsync(
            ProxyConnectionOptions options,
            bool requireWarp = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        private static EgressProbeResult Success() => new(
            true,
            "203.0.113.57",
            "SG",
            "Singapore",
            "Cloudflare WARP",
            "on",
            12,
            DateTime.UtcNow,
            null);
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
            long? userId = null) =>
            throw new NotSupportedException();

        public Client? GetClient(int accountId) => null;
        public Task RemoveClientAsync(int accountId) => Task.CompletedTask;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }
}
