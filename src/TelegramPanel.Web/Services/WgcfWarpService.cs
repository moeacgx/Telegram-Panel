using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;

namespace TelegramPanel.Web.Services;

public sealed record WgcfProfileDto(string Profile, string Name, string Phase, bool Registered,
    bool Generated, bool Desired, string Runtime, int? ProxyId, int AccountCount,
    string TestStatus, string? EgressIp, string? Error);
public sealed record WgcfEnvironmentDto(bool Available, string? Reason, IReadOnlyList<WgcfProfileDto> Profiles);

/// <summary>在主容器内托管轻量出口；注册材料仅保存在私有持久目录。</summary>
public sealed class WgcfWarpService : BackgroundService
{
    private sealed record Entry(string Profile, string Name, string Phase, string? Error = null);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WgcfWarpService> _logger;
    private readonly string _root;
    private readonly string _script = Path.Combine(AppContext.BaseDirectory, "wgcf-warp", "warpctl.py");
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _toolLock = new(1, 1);
    private volatile bool _supervisorReady;

    public WgcfWarpService(IServiceScopeFactory scopes, IConfiguration configuration,
        IWebHostEnvironment environment, ILogger<WgcfWarpService> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _root = Path.Combine(StoragePathResolver.ResolveWritableRoot(configuration, environment), "wgcf-warp");
    }

    private bool HasDependencies => OperatingSystem.IsLinux() && File.Exists(_script)
        && File.Exists("/usr/bin/python3") && File.Exists("/usr/local/bin/wgcf") && File.Exists("/usr/local/bin/wireproxy");

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
            if (entry.Phase != "creating")
            {
                Persist(entry with { Phase = "creating", Error = null });
                _queue.Writer.TryWrite(profile);
            }
        }
        finally { _stateLock.Release(); }
        return (await ListAsync(ct)).Profiles.Single(x => x.Profile == profile);
    }

    public async Task<WgcfProfileDto> SetEnabledAsync(string profile, bool enabled, CancellationToken ct)
    {
        EnsureAvailable();
        var entry = GetEntry(profile);
        if (entry.Phase == "creating") throw new InvalidOperationException("档案正在创建，请等待完成");
        await _toolLock.WaitAsync(ct);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ProxyManagementService>();
            var proxy = await service.SetManagedWgcfEnabledAsync(profile, enabled, ct);
            await CommandAsync(new[] { enabled ? "start" : "stop", profile }, ct);
            await WaitRuntimeAsync(profile, enabled ? "listening" : "stopped", ct);
            if (enabled) await service.TestAsync(proxy.Id, ct);
            Persist(entry with { Phase = enabled ? "ready" : "stopped", Error = null });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 拒绝占用出口的停用时不能更改原来的运行状态。
            if (ex is ProxyInUseException) throw;
            Persist(entry with { Phase = "failed", Error = "启停未确认完成，请核对状态后重试" });
            throw new InvalidOperationException("启停未确认完成，请核对状态后重试");
        }
        finally { _toolLock.Release(); }
        return (await ListAsync(ct)).Profiles.Single(x => x.Profile == profile);
    }

    public async Task<WgcfProfileDto> TestAsync(string profile, CancellationToken ct)
    {
        EnsureAvailable();
        GetEntry(profile);
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var proxyId = await db.OutboundProxies.Where(x => x.ManagedWgcfProfile == profile)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(ct) ?? throw new InvalidOperationException("档案尚未生成代理");
        if (ToDto(GetEntry(profile), null).Runtime != "listening")
            throw new InvalidOperationException("出口尚未监听，不能执行检测");
        await scope.ServiceProvider.GetRequiredService<ProxyManagementService>().TestAsync(proxyId, ct);
        return (await ListAsync(ct)).Profiles.Single(x => x.Profile == profile);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!HasDependencies) return;
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
                    if (entry.Phase == "creating") _queue.Writer.TryWrite(entry.Profile);
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
                await CommandAsync(new[] { "provision", profile, "--accept-tos", "--name", entry.Name }, ct);
                using var meta = ReadPrivateJson(Path.Combine(_root, profile, "meta.json"));
                using var auth = ReadPrivateJson(Path.Combine(_root, profile, "proxy-auth.json"));
                await using var scope = _scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ProxyManagementService>();
                var proxy = await service.CreateManagedWgcfProxyAsync(profile, entry.Name,
                    meta.RootElement.GetProperty("port").GetInt32(),
                    auth.RootElement.GetProperty("username").GetString()!, auth.RootElement.GetProperty("password").GetString()!, ct);
                await WaitRuntimeAsync(profile, "listening", ct);
                await service.TestAsync(proxy.Id, ct);
                Persist(entry with { Phase = "ready", Error = null });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                Persist(entry with { Phase = "failed", Error = "创建或恢复未完成；材料已保留，请重试恢复。注册结果不明确时不会重新注册" });
                _logger.LogWarning("轻量 WARP 创建或恢复未完成，原始输出已隐藏");
            }
            finally { _toolLock.Release(); }
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

    private void Persist(Entry entry)
    {
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
