using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Data;
using OutboundProxy = TelegramPanel.Data.Entities.OutboundProxy;

namespace TelegramPanel.Web.Services;

public sealed record WgcfProfileDto(string Profile, string Name, string Phase, bool Registered,
    bool Generated, bool Desired, string Runtime, int? ProxyId, int AccountCount,
    string TestStatus, string? EgressIp, string? Error);
public sealed record WgcfEnvironmentDto(bool Available, string? Reason, IReadOnlyList<WgcfProfileDto> Profiles);

/// <summary>在主容器内托管轻量出口；注册材料仅保存在私有持久目录。</summary>
public sealed class WgcfWarpService : BackgroundService, IManagedWarpProvisioner
{
    private sealed record Entry(string Profile, string Name, string Phase, string? Error = null)
    {
        public string? RequestId { get; init; }
        public bool Temporary { get; init; }
        public bool StopRequested { get; init; }
    }
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WgcfWarpService> _logger;
    private readonly string _root;
    private readonly string _script = Path.Combine(AppContext.BaseDirectory, "wgcf-warp", "warpctl.py");
    private readonly Func<bool> _hasDependencies;
    private readonly Func<IEnumerable<string>, CancellationToken, Task>? _runCommand;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _toolLock = new(1, 1);
    private readonly object _persistLock = new();
    private volatile bool _supervisorReady;
    private readonly TemporaryWarpClaimStore _claims;
    private readonly ConcurrentDictionary<string, string> _queueTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingTemporary = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TemporaryWarpClaimStore.ManagedProfileClaim> _queuedClaims = new(StringComparer.Ordinal);

    public WgcfWarpService(IServiceScopeFactory scopes, IConfiguration configuration,
        IWebHostEnvironment environment, ILogger<WgcfWarpService> logger)
        : this(scopes, configuration, environment, logger, null, null) { }

    internal WgcfWarpService(IServiceScopeFactory scopes, IConfiguration configuration,
        IWebHostEnvironment environment, ILogger<WgcfWarpService> logger,
        Func<bool>? hasDependencies, Func<IEnumerable<string>, CancellationToken, Task>? runCommand)
    {
        _scopes = scopes;
        _logger = logger;
        _root = Path.Combine(StoragePathResolver.ResolveWritableRoot(configuration, environment), "wgcf-warp");
        _hasDependencies = hasDependencies ?? (() => OperatingSystem.IsLinux() && File.Exists(_script)
            && File.Exists("/usr/bin/python3") && File.Exists("/usr/local/bin/wgcf") && File.Exists("/usr/local/bin/wireproxy"));
        _runCommand = runCommand;
        using var scope = scopes.CreateScope();
        _claims = scope.ServiceProvider.GetService<TemporaryWarpClaimStore>() ?? new TemporaryWarpClaimStore();
    }

    public Task<ManagedWarpAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ManagedWarpAvailability(HasDependencies && _supervisorReady,
            !HasDependencies ? "当前运行环境缺少轻量 WARP 依赖，请更新 Docker 镜像" : !_supervisorReady ? "运行器正在启动或恢复" : null));

    public async Task<ManagedWarpProxyLease> ProvisionAsync(string name, string requestId, bool acceptTerms,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (!acceptTerms) throw new ArgumentException("请先阅读并接受 Cloudflare WARP 条款");
        if (!requestId.StartsWith("telegram-panel.internal.", StringComparison.Ordinal))
            throw new ArgumentException("首次连接创建必须使用内部操作标识");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
        var profile = "web-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        name = ValidateName(name);
        var claim = _claims.ClaimManagedProfile(profile);
        var queued = false;
        try
        {
            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                if (_entries.TryGetValue(profile, out var old) && (!old.Temporary || old.RequestId != requestId))
                    throw new InvalidOperationException("出口操作归属不一致");
                if (_pendingTemporary.ContainsKey(profile))
                    throw new InvalidOperationException("原出口创建或取消仍在收尾，请稍后重试");
                if (old == null && _entries.Count >= 100) throw new InvalidOperationException("轻量出口档案已达到 100 份上限");
                await EnsureUnboundAsync(profile, cancellationToken);
                _queueTokens[profile] = claim.Token;
                _pendingTemporary[profile] = 0;
                Persist(new Entry(profile, old?.Name ?? name, "creating") { Temporary = true, RequestId = requestId });
                _queue.Writer.TryWrite(profile);
                queued = true;
            }
            finally { _stateLock.Release(); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var entry = GetEntry(profile);
                if (entry.Phase == "failed") throw new InvalidOperationException(entry.Error);
                if (entry.Phase == "ready")
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var proxy = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies
                        .AsNoTracking().SingleAsync(x => x.ManagedWgcfProfile == profile, timeout.Token);
                    if (proxy.IsEnabled && proxy.TestStatus == "ok" && !string.IsNullOrWhiteSpace(proxy.EgressIp)
                        && ToDto(entry, proxy).Runtime == "listening")
                        return new ManagedWarpProxyLease(proxy, claim.Token, claim);
                    throw new InvalidOperationException("轻量出口检测未通过，不能开始首次连接");
                }
                await Task.Delay(200, timeout.Token);
            }
        }
        catch
        {
            // 队列任务可能仍在运行，先保存停止意图，最终完成也不能遗留活动出口。
            if (queued)
            {
                try { await RequestStopAsync(profile, claim.Token); }
                catch (Exception) { _logger.LogWarning("临时轻量出口停止待重试，材料和停止意图已保留"); }
            }
            claim.Dispose();
            throw;
        }
    }

    public async Task<ManagedWarpProxyLease> AcquireAsync(int proxyId, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        await using var scope = _scopes.CreateAsyncScope();
        var proxy = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == proxyId, cancellationToken) ?? throw new KeyNotFoundException("代理不存在");
        var profile = proxy.ManagedWgcfProfile ?? throw new ArgumentException("该代理不是轻量 WARP");
        var claim = _claims.ClaimManagedProfile(profile);
        try
        {
            await EnsureUnboundAsync(profile, cancellationToken);
            proxy = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies.AsNoTracking()
                .SingleAsync(x => x.Id == proxyId, cancellationToken);
            var entry = GetEntry(profile);
            if (entry.Temporary) throw new InvalidOperationException("临时出口只能通过原创建流程恢复");
            if (entry.Phase != "ready" || ToDto(entry, proxy).Runtime != "listening" || !proxy.IsEnabled
                || proxy.TestStatus != "ok" || string.IsNullOrWhiteSpace(proxy.EgressIp))
                throw new InvalidOperationException("轻量出口尚未就绪");
            return new ManagedWarpProxyLease(proxy, claim.Token, claim);
        }
        catch { claim.Dispose(); throw; }
    }

    public async Task StopUnboundAsync(int proxyId, string? claimToken = null, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var profile = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies.AsNoTracking()
            .Where(x => x.Id == proxyId).Select(x => x.ManagedWgcfProfile).SingleOrDefaultAsync(cancellationToken);
        if (profile == null) return;
        RejectForeignClaim(profile, claimToken);
        using var claim = _claims.OwnsManagedProfile(profile, claimToken)
            ? null : _claims.ClaimManagedProfile(profile);
        await EnsureUnboundAsync(profile, cancellationToken);
        await RequestStopAsync(profile, claim?.Token ?? claimToken);
    }

    private void RejectForeignClaim(string profile, string? token = null)
    {
        if (_claims.IsManagedProfileClaimed(profile) && !_claims.OwnsManagedProfile(profile, token))
            throw new ProxyInUseException("轻量 WARP 正被首次连接或绑定流程占用");
    }

    private async Task EnsureUnboundAsync(string profile, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies
            .AnyAsync(x => x.ManagedWgcfProfile == profile && x.Accounts.Any(), ct))
            throw new ProxyInUseException("轻量出口已绑定账号");
    }

    private async Task RequestStopAsync(string profile, string? token)
    {
        await _stateLock.WaitAsync();
        try
        {
            if (!_entries.TryGetValue(profile, out var entry)) return;
            Persist(entry with { StopRequested = true });
        }
        finally { _stateLock.Release(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        try
        {
            await _toolLock.WaitAsync(timeout.Token);
            try { await StopProfileAsync(profile, token, timeout.Token); }
            finally { _toolLock.Release(); }
        }
        catch (Exception)
        {
            _logger.LogWarning("临时轻量出口停止尚未确认，运行器将继续按停止意图收尾");
            throw new InvalidOperationException("轻量出口停止尚未确认，请保留租约并重试清理");
        }
    }

    private async Task StopProfileAsync(string profile, string? token, CancellationToken ct)
    {
        await EnsureUnboundAsync(profile, ct);
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.OutboundProxies.AnyAsync(x => x.ManagedWgcfProfile == profile, ct))
            await scope.ServiceProvider.GetRequiredService<ProxyManagementService>().SetManagedWgcfEnabledAsync(profile, false, ct, token);
        if (Directory.Exists(Path.Combine(_root, profile)))
        {
            await CommandAsync(new[] { "stop", profile }, ct);
            await WaitRuntimeAsync(profile, "stopped", ct);
        }
        Persist(GetEntry(profile) with { Phase = "stopped", StopRequested = true, Error = null });
    }

    private bool HasDependencies => _hasDependencies();

    public async Task<WgcfEnvironmentDto> ListAsync(CancellationToken ct)
    {
        var profiles = new List<WgcfProfileDto>();
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var proxies = await db.OutboundProxies.AsNoTracking().Include(x => x.Accounts)
            .Where(x => x.ManagedWgcfProfile != null).ToListAsync(ct);
        foreach (var entry in _entries.Values.OrderBy(x => x.Name))
            profiles.Add(ToDto(entry, proxies.FirstOrDefault(x => x.ManagedWgcfProfile == entry.Profile)));
        return new(HasDependencies && _supervisorReady,
            !HasDependencies ? "当前运行环境缺少轻量 WARP 依赖，请更新 Docker 镜像" : !_supervisorReady ? "运行器正在启动或恢复" : null,
            profiles);
    }

    public async Task<WgcfProfileDto> CreateAsync(string requestId, string name, bool acceptTerms, CancellationToken ct)
    {
        EnsureAvailable();
        var profile = ProfileFor(requestId);
        name = ValidateName(name);
        if (!acceptTerms) throw new ArgumentException("请先阅读并接受 Cloudflare WARP 条款");
        await _stateLock.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(profile, out var existing))
            {
                if (existing.Temporary) throw new InvalidOperationException("该档案属于账号首次连接流程");
                if (existing.Name != name) throw new InvalidOperationException("该请求标识已对应其它名称，请刷新后核对");
                return (await ListAsync(ct)).Profiles.Single(x => x.Profile == profile);
            }
            if (_entries.Count >= 100) throw new InvalidOperationException("轻量出口档案已达到 100 份上限，请先核对现有档案");
            var entry = new Entry(profile, name, "creating");
            Persist(entry);
            _queue.Writer.TryWrite(profile);
            return ToDto(entry, null);
        }
        finally { _stateLock.Release(); }
    }

    public async Task<WgcfProfileDto> ResumeAsync(string profile, CancellationToken ct)
    {
        EnsureAvailable();
        await _stateLock.WaitAsync(ct);
        try
        {
            var entry = GetEntry(profile);
            if (entry.Temporary)
            {
                await using var scope = _scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies
                    .AnyAsync(x => x.ManagedWgcfProfile == profile && x.Accounts.Any(), ct))
                    throw new InvalidOperationException("临时出口请通过原账号创建流程重试");
            }
            RejectForeignClaim(profile);
            if (entry.Phase != "creating")
            {
                var claim = _claims.ClaimManagedProfile(profile);
                try
                {
                    Persist(entry with { Phase = "creating", Error = null, StopRequested = false });
                    _queueTokens[profile] = claim.Token;
                    _queuedClaims[profile] = claim;
                    _queue.Writer.TryWrite(profile);
                }
                catch { claim.Dispose(); throw; }
            }
        }
        finally { _stateLock.Release(); }
        return (await ListAsync(ct)).Profiles.Single(x => x.Profile == profile);
    }

    public async Task<WgcfProfileDto> SetEnabledAsync(string profile, bool enabled, CancellationToken ct)
    {
        EnsureAvailable();
        // 状态读取与写回属于同一操作，固定先取状态锁再取工具锁，避免覆盖恢复请求。
        await _stateLock.WaitAsync(ct);
        try
        {
            var entry = GetEntry(profile);
            RejectForeignClaim(profile);
            if (entry.Phase == "creating") throw new InvalidOperationException("档案正在创建，请等待完成");
            using var claim = _claims.ClaimManagedProfile(profile);
            await _toolLock.WaitAsync(ct);
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ProxyManagementService>();
                var proxy = await service.SetManagedWgcfEnabledAsync(profile, enabled, ct, claim.Token);
                await CommandAsync(new[] { enabled ? "start" : "stop", profile }, ct);
                await WaitRuntimeAsync(profile, enabled ? "listening" : "stopped", ct);
                if (enabled) await service.TestAsync(proxy.Id, ct);
                PersistState(entry with
                {
                    Phase = enabled ? "ready" : "stopped",
                    Error = null,
                    // 公开启用是新的明确意图，清除之前持久化的停止请求。
                    StopRequested = !enabled
                }, preserveStopRequested: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 拒绝占用出口的停用时不能更改原来的运行状态。
                if (ex is ProxyInUseException) throw;
                Persist(entry with { Phase = "failed", Error = "启停未确认完成，请核对状态后重试" });
                throw new InvalidOperationException("启停未确认完成，请核对状态后重试");
            }
            finally { _toolLock.Release(); }
        }
        finally { _stateLock.Release(); }
        return (await ListAsync(ct)).Profiles.Single(x => x.Profile == profile);
    }

    public async Task<WgcfProfileDto> TestAsync(string profile, CancellationToken ct)
    {
        EnsureAvailable();
        GetEntry(profile);
        RejectForeignClaim(profile);
        using var claim = _claims.ClaimManagedProfile(profile);
        await _toolLock.WaitAsync(ct);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var proxy = await db.OutboundProxies.AsNoTracking()
                .Where(x => x.ManagedWgcfProfile == profile)
                .Select(x => new { x.Id, x.IsEnabled })
                .SingleOrDefaultAsync(ct) ?? throw new InvalidOperationException("档案尚未生成代理");
            if (!proxy.IsEnabled)
                throw new InvalidOperationException("出口已停止，启动并确认监听后才能检测");
            if (ToDto(GetEntry(profile), null).Runtime != "listening")
                throw new InvalidOperationException("出口尚未监听，不能执行检测");
            await scope.ServiceProvider.GetRequiredService<ProxyManagementService>().TestAsync(proxy.Id, ct);
        }
        finally { _toolLock.Release(); }
        return (await ListAsync(ct)).Profiles.Single(x => x.Profile == profile);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux() || !HasDependencies) return;
        try
        {
            Directory.CreateDirectory(_root);
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var requests = Path.Combine(_root, "web-requests");
            Directory.CreateDirectory(requests);
            File.SetUnixFileMode(requests, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var file in Directory.EnumerateFiles(requests, "*.json"))
            {
                try
                {
                    using var json = ReadPrivateJson(file);
                    var entry = json.RootElement.Deserialize<Entry>();
                    if (entry == null || Path.GetFileNameWithoutExtension(file) != entry.Profile
                        || ProfileFor(entry.Profile[4..]) != entry.Profile) continue;
                    ValidateName(entry.Name);
                    _entries[entry.Profile] = entry;
                    if (entry.Temporary)
                    {
                        await using var scope = _scopes.CreateAsyncScope();
                        var bound = await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundProxies
                            .AnyAsync(x => x.ManagedWgcfProfile == entry.Profile && x.Accounts.Any(), stoppingToken);
                        if (!bound)
                        {
                            Persist(entry with { StopRequested = true });
                            _queue.Writer.TryWrite(entry.Profile);
                        }
                    }
                    else if (entry.Phase == "creating") _queue.Writer.TryWrite(entry.Profile);
                }
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
                { _logger.LogWarning("轻量 WARP 请求档案无法恢复，已跳过"); }
            }
            await Task.WhenAll(SuperviseAsync(stoppingToken), ProcessQueueAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception) { _logger.LogError("轻量 WARP 运行器不可用，面板其它功能继续运行"); }
        finally { _supervisorReady = false; }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var profile in _queue.Reader.ReadAllAsync(ct))
        {
            await _toolLock.WaitAsync(ct);
            var entry = GetEntry(profile);
            try
            {
                for (var i = 0; i < 30 && !_supervisorReady; i++) await Task.Delay(500, ct);
                if (!_supervisorReady) throw new InvalidOperationException();
                _queueTokens.TryGetValue(profile, out var token);
                if (GetEntry(profile).StopRequested)
                {
                    await StopProfileAsync(profile, token, ct);
                    continue;
                }
                await CommandAsync(new[] { "provision", profile, "--accept-tos", "--name", entry.Name }, ct);
                using var meta = ReadPrivateJson(Path.Combine(_root, profile, "meta.json"));
                // provision 重试保留已有启停意图；网页恢复是显式启动操作，需要单独提交启动请求。
                if (!Bool(meta.RootElement, "desired"))
                    await CommandAsync(new[] { "start", profile }, ct);
                using var auth = ReadPrivateJson(Path.Combine(_root, profile, "proxy-auth.json"));
                await using var scope = _scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ProxyManagementService>();
                var proxy = await service.CreateManagedWgcfProxyAsync(profile, entry.Name,
                    meta.RootElement.GetProperty("port").GetInt32(),
                    auth.RootElement.GetProperty("username").GetString()!, auth.RootElement.GetProperty("password").GetString()!, ct);
                // stop 会先关闭数据库路由，再确认运行器退出。恢复同一档案时必须重新启用
                // 路由，不能仅让 wireproxy 监听后就标记为 ready。
                proxy = await service.SetManagedWgcfEnabledAsync(profile, true, ct, token);
                await WaitRuntimeAsync(profile, "listening", ct);
                await service.TestAsync(proxy.Id, ct);
                if (GetEntry(profile).StopRequested)
                    await StopProfileAsync(profile, token, ct);
                else
                    Persist(GetEntry(profile) with { Phase = "ready", Error = null });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                if (GetEntry(profile).StopRequested)
                {
                    try { await StopProfileAsync(profile, _queueTokens.GetValueOrDefault(profile), ct); }
                    catch (Exception) { Persist(GetEntry(profile) with { Phase = "failed", Error = "临时出口停止未确认，请核对运行状态" }); }
                }
                else Persist(GetEntry(profile) with { Phase = "failed", Error = "创建或恢复未完成；材料已保留，请重试恢复。注册结果不明确时不会重新注册" });
                _logger.LogWarning("轻量 WARP 创建或恢复未完成，原始输出已隐藏");
            }
            finally
            {
                _queueTokens.TryRemove(profile, out _);
                _pendingTemporary.TryRemove(profile, out _);
                if (_queuedClaims.TryRemove(profile, out var claim)) claim.Dispose();
                _toolLock.Release();
            }
        }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var process = NewProcess(new[] { "serve" });
            try
            {
                process.Start();
                var drain = Task.WhenAll(DiscardAsync(process.StandardOutput), DiscardAsync(process.StandardError));
                await Task.Delay(750, ct);
                _supervisorReady = !process.HasExited;
                await process.WaitForExitAsync(ct);
                await drain;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception) { _logger.LogWarning("轻量 WARP 监督进程退出，稍后重启"); }
            finally
            {
                _supervisorReady = false;
                await StopProcessAsync(process);
            }
            if (!ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private Process NewProcess(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("/usr/bin/python3") { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(_script);
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(_root);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return new Process { StartInfo = start };
    }

    private async Task CommandAsync(IEnumerable<string> args, CancellationToken ct)
    {
        if (_runCommand != null)
        {
            await _runCommand(args, ct);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(110));
        using var process = NewProcess(args);
        process.Start();
        var drain = Task.WhenAll(DiscardAsync(process.StandardOutput), DiscardAsync(process.StandardError));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await drain;
            if (process.ExitCode != 0) throw new InvalidOperationException("运行器操作未完成，材料已保留");
        }
        finally { await StopProcessAsync(process); }
    }

    private async Task WaitRuntimeAsync(string profile, string state, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            if (ToDto(GetEntry(profile), null).Runtime == state) return;
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("运行器未确认目标状态");
    }

    private static async Task DiscardAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) > 0) { }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            using var signal = new Process { StartInfo = new ProcessStartInfo("/bin/kill") { UseShellExecute = false } };
            signal.StartInfo.ArgumentList.Add("-TERM");
            signal.StartInfo.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            signal.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception)
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            catch (InvalidOperationException) { }
        }
    }

    private void Persist(Entry entry) => PersistState(entry, preserveStopRequested: true);

    private void PersistState(Entry entry, bool preserveStopRequested)
    {
        lock (_persistLock)
        {
            if (preserveStopRequested && entry.Phase is "ready" or "failed"
                && _entries.TryGetValue(entry.Profile, out var current)
                && current.StopRequested)
                entry = entry with { StopRequested = true };
            var target = Path.Combine(_root, "web-requests", entry.Profile + ".json");
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, entry);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
            _entries[entry.Profile] = entry;
        }
    }

    private WgcfProfileDto ToDto(Entry entry, TelegramPanel.Data.Entities.OutboundProxy? proxy)
    {
        var registered = false;
        var generated = false;
        var desired = false;
        var runtime = "unknown";
        try
        {
            using var meta = ReadPrivateJson(Path.Combine(_root, entry.Profile, "meta.json"));
            registered = Bool(meta.RootElement, "registered");
            generated = Bool(meta.RootElement, "generated");
            desired = Bool(meta.RootElement, "desired");
            using var state = ReadPrivateJson(Path.Combine(_root, entry.Profile, "runtime.json"));
            runtime = RuntimeState(meta.RootElement, state.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
        return new(entry.Profile, entry.Name, entry.Phase, registered, generated, desired, runtime,
            proxy?.Id, proxy?.Accounts.Count ?? 0, proxy?.TestStatus ?? "unknown", proxy?.EgressIp, entry.Error);
    }

    private Entry GetEntry(string profile) => _entries.TryGetValue(profile, out var entry)
        ? entry : throw new KeyNotFoundException("轻量出口档案不存在");

    private void EnsureAvailable()
    {
        if (!HasDependencies || !_supervisorReady) throw new InvalidOperationException("轻量运行器未就绪，请更新镜像或稍后重试");
    }

    private static JsonDocument ReadPrivateJson(string file)
    {
        if ((File.GetAttributes(Path.GetDirectoryName(file)!) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("私有档案路径不能使用符号链接");
        using var stream = File.OpenRead(file);
        if (stream.Length > 64 * 1024) throw new IOException("档案内容过大");
        return JsonDocument.Parse(stream);
    }

    private static bool Bool(JsonElement json, string key) => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    internal static string ProfileFor(string requestId) => Guid.TryParse(requestId, out var id) && id != Guid.Empty
        ? "web-" + id.ToString("N") : throw new ArgumentException("请求标识必须是有效 UUID");
    internal static string ValidateName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("名称必须为 1～80 个字符且不含控制字符");
        return name;
    }
    internal static string RuntimeState(JsonElement meta, JsonElement runtime)
    {
        if (!runtime.TryGetProperty("at", out var at) || !at.TryGetDouble(out var timestamp)
            || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 10) return "unknown";
        if (!meta.TryGetProperty("revision", out var revision) || !runtime.TryGetProperty("revision", out var observed)
            || revision.GetString() != observed.GetString()) return Bool(meta, "desired") ? "starting" : "stopping";
        return runtime.TryGetProperty("state", out var state) && state.GetString() is "listening" or "starting" or "stopped" or "backoff" or "failed"
            ? state.GetString()! : "unknown";
    }
}
