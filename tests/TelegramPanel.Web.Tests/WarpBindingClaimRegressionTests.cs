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

public sealed class WarpBindingClaimRegressionTests
{
    [Fact]
    public async Task 逐账号创建轻量WARP会持有独占直到失败停止结束且保留档案()
    {
        await using var f = await Fixture.CreateAsync();
        bool? claimedDuringBinding = null;
        bool? claimedDuringStop = null;
        f.Pool.OnRemove = async () =>
        {
            var proxy = await f.Db.OutboundProxies.SingleAsync();
            claimedDuringBinding = f.Claims.IsManagedProfileClaimed(proxy.ManagedWgcfProfile!);
            throw new InvalidOperationException("模拟旧客户端无法断开");
        };
        f.Provisioner.OnStop = proxy =>
        {
            claimedDuringStop = f.Claims.IsManagedProfileClaimed(proxy.ManagedWgcfProfile!);
            return Task.CompletedTask;
        };
        var result = await f.Service.BindAccountsAsync(new[] { f.Account.Id },
            new("warp_per_account", AcceptWarpTerms: true));
        Assert.Equal(1, result.Failed);
        Assert.True(claimedDuringBinding);
        Assert.True(claimedDuringStop);
        var retained = await f.Db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.False(retained.IsEnabled);
        Assert.False(f.Claims.IsManagedProfileClaimed(retained.ManagedWgcfProfile!));
        Assert.Null((await f.Db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
        Assert.Empty(await f.Db.WarpProfiles.ToListAsync());
    }

    [Fact]
    public async Task 未接受条款和不可用服务均拒绝轻量创建且不创建Docker资源()
    {
        await using var f = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.BindAccountsAsync(new[] { f.Account.Id }, new("warp_per_account")));
        Assert.Equal(0, f.Provisioner.ProvisionCalls);
        Assert.Empty(await f.Db.OutboundProxies.ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CreateWarpAsync("test", "request", protocol: "http", acceptTerms: true));
        Assert.Equal(0, f.Provisioner.ProvisionCalls);
    }

    [Fact]
    public async Task 首连租约阻止外部抢绑定和启停但允许所有者绑定()
    {
        await using var f = await Fixture.CreateAsync();
        using var lease = await f.Service.CreateManagedWarpLeaseAsync("managed", Guid.NewGuid().ToString("D"), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.BindAccountsAsync(new[] { f.Account.Id }, new("existing", lease.Proxy.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetManagedWgcfEnabledAsync(lease.Proxy.ManagedWgcfProfile!, false));
        Assert.Null((await f.Db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
        var result = await f.Service.BindAccountsAsync(new[] { f.Account.Id }, new("existing", lease.Proxy.Id),
            managedWarpClaimToken: lease.ClaimToken);
        Assert.Equal(1, result.Success);
        Assert.Equal(lease.Proxy.Id, (await f.Db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
    }

    [Fact]
    public async Task 相同操作重试派生相同逐账号请求且不会产生第二份档案()
    {
        await using var f = await Fixture.CreateAsync();
        var input = new AccountProxyBindingInput("warp_per_account", AcceptWarpTerms: true, WarpRequestId: Guid.NewGuid().ToString("D"));
        Assert.Equal(1, (await f.Service.BindAccountsAsync(new[] { f.Account.Id }, input)).Success);
        var original = await f.Db.OutboundProxies.AsNoTracking().SingleAsync();
        Assert.Equal(1, (await f.Service.BindAccountsAsync(new[] { f.Account.Id }, input)).Success);
        Assert.Equal(original.Id, (await f.Db.OutboundProxies.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task 创建期间旧全局模式改变时CAS拒绝切换并停止新出口()
    {
        await using var f = await Fixture.CreateAsync();
        f.Provisioner.OnProvision = async _ =>
        {
            f.Account.UseGlobalProxy = !f.Account.UseGlobalProxy;
            await f.Db.SaveChangesAsync();
        };
        var result = await f.Service.BindAccountsAsync(new[] { f.Account.Id }, new("warp_per_account", AcceptWarpTerms: true));
        Assert.Equal(1, result.Failed);
        Assert.Null((await f.Db.Accounts.AsNoTracking().SingleAsync()).ProxyId);
        Assert.Equal(1, f.Provisioner.StopCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }
        public Account Account { get; }
        public TemporaryWarpClaimStore Claims { get; } = new();
        public RecordingPool Pool { get; } = new();
        public FakeManagedWarpProvisioner Provisioner { get; }
        public ProxyManagementService Service { get; }
        private Fixture(SqliteConnection connection, AppDbContext db, Account account)
        {
            _connection = connection;
            Db = db;
            Account = account;
            Provisioner = new(db, Claims);
            var configuration = new ConfigurationBuilder().Build();
            var probe = new ProxyEgressProbeService();
            Service = new(db, Pool, probe, new WarpContainerManager(db, configuration, probe, NullLogger<WarpContainerManager>.Instance),
                NullLogger<ProxyManagementService>.Instance, configuration, Claims, managedWarpProvisioner: Provisioner);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var account = new Account { Phone = "8613800000888", UserId = 888, SessionPath = "sessions/test.session", ApiId = 1, ApiHash = "hash", IsActive = true };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return new(connection, db, account);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class RecordingPool : ITelegramClientPool
    {
        public Func<Task>? OnRemove { get; set; }
        public int ActiveClientCount => 0;
        public Task<Client> GetOrCreateClientAsync(int accountId, int apiId, string apiHash, string sessionPath,
            string? sessionKey = null, string? phoneNumber = null, long? userId = null) => throw new NotSupportedException();
        public Client? GetClient(int accountId) => null;
        public Task RemoveClientAsync(int accountId) => Task.CompletedTask;
        public Task RemoveClientStrictAsync(int accountId) => OnRemove?.Invoke() ?? Task.CompletedTask;
        public Task RemoveAllClientsAsync() => Task.CompletedTask;
        public bool IsClientConnected(int accountId) => false;
    }
}
