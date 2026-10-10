using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Api;
using TelegramPanel.Web.Services;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ProxyApiValidationTests
{
    [Fact]
    public async Task 批量代理接口空选择均返回四百而不是抛出五百()
    {
        var warpUsageGuard = new AccountLoginProxyStateStore();
        var temporaryWarpClaims = new TemporaryWarpClaimStore();
        var enabled = await ProxyApiEndpoints.BatchSetEnabledAsync(
            new ProxyBatchEnabledRequestDto(Array.Empty<int>(), true),
            null!,
            warpUsageGuard,
            temporaryWarpClaims,
            CancellationToken.None);
        var tested = await ProxyApiEndpoints.BatchTestAsync(
            new ProxyBatchIdsRequestDto(Array.Empty<int>()),
            null!,
            CancellationToken.None);
        var deleted = await ProxyApiEndpoints.BatchDeleteAsync(
            new ProxyBatchIdsRequestDto(Array.Empty<int>()),
            null!,
            warpUsageGuard,
            temporaryWarpClaims,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(enabled));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(tested));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(deleted));
    }

    [Fact]
    public async Task 代理列表非法使用状态返回四百()
    {
        await using var fixture = await WarpProxyFixture.CreateAsync();
        var result = await ProxyApiEndpoints.ListAsync(
            "invalid",
            null,
            fixture.Service,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(result));
    }

    [Fact]
    public async Task 登录首连冻结WARP时批量停用和删除均失败且数据库不变()
    {
        await using var fixture = await WarpProxyFixture.CreateAsync();
        using var usageLease = fixture.WarpUsageGuard.TryAcquireUsage(fixture.ProxyId);
        Assert.NotNull(usageLease);

        await AssertBatchMutationsBlockedAsync(fixture);
    }

    [Fact]
    public async Task 导入声明WARP时批量停用和删除均失败且数据库不变()
    {
        await using var fixture = await WarpProxyFixture.CreateAsync();
        using var requestClaim = fixture.TemporaryWarpClaims.ClaimRequest(fixture.RequestId);

        await AssertBatchMutationsBlockedAsync(fixture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 导入代理策略为空时不会隐式选择全局代理(string? strategy)
    {
        Assert.Null(PanelAdminApiEndpoints.ParseImportProxyBinding(strategy, "1"));
    }

    [Theory]
    [InlineData("proxy_per_account")]
    [InlineData(" PROXY_PER_ACCOUNT ")]
    public void 逐账号批量代理不能被非Zip导入入口解析(string strategy)
    {
        Assert.Null(PanelAdminApiEndpoints.ParseImportProxyBinding(strategy, "1"));
    }

    [Fact]
    public void 自动WARP池策略解析为无固定代理引用()
    {
        var binding = PanelAdminApiEndpoints.ParseImportProxyBinding("warp_pool", null);

        Assert.NotNull(binding);
        Assert.Equal("warp_pool", binding!.Strategy);
        Assert.Null(binding.ProxyId);
    }

    private static async Task AssertBatchMutationsBlockedAsync(WarpProxyFixture fixture)
    {
        var expected = await fixture.ReadSnapshotAsync();

        var disabled = await ProxyApiEndpoints.BatchSetEnabledAsync(
            new ProxyBatchEnabledRequestDto(new[] { fixture.ProxyId }, false),
            fixture.Service,
            fixture.WarpUsageGuard,
            fixture.TemporaryWarpClaims,
            CancellationToken.None);
        AssertBatchFailure(disabled, fixture.ProxyId);
        Assert.Equal(expected, await fixture.ReadSnapshotAsync());

        var deleted = await ProxyApiEndpoints.BatchDeleteAsync(
            new ProxyBatchIdsRequestDto(new[] { fixture.ProxyId }),
            fixture.Service,
            fixture.WarpUsageGuard,
            fixture.TemporaryWarpClaims,
            CancellationToken.None);
        AssertBatchFailure(deleted, fixture.ProxyId);
        Assert.Equal(expected, await fixture.ReadSnapshotAsync());
    }

    private static void AssertBatchFailure(IResult result, int proxyId)
    {
        Assert.Equal(StatusCodes.Status200OK, StatusCode(result));
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var response = Assert.IsType<ProxyBatchResultDto>(valueResult.Value);
        Assert.Equal(0, response.Success);
        Assert.Equal(1, response.Failed);
        var item = Assert.Single(response.Items);
        Assert.Equal(proxyId, item.ProxyId);
        Assert.False(item.Success);
        Assert.Contains("WARP", item.Error);
        Assert.Contains("首次连接", item.Error);
    }

    private static int? StatusCode(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private sealed record WarpDatabaseSnapshot(
        int ProxyCount,
        string ProxyName,
        bool ProxyEnabled,
        string ProxyTestStatus,
        string? ProxyLastError,
        DateTime ProxyUpdatedAtUtc,
        int ProfileCount,
        string ProfileStatus,
        bool ProfileDesiredEnabled,
        string? ProfileContainerId,
        DateTime ProfileUpdatedAtUtc);

    private sealed class WarpProxyFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private WarpProxyFixture(
            SqliteConnection connection,
            AppDbContext db,
            ProxyManagementService service,
            AccountLoginProxyStateStore warpUsageGuard,
            TemporaryWarpClaimStore temporaryWarpClaims,
            int proxyId,
            string requestId)
        {
            _connection = connection;
            Db = db;
            Service = service;
            WarpUsageGuard = warpUsageGuard;
            TemporaryWarpClaims = temporaryWarpClaims;
            ProxyId = proxyId;
            RequestId = requestId;
        }

        public AppDbContext Db { get; }
        public ProxyManagementService Service { get; }
        public AccountLoginProxyStateStore WarpUsageGuard { get; }
        public TemporaryWarpClaimStore TemporaryWarpClaims { get; }
        public int ProxyId { get; }
        public string RequestId { get; }

        public static async Task<WarpProxyFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            const string requestId = "import-account:proxy-api-validation";
            var proxy = new OutboundProxy
            {
                Name = "WARP 回归测试代理",
                Kind = OutboundProxyKinds.Warp,
                Protocol = OutboundProxyProtocols.Http,
                Host = "warp-proxy-api-validation",
                Port = 1080,
                IsEnabled = true,
                TestStatus = "ok",
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            var profile = new WarpProfile
            {
                ProfileId = "proxy-api-validation",
                RequestId = requestId,
                ContainerName = "telegram-panel-warp-proxy-api-validation",
                ContainerId = "container-proxy-api-validation",
                VolumeName = "telegram-panel-warp-data-proxy-api-validation",
                HostPort = 42080,
                Status = "active",
                DesiredEnabled = true,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                Proxy = proxy
            };
            proxy.WarpProfile = profile;
            db.OutboundProxies.Add(proxy);
            db.WarpProfiles.Add(profile);
            await db.SaveChangesAsync();

            var configuration = new ConfigurationBuilder()
                .Build();
            var probe = new ProxyEgressProbeService();
            var warpUsageGuard = new AccountLoginProxyStateStore();
            var temporaryWarpClaims = new TemporaryWarpClaimStore();
            var service = new ProxyManagementService(
                db,
                new NoopClientPool(),
                probe,
                NullLogger<ProxyManagementService>.Instance,
                configuration,
                temporaryWarpClaims,
                warpUsageGuard);

            return new WarpProxyFixture(
                connection,
                db,
                service,
                warpUsageGuard,
                temporaryWarpClaims,
                proxy.Id,
                requestId);
        }

        public async Task<WarpDatabaseSnapshot> ReadSnapshotAsync()
        {
            Db.ChangeTracker.Clear();
            var proxy = await Db.OutboundProxies.AsNoTracking().SingleAsync();
            var profile = await Db.WarpProfiles.AsNoTracking().SingleAsync();
            return new WarpDatabaseSnapshot(
                await Db.OutboundProxies.CountAsync(),
                proxy.Name,
                proxy.IsEnabled,
                proxy.TestStatus,
                proxy.LastError,
                proxy.UpdatedAtUtc,
                await Db.WarpProfiles.CountAsync(),
                profile.Status,
                profile.DesiredEnabled,
                profile.ContainerId,
                profile.UpdatedAtUtc);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

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

}
