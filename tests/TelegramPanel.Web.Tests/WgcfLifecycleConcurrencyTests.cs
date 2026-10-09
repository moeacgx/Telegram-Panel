using System.Reflection;
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

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(Path.Combine(f.Root, "web-requests"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = f._directory
            }).Build();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(
                $"Data Source={Path.Combine(f._directory, "test.db")};Pooling=False"));
            services.AddScoped(provider =>
            {
                var db = provider.GetRequiredService<AppDbContext>();
                var probe = new Probe();
                return new ProxyManagementService(db, new EmptyPool(), probe,
                    new WarpContainerManager(db, config, probe, NullLogger<WarpContainerManager>.Instance),
                    NullLogger<ProxyManagementService>.Instance, config);
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
            f.Profile = (await f.Service.CreateAsync(Guid.NewGuid().ToString(), "并发验收出口", true, default)).Profile;
            Assert.True(f.Field<Channel<string>>("_queue").Reader.TryRead(out _));
            Directory.CreateDirectory(Path.Combine(f.Root, f.Profile));
            File.WriteAllText(Path.Combine(f.Root, f.Profile, "proxy-auth.json"), "{\"username\":\"fixture\",\"password\":\"fixture-secret\"}");
            f.WriteRuntime(true);
            f.PersistPhase("ready");
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
