using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Services;
using WTelegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class WgcfLifecycleConcurrencyTests
{
    [Fact]
    public async Task 未启动的运行器释放时清理排队恢复占用()
    {
        TemporaryWarpClaimStore claims;
        string profile;
        await using (var f = await Fixture.CreateAsync())
        {
            claims = f.Provider.GetRequiredService<TemporaryWarpClaimStore>();
            profile = f.Profile;
            await f.Service.ResumeAsync(profile, default);
            Assert.True(claims.IsManagedProfileClaimed(profile));
        }
        Assert.False(claims.IsManagedProfileClaimed(profile));
    }

    [Fact]
    public async Task 公开停止等待工具时独占出口并拒绝首次连接租约()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var scope = f.Provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ProxyManagementService>().TestAsync((await f.ProxyAsync()).Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var toolLock = f.Field<SemaphoreSlim>("_toolLock");
        await toolLock.WaitAsync(timeout.Token);
        var stop = f.Service.SetEnabledAsync(f.Profile, false, timeout.Token);
        try
        {
            var proxyId = (await f.ProxyAsync()).Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.AcquireAsync(proxyId));
        }
        finally { toolLock.Release(); }
        await stop;
        Assert.False((await f.ProxyAsync()).IsEnabled);
    }

    [Fact]
    public async Task 公开恢复排队直到检测完成持续独占出口()
    {
        await using var f = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await f.Service.ResumeAsync(f.Profile, timeout.Token);
        var claims = f.Provider.GetRequiredService<TemporaryWarpClaimStore>();
        Assert.True(claims.IsManagedProfileClaimed(f.Profile));
        var proxyId = (await f.ProxyAsync()).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.AcquireAsync(proxyId));
        var worker = f.ProcessQueueAsync(timeout.Token);
        try
        {
            while (f.PersistedPhase() == "creating") await Task.Delay(10, timeout.Token);
            await f.WaitQueueCleanupAsync(timeout.Token);
            Assert.Equal("ready", f.PersistedPhase());
            Assert.False(claims.IsManagedProfileClaimed(f.Profile));
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }

    [Fact]
    public async Task 已完成账号绑定的内部档案允许恢复并保留原请求归属()
    {
        const string request = "telegram-panel.internal.binding.fixture.account.1";
        await using var f = await Fixture.CreateAsync(request);
        await using (var scope = f.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Accounts.Add(new Account
            {
                Phone = "8613800000000", SessionPath = "sessions/fixture.session", ApiId = 123,
                ApiHash = "0123456789abcdef0123456789abcdef", ProxyId = (await f.ProxyAsync()).Id
            });
            await db.SaveChangesAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await f.Service.ResumeAsync(f.Profile, timeout.Token);
        var worker = f.ProcessQueueAsync(timeout.Token);
        try
        {
            while (f.PersistedPhase() == "creating") await Task.Delay(10, timeout.Token);
            await f.WaitQueueCleanupAsync(timeout.Token);
            Assert.Equal("ready", f.PersistedPhase());
            Assert.True(f.PersistedTemporary());
            Assert.Equal(request, f.PersistedRequestId());
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }

    [Fact]
    public async Task 首连租约拒绝外部启停恢复检测且释放后恢复操作()
    {
        await using var f = await Fixture.CreateAsync();
        await using (var scope = f.Provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ProxyManagementService>().TestAsync((await f.ProxyAsync()).Id);
        using (var lease = await f.Service.AcquireAsync((await f.ProxyAsync()).Id))
        {
            await Assert.ThrowsAsync<ProxyInUseException>(() => f.Service.SetEnabledAsync(f.Profile, false, default));
            await Assert.ThrowsAsync<ProxyInUseException>(() => f.Service.ResumeAsync(f.Profile, default));
            await Assert.ThrowsAsync<ProxyInUseException>(() => f.Service.TestAsync(f.Profile, default));
            await Assert.ThrowsAsync<ProxyInUseException>(() => f.Service.StopUnboundAsync(lease.Proxy.Id));
            Assert.Empty(f.Commands);
        }
        await f.Service.SetEnabledAsync(f.Profile, false, default);
        Assert.Equal("stopped", f.PersistedPhase());
    }

    [Fact]
    public async Task 重启未绑定临时档案在清理收尾前拒绝重复创建()
    {
        const string request = "telegram-panel.internal.restart.unbound";
        await using var f = await Fixture.CreateAsync(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await f.RestoreEntryAsync(timeout.Token);
        Assert.True(f.PersistedStopRequested());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Service.ProvisionAsync("并发验收出口", request, true, timeout.Token));
        var worker = f.ProcessQueueAsync(timeout.Token);
        try
        {
            while (f.PersistedPhase() != "stopped") await Task.Delay(10, timeout.Token);
            await f.WaitQueueCleanupAsync(timeout.Token);
            Assert.Equal(new[] { "stop" }, f.Commands);
            Assert.False((await f.ProxyAsync()).IsEnabled);
            using var lease = await f.Service.ProvisionAsync("并发验收出口", request, true, timeout.Token);
            Assert.Equal("ok", lease.Proxy.TestStatus);
            Assert.False(f.PersistedStopRequested());
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }

    [Fact]
    public async Task 重启已绑定临时档案会继续未完成的恢复任务()
    {
        const string request = "telegram-panel.internal.restart.bound";
        await using var f = await Fixture.CreateAsync(request);
        await using (var scope = f.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Accounts.Add(new Account
            {
                Phone = "8613800000001", SessionPath = "sessions/restart.session", ApiId = 123,
                ApiHash = "0123456789abcdef0123456789abcdef", ProxyId = (await f.ProxyAsync()).Id
            });
            await db.SaveChangesAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await f.RestoreEntryAsync(timeout.Token, "creating");
        var worker = f.ProcessQueueAsync(timeout.Token);
        try
        {
            while (f.PersistedPhase() != "ready") await Task.Delay(10, timeout.Token);
            await f.WaitQueueCleanupAsync(timeout.Token);
            Assert.Equal(new[] { "provision" }, f.Commands);
            Assert.True(f.PersistedTemporary());
            Assert.Equal(request, f.PersistedRequestId());
            Assert.True((await f.ProxyAsync()).IsEnabled);
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }

    [Fact]
    public async Task 未接受条款不创建不注册且成功首连返回检测通过的同一出口()
    {
        var request = "telegram-panel.internal.login.123";
        await using var f = await Fixture.CreateAsync(request);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ProvisionAsync("并发验收出口", request, false));
        Assert.Empty(f.Commands);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker = f.ProcessQueueAsync(timeout.Token);
        try
        {
            using var lease = await f.Service.ProvisionAsync("并发验收出口", request, true, timeout.Token);
            Assert.Equal((await f.ProxyAsync()).Id, lease.Proxy.Id);
            Assert.Equal("ok", lease.Proxy.TestStatus);
            Assert.NotNull(lease.Proxy.EgressIp);
            await f.Service.StopUnboundAsync(lease.Proxy.Id, lease.ClaimToken, timeout.Token);
            Assert.Equal("stopped", f.PersistedPhase());
            Assert.False((await f.ProxyAsync()).IsEnabled);
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }

    [Fact]
    public async Task 取消排队的首连会停止并保留材料且后到任务不重新启动()
    {
        var request = "telegram-panel.internal.import.fixture";
        await using var f = await Fixture.CreateAsync(request);
        using var cancellation = new CancellationTokenSource();
        var provision = f.Service.ProvisionAsync("并发验收出口", request, true, cancellation.Token);
        while (f.PersistedPhase() != "creating") await Task.Delay(10);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provision);
        Assert.Equal("stopped", f.PersistedPhase());
        // 停止任务仍在队列中时，不能因首连租约已释放而重复创建同一临时档案。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Service.ProvisionAsync("并发验收出口", request, true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker = f.ProcessQueueAsync(timeout.Token);
        try
        {
            while (f.Commands.Count < 2) await Task.Delay(10, timeout.Token);
            Assert.DoesNotContain("provision", f.Commands);
            Assert.False((await f.ProxyAsync()).IsEnabled);
        }
        finally
        {
            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }

    [Fact]
    public async Task 重新启用已停止档案会清除旧停止意图()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.SetEnabledAsync(f.Profile, false, default);
        Assert.True(f.PersistedStopRequested());
        await f.Service.SetEnabledAsync(f.Profile, true, default);
        Assert.False(f.PersistedStopRequested());
    }

    [Fact]
    public async Task 停止等待工具时恢复请求必须等待并保留最终创建状态()
    {
        await using var f = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var toolLock = f.Field<SemaphoreSlim>("_toolLock");
        await toolLock.WaitAsync(timeout.Token);
        Task<WgcfProfileDto> stop;
        Task<WgcfProfileDto> resume;
        try
        {
            stop = f.Service.SetEnabledAsync(f.Profile, false, timeout.Token);
            Assert.Equal(0, f.Field<SemaphoreSlim>("_stateLock").CurrentCount);
            resume = f.Service.ResumeAsync(f.Profile, timeout.Token);
            Assert.False(resume.IsCompleted);
        }
        finally { toolLock.Release(); }

        await Task.WhenAll(stop, resume);
        Assert.Equal("creating", (await f.Service.ListAsync(timeout.Token)).Profiles.Single().Phase);
        Assert.Equal("creating", f.PersistedPhase());
        Assert.Equal(new[] { "stop" }, f.Commands);
    }

    [Fact]
    public async Task 停止获取状态锁后必须重新读取已提交的恢复状态()
    {
        await using var f = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stateLock = f.Field<SemaphoreSlim>("_stateLock");
        await stateLock.WaitAsync(timeout.Token);
        Task<WgcfProfileDto> stop;
        try
        {
            stop = f.Service.SetEnabledAsync(f.Profile, false, timeout.Token);
            Assert.False(stop.IsCompleted);
            // 模拟先取得状态锁的恢复请求提交 creating，再允许停止请求读取。
            f.PersistPhase("creating");
        }
        finally { stateLock.Release(); }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => stop);
        Assert.Contains("正在创建", error.Message);
        Assert.Equal("creating", f.PersistedPhase());
        Assert.Empty(f.Commands);
        Assert.True((await f.ProxyAsync()).IsEnabled);
    }

    [Fact]
    public async Task 取消等待工具的停止请求会释放状态锁且不改写档案()
    {
        await using var f = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stopCancellation = new CancellationTokenSource();
        var toolLock = f.Field<SemaphoreSlim>("_toolLock");
        await toolLock.WaitAsync(timeout.Token);
        try
        {
            var stop = f.Service.SetEnabledAsync(f.Profile, false, stopCancellation.Token);
            Assert.Equal(0, f.Field<SemaphoreSlim>("_stateLock").CurrentCount);
            stopCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.Equal(1, f.Field<SemaphoreSlim>("_stateLock").CurrentCount);
            Assert.Equal("ready", f.PersistedPhase());
            await f.Service.ResumeAsync(f.Profile, timeout.Token);
            Assert.Equal("creating", f.PersistedPhase());
            Assert.Empty(f.Commands);
        }
        finally { toolLock.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 恢复尊重注册重试保留的启停状态并在需要时显式启动(bool desired)
    {
        await using var f = await Fixture.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        f.WriteRuntime(desired);
        f.PersistPhase("stopped");
        await using (var scope = f.Provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProxyManagementService>()
                .SetManagedWgcfEnabledAsync(f.Profile, false, timeout.Token);
        }
        await f.Service.ResumeAsync(f.Profile, timeout.Token);
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var worker = f.ProcessQueueAsync(workerCancellation.Token);
        try
        {
            WgcfProfileDto state;
            do
            {
                state = (await f.Service.ListAsync(timeout.Token)).Profiles.Single();
                if (state.Phase != "creating") break;
                await Task.Delay(10, timeout.Token);
            } while (true);

            Assert.Equal("ready", state.Phase);
            Assert.True(state.Desired);
            Assert.Equal("listening", state.Runtime);
            Assert.Equal("ok", state.TestStatus);
            Assert.True((await f.ProxyAsync()).IsEnabled);
            Assert.Equal(desired ? new[] { "provision" } : new[] { "provision", "start" }, f.Commands);
        }
        finally
        {
            workerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "wgcf-lifecycle-" + Guid.NewGuid().ToString("N"));
        public string Profile { get; private set; } = string.Empty;
        public ServiceProvider Provider { get; private set; } = null!;
        public WgcfWarpService Service { get; private set; } = null!;
        public List<string> Commands { get; } = new();
        private string Root => Path.Combine(_directory, "wgcf-warp");

        public static async Task<Fixture> CreateAsync(string? internalRequest = null)
        {
            var f = new Fixture();
            Directory.CreateDirectory(Path.Combine(f.Root, "web-requests"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = f._directory
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<TemporaryWarpClaimStore>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(
                $"Data Source={Path.Combine(f._directory, "test.db")};Pooling=False"));
            services.AddScoped(provider =>
            {
                var db = provider.GetRequiredService<AppDbContext>();
                var probe = new Probe();
                return new ProxyManagementService(db, new EmptyPool(), probe,
                    new WarpContainerManager(db, config, probe, NullLogger<WarpContainerManager>.Instance),
                    NullLogger<ProxyManagementService>.Instance, config,
                    provider.GetRequiredService<TemporaryWarpClaimStore>());
            });
            f.Provider = services.BuildServiceProvider();
            await using (var scope = f.Provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            f.Service = new WgcfWarpService(f.Provider.GetRequiredService<IServiceScopeFactory>(), config,
                new TestEnvironment { ContentRootPath = f._directory }, NullLogger<WgcfWarpService>.Instance,
                () => true, (args, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var command = args.First();
                    f.Commands.Add(command);
                    // provision 复用既有材料，不修改启停意图；start/stop 模拟运行器确认。
                    if (command is "start" or "stop") f.WriteRuntime(command == "start");
                    return Task.CompletedTask;
                });
            typeof(WgcfWarpService).GetField("_supervisorReady", PrivateInstance)!.SetValue(f.Service, true);
            var id = internalRequest == null ? Guid.NewGuid().ToString() :
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(internalRequest)).AsSpan(0, 16), bigEndian: true).ToString();
            f.Profile = (await f.Service.CreateAsync(id, "并发验收出口", true, default)).Profile;
            Assert.True(f.Field<Channel<string>>("_queue").Reader.TryRead(out _));
            Directory.CreateDirectory(Path.Combine(f.Root, f.Profile));
            File.WriteAllText(Path.Combine(f.Root, f.Profile, "proxy-auth.json"), "{\"username\":\"fixture\",\"password\":\"fixture-secret\"}");
            f.WriteRuntime(true);
            f.PersistPhase("ready");
            if (internalRequest != null)
            {
                var entryType = typeof(WgcfWarpService).GetNestedType("Entry", BindingFlags.NonPublic)!;
                var entry = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
                {
                    Profile = f.Profile, Name = "并发验收出口", Phase = "ready", Temporary = true, RequestId = internalRequest
                }), entryType);
                typeof(WgcfWarpService).GetMethod("Persist", PrivateInstance)!.Invoke(f.Service, new[] { entry });
            }
            await using (var scope = f.Provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<ProxyManagementService>()
                    .CreateManagedWgcfProxyAsync(f.Profile, "并发验收出口", 18081, "fixture", "fixture-secret");
            return f;
        }

        public T Field<T>(string name) => (T)typeof(WgcfWarpService).GetField(name, PrivateInstance)!.GetValue(Service)!;

        public void PersistPhase(string phase)
        {
            var entryType = typeof(WgcfWarpService).GetNestedType("Entry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType, Profile, "并发验收出口", phase, null)!;
            typeof(WgcfWarpService).GetMethod("Persist", PrivateInstance)!.Invoke(Service, new[] { entry });
        }

        public string PersistedPhase()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "web-requests", Profile + ".json")));
            return json.RootElement.GetProperty("Phase").GetString()!;
        }

        public bool PersistedTemporary()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "web-requests", Profile + ".json")));
            return json.RootElement.GetProperty("Temporary").GetBoolean();
        }

        public string? PersistedRequestId()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "web-requests", Profile + ".json")));
            return json.RootElement.GetProperty("RequestId").GetString();
        }

        public bool PersistedStopRequested()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "web-requests", Profile + ".json")));
            return json.RootElement.GetProperty("StopRequested").GetBoolean();
        }

        public void WriteRuntime(bool desired)
        {
            File.WriteAllText(Path.Combine(Root, Profile, "meta.json"), JsonSerializer.Serialize(new
            {
                registered = true, generated = true, desired, port = 18081, revision = "fixture-revision"
            }));
            File.WriteAllText(Path.Combine(Root, Profile, "runtime.json"), JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), revision = "fixture-revision",
                state = desired ? "listening" : "stopped"
            }));
        }

        public Task ProcessQueueAsync(CancellationToken ct) =>
            (Task)typeof(WgcfWarpService).GetMethod("ProcessQueueAsync", PrivateInstance)!.Invoke(Service, new object[] { ct })!;

        public async Task WaitQueueCleanupAsync(CancellationToken ct)
        {
            // 终态持久化发生在 finally 之前；工具锁释放才代表排队标记和租约已清理。
            var toolLock = Field<SemaphoreSlim>("_toolLock");
            await toolLock.WaitAsync(ct);
            toolLock.Release();
        }

        public Task RestoreEntryAsync(CancellationToken ct, string? phase = null)
        {
            var json = File.ReadAllText(Path.Combine(Root, "web-requests", Profile + ".json"));
            if (phase != null) json = json.Replace("\"Phase\":\"ready\"", "\"Phase\":\"" + phase + "\"");
            var entryType = typeof(WgcfWarpService).GetNestedType("Entry", BindingFlags.NonPublic)!;
            var entry = JsonSerializer.Deserialize(json, entryType)!;
            typeof(WgcfWarpService).GetMethod("Persist", PrivateInstance)!.Invoke(Service, new[] { entry });
            return (Task)typeof(WgcfWarpService).GetMethod("RestoreEntryAsync", PrivateInstance)!
                .Invoke(Service, new[] { entry, (object)ct })!;
        }

        public async Task<OutboundProxy> ProxyAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies.AsNoTracking().SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Provider.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
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

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "TelegramPanel.Web.Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
