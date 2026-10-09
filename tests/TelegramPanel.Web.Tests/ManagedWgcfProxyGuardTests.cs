using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ManagedWgcfProxyGuardTests
{
    [Fact]
    public async Task 受管出口拒绝多人绑定且不改变旧路由和激活状态()
    {
        await using var f = await Fixture.CreateAsync();
        var proxy = await f.ManagedAsync();
        var first = await f.AccountAsync("1");
        var second = await f.AccountAsync("2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BindAccountsAsync(
            new[] { first.Id, second.Id }, new("existing", proxy.Id)));
        Assert.All(await f.Db.Accounts.AsNoTracking().ToListAsync(), a =>
        {
            Assert.Null(a.ProxyId);
            Assert.True(a.IsActive);
            Assert.True(a.UseGlobalProxy);
        });
    }

    [Fact]
    public async Task 已有所有者的受管出口拒绝第二账号并保留第一绑定()
    {
        await using var f = await Fixture.CreateAsync();
        var proxy = await f.ManagedAsync();
        var first = await f.AccountAsync("1", proxy);
        var second = await f.AccountAsync("2");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BindAccountsAsync(
            new[] { second.Id }, new("existing", proxy.Id)));
        Assert.Equal(proxy.Id, (await f.Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == first.Id)).ProxyId);
        Assert.Null((await f.Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == second.Id)).ProxyId);
    }

    [Fact]
    public async Task 受管出口拒绝全局已有代理选择且不调用保存回调()
    {
        await using var f = await Fixture.CreateAsync();
        var proxy = await f.ManagedAsync();
        var saved = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ExecuteGlobalProxyChangeAsync(
            true, "existing", proxy.Id, _ => { saved = true; return Task.CompletedTask; }));
        Assert.False(saved);
    }

    [Fact]
    public async Task 受管出口不允许作为导入登录首次连接的已有代理()
    {
        await using var f = await Fixture.CreateAsync();
        var proxy = await f.ManagedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ValidateBindingInputAsync(new("existing", proxy.Id)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public async Task 普通创建和逐账号导入不能克隆受管监听端口(string host)
    {
        await using var f = await Fixture.CreateAsync();
        var proxy = await f.ManagedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CreateAsync(Input(host, proxy.Port)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.PrepareAccountProxyAssignmentsAsync(
            $"socks5://shadow:secret@{(host.Contains(':') ? $"[{host}]" : host)}:{proxy.Port}", 1));
        Assert.Single(await f.Db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 普通修改删除和仓储捷径不能变更受管出口()
    {
        await using var f = await Fixture.CreateAsync();
        var proxy = await f.ManagedAsync();
        var account = await f.AccountAsync("1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.UpdateAsync(proxy.Id, Input("127.0.0.1", proxy.Port)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DeleteAsync(proxy.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OutboundProxyRepository(f.Db).BindAccountsAsync(
            new[] { account.Id }, proxy.Id));
        Assert.Single(await f.Db.OutboundProxies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 手改全局配置引用受管出口时运行解析拒绝且不返回直连()
    {
        await using var f = await Fixture.CreateAsync();
        var proxy = await f.ManagedAsync();
        f.Configuration["Telegram:Proxy:Enabled"] = "true";
        f.Configuration["Telegram:Proxy:SourceMode"] = "existing";
        f.Configuration["Telegram:Proxy:ProxyId"] = proxy.Id.ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GlobalProxyResolver(f.Db, f.Configuration).ResolveAsync("account"));
        f.Configuration["Telegram:Proxy:SourceMode"] = "manual";
        f.Configuration["Telegram:Proxy:Server"] = "localhost";
        f.Configuration["Telegram:Proxy:Port"] = proxy.Port.ToString();
        f.Configuration["Telegram:Proxy:Protocol"] = "socks5";
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GlobalProxyResolver(f.Db, f.Configuration).ResolveAsync("account"));
    }

    [Fact]
    public async Task 专用创建幂等且启停不允许跳过占用检查()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Service.CreateManagedWgcfProxyAsync("profile-one", "managed", 18081, "user", "secret");
        var same = await f.Service.CreateManagedWgcfProxyAsync("profile-one", "ignored", 18081, "user", "secret");
        Assert.Equal(first.Id, same.Id);
        var account = await f.AccountAsync("1", first);
        await Assert.ThrowsAsync<ProxyInUseException>(() => f.Service.SetManagedWgcfEnabledAsync("profile-one", false));
        Assert.True((await f.Db.OutboundProxies.AsNoTracking().SingleAsync()).IsEnabled);
        f.Db.Accounts.Remove(account);
        await f.Db.SaveChangesAsync();
        var stopped = await f.Service.SetManagedWgcfEnabledAsync("profile-one", false);
        Assert.False(stopped.IsEnabled);
        Assert.Equal("unknown", stopped.TestStatus);
        var resumed = await f.Service.SetManagedWgcfEnabledAsync("profile-one", true);
        Assert.True(resumed.IsEnabled);
        Assert.Equal("unknown", resumed.TestStatus);
    }

    [Fact]
    public async Task CAS同时检查旧代理和全局模式()
    {
        await using var f = await Fixture.CreateAsync();
        var managed = await f.ManagedAsync();
        var account = await f.AccountAsync("1");
        await Assert.ThrowsAsync<ProxyBindingConflictException>(() => f.Service.BindAccountsAsync(
            new[] { account.Id }, new("existing", managed.Id, 0, null, false)));
        var unchanged = await f.Db.Accounts.AsNoTracking().SingleAsync();
        Assert.True(unchanged.UseGlobalProxy);
        Assert.Null(unchanged.ProxyId);
    }

    private static OutboundProxyInput Input(string host, int port) => new(
        "shadow", "manual", "socks5", host, port, "shadow", "secret", null, null, null, null);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }
        public IConfiguration Configuration { get; }
        public ProxyManagementService Service { get; }
        private Fixture(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
            Configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var probe = new Probe();
            Service = new(db, new EmptyPool(), probe,
                new WarpContainerManager(db, Configuration, probe, NullLogger<WarpContainerManager>.Instance),
                NullLogger<ProxyManagementService>.Instance, Configuration);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new(connection, db);
        }
        public async Task<OutboundProxy> ManagedAsync()
        {
            var proxy = new OutboundProxy
            {
                Name = "managed", Kind = "wireguard_warp", ManagedWgcfProfile = "profile-one",
                Protocol = "socks5", Host = "127.0.0.1", Port = 18081, Username = "user", Password = "secret",
                IsEnabled = true, TestStatus = "ok", EgressIp = "203.0.113.2"
            };
            Db.Add(proxy);
            await Db.SaveChangesAsync();
            return proxy;
        }
        public async Task<Account> AccountAsync(string suffix, OutboundProxy? proxy = null)
        {
            var account = new Account
            {
                Phone = "861380000000" + suffix, UserId = int.Parse(suffix), SessionPath = "sessions/test" + suffix,
                ApiId = 1, ApiHash = "hash", IsActive = true, UseGlobalProxy = proxy == null, Proxy = proxy
            };
            Db.Add(account);
            await Db.SaveChangesAsync();
            return account;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
    private sealed class Probe : IProxyEgressProbeService
    {
        private static Task<EgressProbeResult> Success() => Task.FromResult(new EgressProbeResult(
            true, "203.0.113.2", "SG", "Singapore", "Cloudflare", "on", 1, DateTime.UtcNow, null));
        public Task<EgressProbeResult> ProbePanelAsync(CancellationToken cancellationToken = default) => Success();
        public Task<EgressProbeResult> ProbeProxyAsync(OutboundProxy proxy, string stableAccountKey, CancellationToken cancellationToken = default) => Success();
        public Task<EgressProbeResult> ProbeProxyAsync(ProxyConnectionOptions options, bool requireWarp = false, CancellationToken cancellationToken = default) => Success();
    }
    private sealed class EmptyPool : ITelegramClientPool
    {
        public int ActiveClientCount => 0;
        public Task<Client> GetOrCreateClientAsync(int accountId, int apiId, string apiHash, string sessionPath,
            string? sessionKey = null, string? phoneNumber = null, long? userId = null) => throw new NotSupportedException();
        public Client? GetClient(int accountId) => null;
        public Task RemoveClientAsync(int accountId) => Task.CompletedTask;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }
}
