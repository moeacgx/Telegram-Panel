using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TelegramPanel.Web.Modules;

namespace TelegramPanel.Web.Services;

public sealed class AppSelfUpdateService
{
    private const string CacheKeyPrefix = "self-update:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<UpdateCheckOptions> _updateOptions;
    private readonly IOptionsMonitor<SelfUpdateOptions> _selfUpdateOptions;
    private readonly UpdateModeStore _updateModeStore;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly AppRestartService _restartService;
    private readonly ILogger<AppSelfUpdateService> _logger;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public AppSelfUpdateService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<UpdateCheckOptions> updateOptions,
        IOptionsMonitor<SelfUpdateOptions> selfUpdateOptions,
        UpdateModeStore updateModeStore,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        AppRestartService restartService,
        ILogger<AppSelfUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _updateOptions = updateOptions;
        _selfUpdateOptions = selfUpdateOptions;
        _updateModeStore = updateModeStore;
        _configuration = configuration;
        _environment = environment;
        _restartService = restartService;
        _logger = logger;
    }

    public bool IsDockerEnvironment => IsRunningInDocker();

    public async Task<AppSelfUpdateInfo> CheckLatestAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionService.Version;
        var updateOptions = _updateOptions.CurrentValue;
        var selfUpdateOptions = _selfUpdateOptions.CurrentValue;
        var isDocker = IsRunningInDocker();
        var updateMode = _updateModeStore.GetMode(selfUpdateOptions.Mode);

        if (updateMode == SelfUpdateOptions.ImageMode)
        {
            return AppSelfUpdateInfo.Disabled(
                currentVersion,
                isDocker,
                "当前为 Docker 镜像更新模式，请修改 TP_IMAGE 后执行 docker compose pull && docker compose up -d",
                updateMode);
        }

        if (!selfUpdateOptions.Enabled)
        {
            return AppSelfUpdateInfo.Disabled(currentVersion, isDocker, "自动更新功能未启用", updateMode);
        }

        var repo = (updateOptions.Repository ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
        {
            return AppSelfUpdateInfo.Failed(currentVersion, isDocker, "UpdateCheck:Repository 配置无效（应为 owner/repo）", updateMode);
        }

        if (!TryGetAssetKeyword(RuntimeInformation.ProcessArchitecture, out var assetKeyword, out var archLabel))
        {
            return AppSelfUpdateInfo.Failed(currentVersion, isDocker, $"当前架构不支持自动更新：{RuntimeInformation.ProcessArchitecture}", updateMode);
        }

        var cacheKey = $"{CacheKeyPrefix}{repo}:{assetKeyword}:{updateMode}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out var cachedObj) && cachedObj is AppSelfUpdateInfo cached)
            return cached;

        AppSelfUpdateInfo result;
        try
        {
            var release = await FetchLatestReleaseAsync(repo, cancellationToken);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                result = AppSelfUpdateInfo.Failed(currentVersion, isDocker, "仓库未找到可用 Release，无法执行一键更新");
            }
            else
            {
                var latestVersion = ParseVersionFromTag(release.TagName);
                if (latestVersion == null)
                {
                    result = AppSelfUpdateInfo.Failed(currentVersion, isDocker, $"解析版本失败（tag={release.TagName}）");
                }
                else
                {
                    var updateAvailable = CompareVersions(latestVersion.Value, currentVersion) > 0;
                    var matchedAsset = PickAsset(release.Assets, assetKeyword);
                    var blockedReason = ResolveBlockedReason(
                        updateAvailable: updateAvailable,
                        assetFound: matchedAsset != null,
                        isDocker: isDocker,
                        dockerOnly: selfUpdateOptions.DockerOnly,
                        archLabel: archLabel);

                    var notes = NormalizeNotes(release.Body);

                    result = new AppSelfUpdateInfo
                    {
                        Success = true,
                        Enabled = true,
                        CurrentVersion = currentVersion,
                        LatestTag = release.TagName,
                        LatestVersion = latestVersion.Value.ToString(),
                        UpdateAvailable = updateAvailable,
                        Url = release.HtmlUrl,
                        PublishedAt = release.PublishedAt,
                        Notes = notes,
                        IsDocker = isDocker,
                        UpdateMode = updateMode,
                        AssetName = matchedAsset?.Name,
                        AssetSizeBytes = matchedAsset?.Size,
                        AssetDownloadUrl = matchedAsset?.BrowserDownloadUrl,
                        CanApply = blockedReason == null,
                        BlockedReason = blockedReason,
                        CheckedAtUtc = DateTimeOffset.UtcNow
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self update check failed");
            result = AppSelfUpdateInfo.Failed(currentVersion, isDocker, ex.Message, updateMode);
        }

        var cacheMinutes = updateOptions.CacheMinutes <= 0 ? 30 : updateOptions.CacheMinutes;
        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cacheMinutes)
        });

        return result;
    }

    public async Task<AppSelfUpdateApplyResult> ApplyLatestAsync(CancellationToken cancellationToken = default)
    {
        var options = _selfUpdateOptions.CurrentValue;
        var updateMode = _updateModeStore.GetMode(options.Mode);
        if (updateMode == SelfUpdateOptions.ImageMode)
            return AppSelfUpdateApplyResult.Failed("当前为 Docker 镜像更新模式，请通过 TP_IMAGE 切换镜像并重建容器");

        if (!options.Enabled)
            return AppSelfUpdateApplyResult.Failed("自动更新功能未启用");

        if (options.DockerOnly && !IsRunningInDocker())
            return AppSelfUpdateApplyResult.Failed("当前不是 Docker 运行环境，已禁止一键更新");

        if (!await _applyLock.WaitAsync(0, cancellationToken))
            return AppSelfUpdateApplyResult.Failed("已有更新任务在执行，请稍后重试");

        try
        {
            var check = await CheckLatestAsync(forceRefresh: true, cancellationToken);
            if (!check.Success)
                return AppSelfUpdateApplyResult.Failed($"检查更新失败：{check.Error}");

            if (!check.UpdateAvailable)
                return AppSelfUpdateApplyResult.Failed($"当前已是最新版本（v{check.CurrentVersion}）");

            if (!check.CanApply || string.IsNullOrWhiteSpace(check.AssetDownloadUrl) || string.IsNullOrWhiteSpace(check.AssetName))
            {
                var reason = check.BlockedReason ?? "未找到匹配当前架构的更新包";
                return AppSelfUpdateApplyResult.Failed(reason);
            }

            var workRoot = ResolveWorkRoot(options);
            Directory.CreateDirectory(workRoot);
            var (workspaceDir, currentDir, backupDir) = ResolveUpdateDirectories(
                workRoot,
                options);

            var unsafeStorage = FindUnsafeStoragePaths(
                _configuration,
                _environment,
                currentDir,
                backupDir,
                workspaceDir);
            if (unsafeStorage.Count > 0)
            {
                return AppSelfUpdateApplyResult.Failed(
                    "一键更新已阻止：以下持久化路径位于会被轮换的程序目录中，或无法安全解析："
                    + string.Join("；", unsafeStorage)
                    + "。请先把路径迁移到 /data 或其它持久化卷后再更新。");
            }

            Directory.CreateDirectory(workspaceDir);

            var stageDir = Path.Combine(workspaceDir, $"stage-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            var packageFileName = $"pkg-{Guid.NewGuid():N}-{SanitizeFileName(check.AssetName)}";
            var packagePath = Path.Combine(workspaceDir, packageFileName);

            try
            {
                await DownloadFileAsync(check.AssetDownloadUrl, packagePath, options.DownloadTimeoutSeconds, cancellationToken);

                Directory.CreateDirectory(stageDir);
                ZipFile.ExtractToDirectory(packagePath, stageDir, overwriteFiles: true);

                var entryDll = Path.Combine(stageDir, "TelegramPanel.Web.dll");
                if (!File.Exists(entryDll))
                    return AppSelfUpdateApplyResult.Failed("更新包结构无效：缺少 TelegramPanel.Web.dll");

                // 先把完整状态写入暂存目录，再原子切换目录。
                // 避免目录已切换但 marker 写入失败，导致新版本无法启动或无法回滚。
                WriteSelfUpdateMarkers(stageDir, check);
                PromoteCurrentDirectory(stageDir, currentDir, backupDir);
                stageDir = string.Empty;

                var restartDelaySeconds = options.RestartDelaySeconds;
                if (restartDelaySeconds <= 0) restartDelaySeconds = 2;
                _restartService.RequestRestart(TimeSpan.FromSeconds(restartDelaySeconds), $"self-update {check.LatestTag}");

                _logger.LogWarning(
                    "Self update applied successfully. current={CurrentVersion} target={TargetTag} asset={AssetName}",
                    check.CurrentVersion,
                    check.LatestTag,
                    check.AssetName);

                return AppSelfUpdateApplyResult.Succeeded(
                    $"更新包已部署：{check.AssetName}，服务将在约 {restartDelaySeconds} 秒后重启",
                    check.LatestTag,
                    check.LatestVersion);
            }
            finally
            {
                TryDeleteFile(packagePath);
                if (!string.IsNullOrWhiteSpace(stageDir))
                    TryDeleteDirectory(stageDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply self update failed");
            return AppSelfUpdateApplyResult.Failed($"更新失败：{ex.Message}");
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<GitHubReleaseDto?> FetchLatestReleaseAsync(string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        var client = CreateGitHubClient();

        using var resp = await client.GetAsync(url, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<GitHubReleaseDto>(json, JsonOptions);
    }

    private async Task DownloadFileAsync(
        string url,
        string path,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var timeout = timeoutSeconds <= 0 ? 300 : timeoutSeconds;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Telegram-Panel");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");

        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        await using var source = await resp.Content.ReadAsStreamAsync(cts.Token);
        await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cts.Token);
    }

    private HttpClient CreateGitHubClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Telegram-Panel");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private string ResolveWorkRoot(SelfUpdateOptions options)
    {
        var configured = (options.WorkRootPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured))
                return Path.GetFullPath(configured);

            var stableRoot = StoragePathResolver.ResolvePersistentRoot(_configuration)
                ?? _environment.ContentRootPath;
            return Path.GetFullPath(Path.Combine(stableRoot, configured));
        }

        if (Directory.Exists("/data"))
            return "/data";

        return Path.GetFullPath(_environment.ContentRootPath);
    }

    internal static (string WorkspaceDir, string CurrentDir, string BackupDir) ResolveUpdateDirectories(
        string workRoot,
        SelfUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalizedRoot = Path.GetFullPath(workRoot);
        if (!StoragePathResolver.TryResolvePhysicalPath(
                normalizedRoot,
                out var physicalRoot,
                out var rootError))
        {
            throw new InvalidOperationException(
                $"自更新工作根目录无法安全解析：{normalizedRoot}（{rootError}）");
        }

        var workspace = ResolveUpdateDirectory(
            normalizedRoot,
            physicalRoot,
            options.WorkDirectoryName,
            "self-update",
            nameof(SelfUpdateOptions.WorkDirectoryName));
        var current = ResolveUpdateDirectory(
            normalizedRoot,
            physicalRoot,
            options.CurrentDirectoryName,
            "app-current",
            nameof(SelfUpdateOptions.CurrentDirectoryName));
        var backup = ResolveUpdateDirectory(
            normalizedRoot,
            physicalRoot,
            options.BackupDirectoryName,
            "app-previous",
            nameof(SelfUpdateOptions.BackupDirectoryName));

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var directories = new[] { workspace, current, backup };
        if (directories.Select(x => x.LogicalPath).Distinct(comparer).Count() != directories.Length
            || directories.Select(x => x.PhysicalPath).Distinct(comparer).Count() != directories.Length)
        {
            throw new InvalidOperationException(
                "自更新工作目录、当前版本目录和备份目录必须互不相同");
        }

        return (workspace.LogicalPath, current.LogicalPath, backup.LogicalPath);
    }

    private static (string LogicalPath, string PhysicalPath) ResolveUpdateDirectory(
        string workRoot,
        string physicalWorkRoot,
        string? configuredName,
        string defaultName,
        string optionName)
    {
        var directoryName = (configuredName ?? string.Empty).Trim();
        if (directoryName.Length == 0)
            directoryName = defaultName;

        if (directoryName is "." or ".."
            || Path.IsPathRooted(directoryName)
            || directoryName.Contains('/')
            || directoryName.Contains('\\'))
        {
            throw new InvalidOperationException(
                $"{optionName} 必须是 WorkRootPath 下的单级目录名");
        }

        var logicalPath = Path.GetFullPath(Path.Combine(workRoot, directoryName));
        if (string.Equals(logicalPath, workRoot, StringComparison.OrdinalIgnoreCase)
            || !StoragePathResolver.IsPathWithin(logicalPath, workRoot))
        {
            throw new InvalidOperationException(
                $"{optionName} 必须位于 WorkRootPath 内");
        }

        if (!StoragePathResolver.TryResolvePhysicalPath(
                logicalPath,
                out var physicalPath,
                out var error))
        {
            throw new InvalidOperationException(
                $"{optionName} 无法安全解析：{logicalPath}（{error}）");
        }

        if (!StoragePathResolver.IsPathWithin(physicalPath, physicalWorkRoot))
        {
            throw new InvalidOperationException(
                $"{optionName} 的真实路径超出 WorkRootPath：{logicalPath}");
        }

        return (logicalPath, physicalPath);
    }

    internal static List<string> FindUnsafeStoragePaths(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        string currentDir,
        string backupDir,
        string workspaceDir)
    {
        var paths = new List<(string Name, string Path)>();
        var failures = new List<string>();
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        void AddPath(string name, Func<string> resolver)
        {
            try
            {
                var resolved = resolver();
                if (string.IsNullOrWhiteSpace(resolved))
                    throw new InvalidOperationException("配置结果为空");
                paths.Add((name, Path.GetFullPath(resolved)));
            }
            catch (Exception ex)
            {
                failures.Add($"{name}路径解析失败（{ex.Message}）");
            }
        }

        try
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=telegram_panel.db";
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource)
                && !string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(("数据库", Path.IsPathRooted(dataSource)
                    ? Path.GetFullPath(dataSource)
                    : StoragePathResolver.ResolveWritablePath(
                        configuration,
                        environment,
                        dataSource,
                        "telegram_panel.db")));
            }
        }
        catch (Exception ex)
        {
            failures.Add($"数据库路径解析失败（{ex.Message}）");
        }

        AddPath(
            "后台凭据",
            () => StoragePathResolver.ResolveWritablePath(
                configuration,
                environment,
                configuration["AdminAuth:CredentialsPath"],
                "admin_auth.json"));
        AddPath(
            "初始化密码",
            () => Path.Combine(Path.GetDirectoryName(StoragePathResolver.ResolveWritablePath(
                configuration, environment, configuration["AdminAuth:CredentialsPath"], "admin_auth.json"))!,
                "admin-bootstrap-password.txt"));
        AddPath(
            "Session",
            () => StoragePathResolver.ResolveWritablePath(
                configuration,
                environment,
                configuration["Telegram:SessionsPath"],
                "sessions"));
        AddPath(
            "本地配置",
            () => LocalConfigFile.ResolvePath(configuration, environment));
        AddPath(
            "DataProtection 密钥",
            () =>
            {
                var configured = (configuration["DataProtection:KeysPath"] ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(
                        StoragePathResolver.ResolveWritableRoot(configuration, environment),
                        Directory.Exists("/data") ? "keys" : "data-protection-keys")
                    : StoragePathResolver.ResolveRelativeToBase(configured, environment.ContentRootPath);
            });
        AddPath(
            "Modules",
            () => ModulePaths.ResolveModulesRoot(configuration, environment));
        AddPath(
            "Uploads",
            () => ImageAssetStorageService.ResolveUploadsRootPath(configuration, environment));

        var replaceableRoots = new[]
        {
            (Name: "当前版本目录", Path: currentDir),
            (Name: "备份目录", Path: backupDir),
            (Name: "更新工作目录", Path: workspaceDir),
            (Name: "安装目录", Path: environment.ContentRootPath)
        };

        var resolvedRoots = new List<(string LogicalPath, string PhysicalPath)>();
        foreach (var root in replaceableRoots)
        {
            try
            {
                var logicalPath = Path.GetFullPath(root.Path);
                if (!StoragePathResolver.TryResolvePhysicalPath(
                        logicalPath,
                        out var physicalPath,
                        out var error))
                {
                    failures.Add($"{root.Name}无法安全解析：{logicalPath}（{error}）");
                    continue;
                }

                resolvedRoots.Add((logicalPath, physicalPath));
            }
            catch (Exception ex)
            {
                failures.Add($"{root.Name}无法安全解析：{root.Path}（{ex.Message}）");
            }
        }

        foreach (var item in paths)
        {
            if (!StoragePathResolver.TryResolvePhysicalPath(
                    item.Path,
                    out var physicalPath,
                    out var error))
            {
                failures.Add($"{item.Name}路径无法安全解析：{item.Path}（{error}）");
                continue;
            }

            var isUnsafe = resolvedRoots.Any(root =>
                StoragePathResolver.IsPathWithin(item.Path, root.LogicalPath)
                || StoragePathResolver.IsPathWithin(physicalPath, root.PhysicalPath));
            if (!isUnsafe)
                continue;

            var physicalSuffix = pathComparer.Equals(item.Path, physicalPath)
                ? string.Empty
                : $"（真实路径={physicalPath}）";
            failures.Add($"{item.Name}={item.Path}{physicalSuffix}");
        }

        return failures
            .Distinct(pathComparer)
            .ToList();
    }

    private static void PromoteCurrentDirectory(string stageDir, string currentDir, string backupDir)
    {
        string? archivedBackupDir = null;
        if (Directory.Exists(backupDir))
        {
            archivedBackupDir = $"{backupDir}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            if (Directory.Exists(archivedBackupDir))
                archivedBackupDir += $"-{Guid.NewGuid():N}";
            Directory.Move(backupDir, archivedBackupDir);
        }

        var movedCurrent = false;
        try
        {
            if (Directory.Exists(currentDir))
            {
                Directory.Move(currentDir, backupDir);
                movedCurrent = true;
            }

            Directory.Move(stageDir, currentDir);
        }
        catch
        {
            if (movedCurrent && Directory.Exists(backupDir) && !Directory.Exists(currentDir))
            {
                Directory.Move(backupDir, currentDir);
            }

            if (!string.IsNullOrWhiteSpace(archivedBackupDir)
                && Directory.Exists(archivedBackupDir)
                && !Directory.Exists(backupDir))
            {
                Directory.Move(archivedBackupDir, backupDir);
            }

            throw;
        }
    }

    private static void WriteSelfUpdateMarkers(string currentDir, AppSelfUpdateInfo check)
    {
        // 发布包不应携带上一次启动留下的状态；删除失败必须中止更新，
        // 否则残留 confirmed 会让入口脚本误把未经验证的新版本视为可用。
        var attemptedPath = Path.Combine(currentDir, SelfUpdateStartupCoordinator.AttemptedMarkerFileName);
        var confirmedPath = Path.Combine(currentDir, SelfUpdateStartupCoordinator.ConfirmedMarkerFileName);
        if (File.Exists(attemptedPath))
            File.Delete(attemptedPath);
        if (File.Exists(confirmedPath))
            File.Delete(confirmedPath);

        var payload = new
        {
            version = check.LatestVersion,
            tag = check.LatestTag,
            asset = check.AssetName,
            appliedAtUtc = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(Path.Combine(currentDir, SelfUpdateStartupCoordinator.PendingMarkerFileName), json);
        File.WriteAllText(Path.Combine(currentDir, SelfUpdateStartupCoordinator.LegacyMarkerFileName), json);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = fileName.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (invalidChars.Contains(buffer[i]))
                buffer[i] = '_';
        }

        return new string(buffer);
    }

    private static string? NormalizeNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var notes = body.Trim();
        if (notes.Length > 2000)
            notes = notes[..2000].Trim() + "\n…";

        return notes;
    }

    private static GitHubAssetDto? PickAsset(IReadOnlyList<GitHubAssetDto>? assets, string assetKeyword)
    {
        if (assets == null || assets.Count == 0)
            return null;

        return assets
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Where(x => x.Name!.Contains(assetKeyword, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Name!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x.BrowserDownloadUrl))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? ResolveBlockedReason(bool updateAvailable, bool assetFound, bool isDocker, bool dockerOnly, string archLabel)
    {
        if (!updateAvailable)
            return "当前已是最新版本";
        if (!assetFound)
            return $"未找到匹配架构 {archLabel} 的更新包";
        if (dockerOnly && !isDocker)
            return "当前不是 Docker 运行环境";
        return null;
    }

    private static bool TryGetAssetKeyword(Architecture architecture, out string keyword, out string archLabel)
    {
        switch (architecture)
        {
            case Architecture.X64:
                keyword = "linux-x64";
                archLabel = "linux-x64";
                return true;
            case Architecture.Arm64:
                keyword = "linux-arm64";
                archLabel = "linux-arm64";
                return true;
            default:
                keyword = string.Empty;
                archLabel = architecture.ToString();
                return false;
        }
    }

    private static bool IsRunningInDocker()
    {
        var dotnetInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (string.Equals(dotnetInContainer, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        return File.Exists("/.dockerenv");
    }

    private static SimpleVersion? ParseVersionFromTag(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return null;

        var s = tagName.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

        var dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];

        var plus = s.IndexOf('+');
        if (plus > 0) s = s[..plus];

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return null;

        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        if (!int.TryParse(parts[2], out var patch)) return null;
        return new SimpleVersion(major, minor, patch);
    }

    private static int CompareVersions(SimpleVersion latest, string current)
    {
        var cur = ParseVersionFromTag(current) ?? new SimpleVersion(0, 0, 0);
        return latest.CompareTo(cur);
    }

    private readonly record struct SimpleVersion(int Major, int Minor, int Patch) : IComparable<SimpleVersion>
    {
        public int CompareTo(SimpleVersion other)
        {
            var r = Major.CompareTo(other.Major);
            if (r != 0) return r;
            r = Minor.CompareTo(other.Minor);
            if (r != 0) return r;
            return Patch.CompareTo(other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

public sealed class AppSelfUpdateInfo
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public bool Enabled { get; init; }

    public string CurrentVersion { get; init; } = "Unknown";
    public string? LatestVersion { get; init; }
    public string? LatestTag { get; init; }
    public bool UpdateAvailable { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsDocker { get; init; }
    public string UpdateMode { get; init; } = SelfUpdateOptions.AutoMode;
    public bool CanApply { get; init; }
    public string? BlockedReason { get; init; }

    public string? AssetName { get; init; }
    public long? AssetSizeBytes { get; init; }
    public string? AssetDownloadUrl { get; init; }

    public static AppSelfUpdateInfo Disabled(string currentVersion, bool isDocker, string? reason = null, string? updateMode = null) => new()
    {
        Success = true,
        Enabled = false,
        CurrentVersion = currentVersion,
        IsDocker = isDocker,
        UpdateMode = SelfUpdateOptions.NormalizeMode(updateMode),
        UpdateAvailable = false,
        CanApply = false,
        BlockedReason = reason,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };

    public static AppSelfUpdateInfo Failed(string currentVersion, bool isDocker, string error, string? updateMode = null) => new()
    {
        Success = false,
        Enabled = true,
        Error = error,
        CurrentVersion = currentVersion,
        IsDocker = isDocker,
        UpdateMode = SelfUpdateOptions.NormalizeMode(updateMode),
        UpdateAvailable = false,
        CanApply = false,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };
}

public sealed class AppSelfUpdateApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool RestartScheduled { get; init; }
    public string? LatestTag { get; init; }
    public string? LatestVersion { get; init; }

    public static AppSelfUpdateApplyResult Failed(string message) => new()
    {
        Success = false,
        Message = message,
        RestartScheduled = false
    };

    public static AppSelfUpdateApplyResult Succeeded(string message, string? latestTag, string? latestVersion) => new()
    {
        Success = true,
        Message = message,
        RestartScheduled = true,
        LatestTag = latestTag,
        LatestVersion = latestVersion
    };
}
