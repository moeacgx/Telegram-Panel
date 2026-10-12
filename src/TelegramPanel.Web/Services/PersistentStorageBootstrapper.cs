using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TelegramPanel.Web.Services;

public sealed record PersistentStoragePaths(
    string WritableRoot,
    string DatabasePath,
    string CredentialsPath,
    string SessionsPath,
    string ConnectionString);

/// <summary>
/// 在数据库和后台服务启动前固定持久化路径，并兼容迁移旧版自更新目录中的数据。
/// SQLite 使用在线备份生成一致快照；迁移不删除旧数据，异常时直接阻止启动。
/// </summary>
public static class PersistentStorageBootstrapper
{
    private const string DefaultDatabaseName = "telegram_panel.db";
    private const string LegacyDatabaseName = "telegram-panel.db";
    private const string DefaultCredentialsName = "admin_auth.json";
    private const string DefaultSessionsName = "sessions";
    private const string LegacyDatabaseMigrationMarkerPrefix = ".storage-database-migration-v1-";
    private const string LegacyCredentialMigrationMarkerName = ".storage-credential-migration-v1.complete";
    private const string LegacySessionMigrationMarkerName = ".storage-session-migration-v1.complete";
    private const string LegacyMigrationLockName = ".storage-migration-v1.lock";
    private static readonly StringComparer FileSystemPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison FileSystemPathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static PersistentStoragePaths Initialize(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        Action<string>? report = null)
    {
        var writableRoot = Path.GetFullPath(
            StoragePathResolver.ResolveWritableRoot(configuration, environment));
        Directory.CreateDirectory(writableRoot);

        var configuredConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? $"Data Source={DefaultDatabaseName}";
        var (connectionString, databasePath, databaseRelativePath) = ResolveDatabase(
            configuredConnectionString,
            writableRoot);

        var credentialsSetting = (configuration["AdminAuth:CredentialsPath"] ?? string.Empty).Trim();
        var sessionsSetting = (configuration["Telegram:SessionsPath"] ?? string.Empty).Trim();
        var credentialsPath = StoragePathResolver.ResolveWritablePath(
            configuration,
            environment,
            credentialsSetting,
            DefaultCredentialsName);
        var sessionsPath = StoragePathResolver.ResolveWritablePath(
            configuration,
            environment,
            sessionsSetting,
            DefaultSessionsName);

        using var migrationLock = AcquireLegacyMigrationLock(writableRoot);
        var legacyBases = EnumerateLegacyBases(environment.ContentRootPath, writableRoot);
        var databaseMarkerPath = BuildLegacyDatabaseMigrationMarkerPath(
            writableRoot,
            databasePath);
        if (ExpectedFileExists(databaseMarkerPath))
        {
            ValidateCompletedMigrationDatabase(databasePath);
            report?.Invoke($"旧数据库迁移已完成，跳过旧数据库扫描：{databaseMarkerPath}");
        }
        else
        {
            var databaseCandidates = EnumerateLegacyCandidates(legacyBases, databaseRelativePath)
                .Concat(EnumerateLegacyCandidates(legacyBases, DefaultDatabaseName))
                .Concat(EnumerateLegacyCandidates(legacyBases, LegacyDatabaseName));
            TryMigrateSqliteDatabase(
                databasePath,
                databaseCandidates,
                report);

            if (CanCompleteLegacyMigration(databasePath))
            {
                WriteLegacyMigrationMarker(databaseMarkerPath);
                report?.Invoke($"旧数据库迁移已完成：{databaseMarkerPath}");
            }
        }

        var credentialMarkerPath = Path.Combine(
            writableRoot,
            LegacyCredentialMigrationMarkerName);
        if (ExpectedFileExists(credentialMarkerPath))
        {
            report?.Invoke($"后台凭据旧目录迁移已完成，跳过旧目录扫描：{credentialMarkerPath}");
        }
        else
        {
            var credentialCandidates = EnumerateLegacyCandidates(
                    legacyBases,
                    ToLegacyRelativePath(credentialsSetting, DefaultCredentialsName))
                .Concat(EnumerateLegacyCandidates(legacyBases, DefaultCredentialsName));
            TryMigrateCredentialsFile(
                credentialsPath,
                credentialCandidates,
                configuration,
                report);
            WriteLegacyMigrationMarker(credentialMarkerPath);
            report?.Invoke($"后台凭据旧目录迁移已完成：{credentialMarkerPath}");
        }

        var sessionMarkerPath = Path.Combine(
            writableRoot,
            LegacySessionMigrationMarkerName);
        if (ExpectedFileExists(sessionMarkerPath))
        {
            Directory.CreateDirectory(sessionsPath);
            report?.Invoke($"Session 旧目录迁移已完成，跳过旧目录扫描：{sessionMarkerPath}");
        }
        else
        {
            TryMigrateDirectory(
                sessionsPath,
                EnumerateLegacyCandidates(
                    legacyBases,
                    ToLegacyRelativePath(sessionsSetting, DefaultSessionsName)),
                report);
            WriteLegacyMigrationMarker(sessionMarkerPath);
            report?.Invoke($"Session 旧目录迁移已完成：{sessionMarkerPath}");
        }

        configuration["ConnectionStrings:DefaultConnection"] = connectionString;
        configuration["AdminAuth:CredentialsPath"] = credentialsPath;
        configuration["Telegram:SessionsPath"] = sessionsPath;

        report?.Invoke(
            $"持久化路径：数据库={databasePath}；后台凭据={credentialsPath}；Session={sessionsPath}");

        return new PersistentStoragePaths(
            writableRoot,
            databasePath,
            credentialsPath,
            sessionsPath,
            connectionString);
    }

    public static void CompleteDatabaseMigration(
        PersistentStoragePaths paths,
        Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        using var migrationLock = AcquireLegacyMigrationLock(paths.WritableRoot);
        var markerPath = BuildLegacyDatabaseMigrationMarkerPath(
            paths.WritableRoot,
            paths.DatabasePath);
        if (ExpectedFileExists(markerPath))
            return;

        ValidateCompletedMigrationDatabase(paths.DatabasePath);
        WriteLegacyMigrationMarker(markerPath);
        report?.Invoke($"旧数据库迁移已完成：{markerPath}");
    }

    private static (string ConnectionString, string DatabasePath, string LegacyRelativePath) ResolveDatabase(
        string configuredConnectionString,
        string writableRoot)
    {
        var builder = new SqliteConnectionStringBuilder(configuredConnectionString);
        var dataSource = string.IsNullOrWhiteSpace(builder.DataSource)
            ? DefaultDatabaseName
            : builder.DataSource.Trim();

        if (string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            return (builder.ToString(), dataSource, DefaultDatabaseName);

        var databasePath = Path.IsPathRooted(dataSource)
            ? Path.GetFullPath(dataSource)
            : Path.GetFullPath(Path.Combine(writableRoot, dataSource));
        builder.DataSource = databasePath;

        return (
            builder.ToString(),
            databasePath,
            ToLegacyRelativePath(dataSource, DefaultDatabaseName));
    }

    private static string ToLegacyRelativePath(string? configuredPath, string fallbackName)
    {
        var path = (configuredPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
            return fallbackName;

        if (Path.IsPathRooted(path))
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return path;
    }

    private static FileStream AcquireLegacyMigrationLock(string writableRoot)
    {
        var lockPath = Path.Combine(writableRoot, LegacyMigrationLockName);
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"另一个面板进程正在初始化持久化迁移，请等待该进程完成后重试：{lockPath}",
                ex);
        }
    }

    private static string BuildLegacyDatabaseMigrationMarkerPath(
        string writableRoot,
        string databasePath)
    {
        var identity = string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? ":memory:"
            : Path.GetFullPath(databasePath);
        if (OperatingSystem.IsWindows())
            identity = identity.ToUpperInvariant();

        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        return Path.Combine(
            writableRoot,
            $"{LegacyDatabaseMigrationMarkerPrefix}{hash}.complete");
    }

    private static bool CanCompleteLegacyMigration(string databasePath) =>
        string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase)
        || InspectSqliteDatabase(databasePath).IsValid;

    private static void ValidateCompletedMigrationDatabase(string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
            return;

        var inspection = InspectSqliteDatabase(databasePath);
        if (!inspection.IsValid)
        {
            throw new InvalidDataException(
                $"旧目录迁移已完成，但持久化数据库不可用；为防止回灌过期数据，已终止启动：{databasePath}；{inspection.Error}");
        }
    }

    private static void WriteLegacyMigrationMarker(string markerPath)
    {
        var directory = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException("无法确定持久化迁移标记目录");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(markerPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                var content = Encoding.UTF8.GetBytes("1\n");
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, markerPath, overwrite: false);
            }
            catch (IOException) when (ExpectedFileExists(markerPath))
            {
                // 并发启动已完成同一迁移时，现有标记即为最终结果。
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static IEnumerable<string> EnumerateLegacyBases(string contentRoot, string writableRoot)
    {
        var seen = new HashSet<string>(FileSystemPathComparer);

        foreach (var path in EnumerateCandidates())
        {
            var fullPath = Path.GetFullPath(path);

            if (seen.Add(fullPath))
                yield return fullPath;
        }

        IEnumerable<string> EnumerateCandidates()
        {
            yield return contentRoot;
            yield return writableRoot;

            if (DirectoryExistsStrict("/app"))
                yield return "/app";

            var parent = Directory.GetParent(Path.GetFullPath(contentRoot))?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                foreach (var directory in EnumerateVersionDirectories(parent))
                    yield return directory;
            }

            if (!string.Equals(parent, writableRoot, FileSystemPathComparison))
            {
                foreach (var directory in EnumerateVersionDirectories(writableRoot))
                    yield return directory;
            }
        }
    }

    private static IEnumerable<string> EnumerateVersionDirectories(string root)
    {
        if (!DirectoryExistsStrict(root))
            yield break;

        var directories = Directory.EnumerateDirectories(root, "app-previous*")
            .Concat(Directory.EnumerateDirectories(root, "app-current"))
            .OrderByDescending(GetDirectoryLastWriteTimeUtcStrict)
            .ToList();

        foreach (var directory in directories)
            yield return directory;
    }

    private static IEnumerable<string> EnumerateLegacyCandidates(
        IEnumerable<string> legacyBases,
        string relativePath)
    {
        foreach (var basePath in legacyBases)
        {
            if (!DirectoryExistsStrict(basePath))
            {
                throw new IOException(
                    $"旧数据候选目录在扫描期间消失或不再是目录：{basePath}");
            }

            yield return Path.GetFullPath(Path.Combine(basePath, relativePath));
        }
    }

    private static void TryMigrateSqliteDatabase(
        string targetPath,
        IEnumerable<string> candidates,
        Action<string>? report)
    {
        if (string.Equals(targetPath, ":memory:", StringComparison.OrdinalIgnoreCase))
            return;

        var targetFingerprintBefore = CaptureSqliteStorageFingerprint(targetPath);
        var target = InspectSqliteDatabase(targetPath);
        var targetFingerprint = CaptureSqliteStorageFingerprint(targetPath);
        if (target.IsRetryableFailure)
        {
            throw new InvalidOperationException(
                $"持久化数据库暂时无法读取，未执行旧数据迁移：{targetPath}；{target.Error}");
        }
        if (targetFingerprintBefore != targetFingerprint)
        {
            throw new InvalidOperationException(
                $"持久化数据库在检查期间发生变化，未执行旧数据迁移：{targetPath}");
        }

        var targetExists = target.Exists;
        var targetArtifactsExist = EnumerateSqliteArtifacts(targetPath).Any(PathExistsStrict);
        if (target.IsValid && target.HasBusinessData)
            return;

        var rankedCandidates = new List<SqliteDatabaseInspection>();
        var invalidCandidates = new List<string>();
        var retryableCandidates = new List<string>();
        var candidateFingerprints = new Dictionary<string, SqliteStorageFingerprint>(FileSystemPathComparer);
        foreach (var candidate in candidates.Distinct(FileSystemPathComparer))
        {
            if (SamePath(candidate, targetPath))
                continue;

            var inspection = InspectSqliteDatabase(candidate);
            if (!inspection.IsValid)
            {
                if (inspection.IsRetryableFailure)
                {
                    retryableCandidates.Add($"{candidate}（{inspection.Error}）");
                    report?.Invoke($"旧数据库候选暂时无法读取：{candidate}；{inspection.Error}");
                }
                else if (inspection.Exists)
                {
                    invalidCandidates.Add($"{candidate}（{inspection.Error}）");
                    report?.Invoke($"跳过不可用的旧数据库候选：{candidate}；{inspection.Error}");
                }

                continue;
            }

            if (inspection.HasBusinessData)
            {
                var fingerprintBefore = CaptureSqliteStorageFingerprint(candidate);
                var fingerprintAfter = CaptureSqliteStorageFingerprint(candidate);
                if (fingerprintBefore != fingerprintAfter)
                {
                    throw new IOException($"旧数据库候选在检查期间发生变化：{candidate}");
                }

                rankedCandidates.Add(inspection);
                candidateFingerprints[inspection.Path] = fingerprintAfter;
            }
            else
                report?.Invoke($"跳过没有业务数据的旧数据库候选：{candidate}");
        }

        if (retryableCandidates.Count > 0)
        {
            throw new InvalidOperationException(
                "旧数据库候选暂时无法读取；为避免回退到更旧快照，本次未执行迁移："
                + string.Join("；", retryableCandidates));
        }

        // 不能依赖目录枚举顺序：先选择仍含账号的快照，再以其它业务数据兜底。
        // 同类快照按最近写入时间恢复，不能用账号数量推断新旧，否则会复活已删除账号。
        rankedCandidates = rankedCandidates
            .OrderByDescending(item => item.AccountCount > 0)
            .ThenByDescending(item => item.HasAccountsTable)
            .ThenByDescending(item => item.LastWriteTimeUtc)
            .ThenBy(item => item.Path, FileSystemPathComparer)
            .ToList();

        if (rankedCandidates.Count == 0)
        {
            if (target.IsValid)
                return;

            if (invalidCandidates.Count > 0)
            {
                throw new InvalidDataException(
                    "找到旧数据库候选，但全部未通过完整性校验："
                    + string.Join("；", invalidCandidates));
            }

            if (targetArtifactsExist && (!target.IsValid || !targetExists))
            {
                throw new InvalidDataException(
                    $"持久化数据库不可用，且未找到可恢复的旧数据库：{targetPath}；{target.Error}");
            }

            return;
        }

        var directory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        Exception? lastFailure = null;
        foreach (var source in rankedCandidates)
        {
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(targetPath)}.migrating-{Guid.NewGuid():N}");
            try
            {
                if (CaptureSqliteStorageFingerprint(source.Path)
                    != candidateFingerprints[source.Path])
                {
                    throw new IOException($"旧数据库候选在恢复前发生变化：{source.Path}");
                }

                CreateValidatedSqliteSnapshot(source.Path, temporaryPath);
                if (CaptureSqliteStorageFingerprint(source.Path)
                    != candidateFingerprints[source.Path])
                {
                    throw new IOException($"旧数据库候选在快照复制期间发生变化：{source.Path}");
                }

                var restored = InspectSqliteDatabase(temporaryPath);
                if (!restored.Exists || restored.IsRetryableFailure)
                    throw new IOException($"数据库恢复快照暂时无法读取：{restored.Error}");
                if (!restored.IsValid
                    || !restored.HasBusinessData
                    || restored.AccountCount != source.AccountCount)
                {
                    throw new InvalidDataException("数据库恢复快照校验失败");
                }

                report?.Invoke(
                    $"准备提升旧数据库快照：{source.Path} -> {targetPath}");
                EnsureSqliteTargetUnchanged(
                    targetPath,
                    target,
                    targetFingerprint);
                PromoteSqliteSnapshot(temporaryPath, targetPath);
                report?.Invoke(
                    $"已从旧版本目录恢复持久化数据库（Accounts={source.AccountCount}）：{source.Path} -> {targetPath}");
                return;
            }
            catch (Exception ex) when (IsRetryableStorageException(ex))
            {
                throw new InvalidOperationException(
                    $"旧数据库候选在恢复期间暂时无法读取；为避免回退到更旧快照，本次未执行迁移：{source.Path}；{ex.Message}",
                    ex);
            }
            catch (InvalidDataException ex)
            {
                lastFailure = ex;
                report?.Invoke($"恢复数据库失败：{source.Path} -> {targetPath}；{ex.Message}");
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }

        throw new InvalidOperationException(
            $"恢复持久化数据库失败：{targetPath}；{lastFailure?.Message}",
            lastFailure);
    }

    private static void TryMigrateCredentialsFile(
        string targetPath,
        IEnumerable<string> candidates,
        IConfiguration configuration,
        Action<string>? report)
    {
        var initialUsername = (configuration["AdminAuth:InitialUsername"] ?? "tgpanel").Trim();
        // 保留旧版固定默认口令识别合同；空配置表示新版本随机初始化。
        var initialPassword = (configuration["AdminAuth:InitialPassword"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(initialPassword))
            initialPassword = "tgpanel123";
        var targetFingerprintBefore = CaptureFileFingerprint(targetPath, includeContentHash: true);
        var target = InspectCredentials(targetPath, initialUsername, initialPassword);
        var targetFingerprint = CaptureFileFingerprint(targetPath, includeContentHash: true);
        if (target.IsRetryableFailure)
        {
            throw new InvalidOperationException(
                $"持久化后台凭据暂时无法读取，未执行旧数据迁移：{targetPath}；{target.Error}");
        }
        if (targetFingerprintBefore != targetFingerprint)
        {
            throw new InvalidOperationException(
                $"持久化后台凭据在检查期间发生变化，未执行旧数据迁移：{targetPath}");
        }

        var targetExists = target.Exists;
        // 只有能明确证明是当前配置生成的默认凭据才允许自动替换；
        // 其它有效文件都按用户凭据保留，避免配置变化后误判并覆盖。
        if (target.IsValid && !target.IsGeneratedDefault)
            return;

        var rankedCandidates = new List<CredentialInspection>();
        var invalidCandidates = new List<string>();
        var retryableCandidates = new List<string>();
        var candidateFingerprints = new Dictionary<string, StorageFileFingerprint>(FileSystemPathComparer);
        foreach (var candidate in candidates.Distinct(FileSystemPathComparer))
        {
            if (SamePath(candidate, targetPath))
                continue;

            var inspection = InspectCredentials(candidate, initialUsername, initialPassword);
            if (!inspection.IsValid)
            {
                if (inspection.IsRetryableFailure)
                {
                    retryableCandidates.Add($"{candidate}（{inspection.Error}）");
                    report?.Invoke($"旧后台凭据候选暂时无法读取：{candidate}；{inspection.Error}");
                }
                else if (inspection.Exists)
                {
                    invalidCandidates.Add($"{candidate}（{inspection.Error}）");
                    report?.Invoke($"跳过不可用的旧后台凭据候选：{candidate}；{inspection.Error}");
                }

                continue;
            }

            // 目标是新生成的默认凭据时，只允许用户已修改过的来源覆盖它。
            if (targetExists && target.IsValid && target.IsGeneratedDefault && !inspection.IsUserModified)
            {
                report?.Invoke($"跳过未修改的默认后台凭据候选：{candidate}");
                continue;
            }

            var fingerprintBefore = CaptureFileFingerprint(candidate, includeContentHash: true);
            var fingerprintAfter = CaptureFileFingerprint(candidate, includeContentHash: true);
            if (fingerprintBefore != fingerprintAfter)
            {
                throw new IOException($"旧后台凭据候选在检查期间发生变化：{candidate}");
            }

            rankedCandidates.Add(inspection);
            candidateFingerprints[inspection.Path] = fingerprintAfter;
        }

        if (retryableCandidates.Count > 0)
        {
            throw new InvalidOperationException(
                "旧后台凭据候选暂时无法读取；为避免跳过可恢复凭据，本次未执行迁移："
                + string.Join("；", retryableCandidates));
        }

        var sourceCandidates = rankedCandidates
            .OrderByDescending(candidate => candidate.IsUserModified)
            .ThenByDescending(candidate => candidate.UpdatedAtUtc)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.Path, FileSystemPathComparer)
            .ToList();

        if (sourceCandidates.Count == 0)
        {
            if (target.IsValid)
                return;

            if (invalidCandidates.Count > 0)
            {
                throw new InvalidDataException(
                    "找到旧后台凭据候选，但全部未通过校验："
                    + string.Join("；", invalidCandidates));
            }

            if (targetExists && !target.IsValid)
            {
                throw new InvalidDataException(
                    $"持久化后台凭据不可用，且未找到可恢复的旧凭据：{targetPath}；{target.Error}");
            }

            return;
        }

        var directory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        Exception? lastFailure = null;
        foreach (var source in sourceCandidates)
        {
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(targetPath)}.migrating-{Guid.NewGuid():N}");
            try
            {
                if (CaptureFileFingerprint(source.Path, includeContentHash: true)
                    != candidateFingerprints[source.Path])
                {
                    throw new IOException($"旧后台凭据候选在恢复前发生变化：{source.Path}");
                }

                File.Copy(source.Path, temporaryPath, overwrite: false);
                var restoredFingerprint = CaptureFileFingerprint(
                    temporaryPath,
                    includeContentHash: true);
                if (restoredFingerprint.ContentHash
                    != candidateFingerprints[source.Path].ContentHash)
                {
                    throw new IOException($"旧后台凭据候选在复制期间发生变化：{source.Path}");
                }

                var restored = InspectCredentials(temporaryPath, initialUsername, initialPassword);
                if (!restored.Exists || restored.IsRetryableFailure)
                    throw new IOException($"后台凭据快照暂时无法读取：{restored.Error}");
                if (!restored.IsValid)
                    throw new InvalidDataException($"后台凭据快照校验失败：{restored.Error}");

                report?.Invoke(
                    $"准备提升旧后台凭据快照：{source.Path} -> {targetPath}");
                EnsureCredentialTargetUnchanged(
                    targetPath,
                    target,
                    targetFingerprint,
                    initialUsername,
                    initialPassword);
                PromoteFileSnapshot(temporaryPath, targetPath);
                var backupMessage = targetExists
                    ? "（已保留原凭据备份）"
                    : string.Empty;
                report?.Invoke(
                    $"已从旧版本目录恢复后台凭据{backupMessage}：{source.Path} -> {targetPath}");
                return;
            }
            catch (Exception ex) when (IsRetryableStorageException(ex))
            {
                throw new InvalidOperationException(
                    $"旧后台凭据候选在恢复期间暂时无法读取；本次未执行迁移：{source.Path}；{ex.Message}",
                    ex);
            }
            catch (InvalidDataException ex)
            {
                lastFailure = ex;
                report?.Invoke($"恢复后台凭据失败：{source.Path} -> {targetPath}；{ex.Message}");
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }

        throw new InvalidOperationException(
            $"恢复后台凭据失败：{targetPath}；{lastFailure?.Message}",
            lastFailure);
    }

    private static void EnsureSqliteTargetUnchanged(
        string targetPath,
        SqliteDatabaseInspection initial,
        SqliteStorageFingerprint expectedFingerprint)
    {
        var fingerprintBefore = CaptureSqliteStorageFingerprint(targetPath);
        var current = InspectSqliteDatabase(targetPath);
        var fingerprintAfter = CaptureSqliteStorageFingerprint(targetPath);
        if (current.IsRetryableFailure)
            throw new IOException($"持久化数据库暂时无法复验：{targetPath}；{current.Error}");

        if (fingerprintBefore != fingerprintAfter
            || expectedFingerprint != fingerprintBefore
            || current.Exists != initial.Exists
            || current.IsValid != initial.IsValid
            || current.HasAccountsTable != initial.HasAccountsTable
            || current.AccountCount != initial.AccountCount
            || current.HasBusinessData != initial.HasBusinessData
            || current.FailureKind != initial.FailureKind)
        {
            throw new IOException(
                $"持久化数据库在旧快照恢复期间发生变化，已取消覆盖：{targetPath}");
        }

        if (current.IsValid && current.HasBusinessData)
        {
            throw new IOException(
                $"持久化数据库已出现业务数据，已取消旧快照覆盖：{targetPath}");
        }
    }

    private static void EnsureCredentialTargetUnchanged(
        string targetPath,
        CredentialInspection initial,
        StorageFileFingerprint expectedFingerprint,
        string initialUsername,
        string initialPassword)
    {
        var fingerprintBefore = CaptureFileFingerprint(targetPath, includeContentHash: true);
        var current = InspectCredentials(targetPath, initialUsername, initialPassword);
        var fingerprintAfter = CaptureFileFingerprint(targetPath, includeContentHash: true);
        if (current.IsRetryableFailure)
            throw new IOException($"持久化后台凭据暂时无法复验：{targetPath}；{current.Error}");

        if (fingerprintBefore != fingerprintAfter
            || expectedFingerprint != fingerprintBefore
            || current.Exists != initial.Exists
            || current.IsValid != initial.IsValid
            || current.IsUserModified != initial.IsUserModified
            || current.IsGeneratedDefault != initial.IsGeneratedDefault
            || current.FailureKind != initial.FailureKind)
        {
            throw new IOException(
                $"持久化后台凭据在旧快照恢复期间发生变化，已取消覆盖：{targetPath}");
        }

        if (current.IsValid && !current.IsGeneratedDefault)
        {
            throw new IOException(
                $"持久化后台凭据已由用户修改，已取消旧凭据覆盖：{targetPath}");
        }
    }

    private static void TryMigrateDirectory(
        string targetPath,
        IEnumerable<string> candidates,
        Action<string>? report)
    {
        var expandedCandidates = candidates
            .SelectMany(path => new[] { path, $"{path}.before-persistent" })
            .Distinct(FileSystemPathComparer);

        foreach (var sourcePath in expandedCandidates)
        {
            if (SamePath(sourcePath, targetPath)
                || !DirectoryExistsStrict(sourcePath)
                || !DirectoryHasEntries(sourcePath))
            {
                continue;
            }

            var copiedFiles = CopyDirectoryWithoutOverwrite(sourcePath, targetPath);
            if (copiedFiles > 0)
            {
                report?.Invoke(
                    $"已从旧版本目录恢复 Session：{sourcePath} -> {targetPath}（{copiedFiles} 个文件）");
            }
        }

        Directory.CreateDirectory(targetPath);
    }

    private static int CopyDirectoryWithoutOverwrite(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        var copiedFiles = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath))
        {
            var targetFile = Path.Combine(targetPath, Path.GetFileName(sourceFile));
            if (!ExpectedFileExists(targetFile))
            {
                CopyFileAtomically(sourceFile, targetFile);
                copiedFiles++;
            }
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(sourcePath))
        {
            if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
                continue;

            var targetDirectory = Path.Combine(targetPath, Path.GetFileName(sourceDirectory));
            Directory.CreateDirectory(targetDirectory);
            copiedFiles += CopyDirectoryWithoutOverwrite(sourceDirectory, targetDirectory);
        }

        return copiedFiles;
    }

    private static bool DirectoryHasEntries(string path)
    {
        return DirectoryExistsStrict(path) && Directory.EnumerateFileSystemEntries(path).Any();
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                FileSystemPathComparison);
        }
        catch
        {
            return false;
        }
    }

    private static void CreateValidatedSqliteSnapshot(string sourcePath, string temporaryPath)
    {
        var sourceInspection = InspectSqliteDatabase(sourcePath);
        if (!sourceInspection.Exists || sourceInspection.IsRetryableFailure)
            throw new IOException($"旧数据库暂时无法读取：{sourcePath}；{sourceInspection.Error}");
        if (!sourceInspection.IsValid)
            throw new InvalidDataException($"旧数据库不可用：{sourcePath}；{sourceInspection.Error}");

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5,
            Pooling = false
        };
        var targetBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = temporaryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5,
            Pooling = false
        };

        using (var source = new SqliteConnection(sourceBuilder.ToString()))
        using (var target = new SqliteConnection(targetBuilder.ToString()))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);

            using var journalMode = target.CreateCommand();
            journalMode.CommandText = "PRAGMA journal_mode=DELETE;";
            _ = journalMode.ExecuteScalar();
        }

        var snapshotInspection = InspectSqliteDatabase(temporaryPath);
        if (!snapshotInspection.Exists || snapshotInspection.IsRetryableFailure)
            throw new IOException($"数据库快照暂时无法读取：{snapshotInspection.Error}");
        if (!snapshotInspection.IsValid)
            throw new InvalidDataException($"数据库快照校验失败：{snapshotInspection.Error}");

        using var stream = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static SqliteDatabaseInspection InspectSqliteDatabase(string path)
    {
        try
        {
            var entryKind = GetPathEntryKind(path);
            if (entryKind == PathEntryKind.Missing)
                return SqliteDatabaseInspection.Missing(path);
            if (entryKind == PathEntryKind.Directory)
                return SqliteDatabaseInspection.Invalid(path, "路径是目录", default);

            var lastWriteTimeUtc = GetSqliteLastWriteTimeUtc(path);
            if (new FileInfo(path).Length == 0)
                return SqliteDatabaseInspection.Invalid(path, "文件为空", lastWriteTimeUtc);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 5,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using (var quickCheck = connection.CreateCommand())
            {
                quickCheck.CommandText = "PRAGMA quick_check;";
                using var reader = quickCheck.ExecuteReader();
                var receivedResult = false;
                while (reader.Read())
                {
                    receivedResult = true;
                    var result = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        return SqliteDatabaseInspection.Invalid(
                            path,
                            $"quick_check 返回：{result}",
                            lastWriteTimeUtc);
                    }
                }

                if (!receivedResult)
                    return SqliteDatabaseInspection.Invalid(path, "quick_check 无返回结果", lastWriteTimeUtc);
            }

            var tableNames = new List<string>();
            using (var tableCommand = connection.CreateCommand())
            {
                tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                using var reader = tableCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        tableNames.Add(reader.GetString(0));
                }
            }

            var businessTables = tableNames
                .Where(name => !name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                .Where(name => !string.Equals(name, "__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (businessTables.Count == 0)
                return SqliteDatabaseInspection.Invalid(path, "数据库不包含业务表", lastWriteTimeUtc);

            var accountsTable = businessTables.FirstOrDefault(
                name => string.Equals(name, "Accounts", StringComparison.OrdinalIgnoreCase));
            var accountCount = accountsTable == null
                ? 0
                : CountTableRows(connection, accountsTable);
            var hasBusinessData = accountCount > 0;

            foreach (var tableName in businessTables)
            {
                if (accountsTable != null
                    && string.Equals(tableName, accountsTable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TableHasRows(connection, tableName))
                {
                    hasBusinessData = true;
                    break;
                }
            }

            return new SqliteDatabaseInspection(
                path,
                IsValid: true,
                HasAccountsTable: accountsTable != null,
                AccountCount: accountCount,
                HasBusinessData: hasBusinessData,
                LastWriteTimeUtc: lastWriteTimeUtc,
                Error: string.Empty,
                FailureKind: InspectionFailureKind.None);
        }
        catch (FileNotFoundException)
        {
            return SqliteDatabaseInspection.Retryable(
                path,
                "文件在检查期间消失",
                GetLastWriteTimeOrDefault(path));
        }
        catch (DirectoryNotFoundException)
        {
            return SqliteDatabaseInspection.Retryable(
                path,
                "目录在检查期间消失",
                GetLastWriteTimeOrDefault(path));
        }
        catch (Exception ex)
        {
            var lastWriteTimeUtc = GetLastWriteTimeOrDefault(path);
            if (IsRetryableStorageException(ex))
                return SqliteDatabaseInspection.Retryable(path, ex.Message, lastWriteTimeUtc);

            if (ex is SqliteException sqliteException
                && (sqliteException.SqliteErrorCode & 0xff) is 11 or 26)
            {
                return SqliteDatabaseInspection.Invalid(path, ex.Message, lastWriteTimeUtc);
            }

            throw;
        }
    }

    private static long CountTableRows(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{EscapeIdentifier(tableName)}\";";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableHasRows(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM \"{EscapeIdentifier(tableName)}\" LIMIT 1);";
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static string EscapeIdentifier(string value) => value.Replace("\"", "\"\"");

    private static DateTime GetSqliteLastWriteTimeUtc(string path)
    {
        var lastWriteTimeUtc = ExpectedFileExists(path)
            ? File.GetLastWriteTimeUtc(path)
            : default;
        var walPath = path + "-wal";
        if (ExpectedFileExists(walPath))
            lastWriteTimeUtc = lastWriteTimeUtc >= File.GetLastWriteTimeUtc(walPath)
                ? lastWriteTimeUtc
                : File.GetLastWriteTimeUtc(walPath);

        return lastWriteTimeUtc;
    }

    private static CredentialInspection InspectCredentials(
        string path,
        string initialUsername,
        string initialPassword)
    {
        try
        {
            var entryKind = GetPathEntryKind(path);
            if (entryKind == PathEntryKind.Missing)
                return CredentialInspection.Missing(path);
            if (entryKind == PathEntryKind.Directory)
                return CredentialInspection.Invalid(path, "路径是目录", default);

            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);

            if (new FileInfo(path).Length == 0)
                return CredentialInspection.Invalid(path, "文件为空", lastWriteTimeUtc);

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return CredentialInspection.Invalid(path, "JSON 根节点不是对象", lastWriteTimeUtc);

            if (TryGetProperty(root, "Version", out var versionProperty)
                && (versionProperty.ValueKind != JsonValueKind.Number
                    || !versionProperty.TryGetInt32(out _)))
            {
                return CredentialInspection.Invalid(path, "Version 无效", lastWriteTimeUtc);
            }

            if (!TryGetStringProperty(root, "Username", out var username)
                || string.IsNullOrWhiteSpace(username)
                || !string.Equals(username, username.Trim(), StringComparison.Ordinal))
            {
                return CredentialInspection.Invalid(path, "缺少有效的 Username", lastWriteTimeUtc);
            }

            if (!TryGetStringProperty(root, "SaltBase64", out var saltBase64)
                || !TryDecodeBase64(saltBase64, out var salt)
                || salt.Length < 8)
            {
                return CredentialInspection.Invalid(path, "SaltBase64 无效", lastWriteTimeUtc);
            }

            if (!TryGetStringProperty(root, "HashBase64", out var hashBase64)
                || !TryDecodeBase64(hashBase64, out var hash)
                || hash.Length != 32)
            {
                return CredentialInspection.Invalid(path, "HashBase64 无效", lastWriteTimeUtc);
            }

            if (!TryGetInt32Property(root, "Iterations", out var iterations)
                || iterations is < 1 or > 10_000_000)
            {
                return CredentialInspection.Invalid(path, "Iterations 无效", lastWriteTimeUtc);
            }

            // AdminCredentialFile 的运行时属性默认值为 true；缺省字段必须保持一致。
            var mustChangePassword = true;
            if (TryGetProperty(root, "MustChangePassword", out var mustChangeProperty))
            {
                if (mustChangeProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return CredentialInspection.Invalid(path, "MustChangePassword 无效", lastWriteTimeUtc);
                }

                mustChangePassword = mustChangeProperty.GetBoolean();
            }

            var isGeneratedDefault = false;
            if (mustChangePassword
                && string.Equals(username, initialUsername, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(initialPassword))
            {
                var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(initialPassword),
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    hash.Length);
                isGeneratedDefault = CryptographicOperations.FixedTimeEquals(hash, actualHash);
            }

            if (TryGetProperty(root, "CreatedAtUtc", out var createdAtProperty)
                && (createdAtProperty.ValueKind != JsonValueKind.String
                    || !createdAtProperty.TryGetDateTime(out _)))
            {
                return CredentialInspection.Invalid(path, "CreatedAtUtc 无效", lastWriteTimeUtc);
            }

            var updatedAtUtc = lastWriteTimeUtc;
            if (TryGetProperty(root, "UpdatedAtUtc", out var updatedAtProperty))
            {
                if (updatedAtProperty.ValueKind != JsonValueKind.String
                    || !updatedAtProperty.TryGetDateTime(out var parsedUpdatedAt))
                {
                    return CredentialInspection.Invalid(path, "UpdatedAtUtc 无效", lastWriteTimeUtc);
                }

                updatedAtUtc = parsedUpdatedAt.ToUniversalTime();
            }
            // MustChangePassword 由后台凭据服务在用户修改账号或密码后置为 false。
            // 旧配置生成的默认凭据可能与当前初始配置不同，不能因此被误判为用户凭据。
            return new CredentialInspection(
                path,
                IsValid: true,
                IsUserModified: !mustChangePassword,
                IsGeneratedDefault: isGeneratedDefault,
                UpdatedAtUtc: updatedAtUtc,
                LastWriteTimeUtc: lastWriteTimeUtc,
                Error: string.Empty,
                FailureKind: InspectionFailureKind.None);
        }
        catch (JsonException)
        {
            return CredentialInspection.Invalid(path, "JSON 格式无效", GetLastWriteTimeOrDefault(path));
        }
        catch (FileNotFoundException)
        {
            return CredentialInspection.Retryable(
                path,
                "文件在检查期间消失",
                GetLastWriteTimeOrDefault(path));
        }
        catch (DirectoryNotFoundException)
        {
            return CredentialInspection.Retryable(
                path,
                "目录在检查期间消失",
                GetLastWriteTimeOrDefault(path));
        }
        catch (Exception ex)
        {
            if (IsRetryableStorageException(ex))
            {
                return CredentialInspection.Retryable(
                    path,
                    ex.Message,
                    GetLastWriteTimeOrDefault(path));
            }

            throw;
        }
    }

    private static DateTime GetLastWriteTimeOrDefault(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch
        {
            return default;
        }
    }

    private static SqliteStorageFingerprint CaptureSqliteStorageFingerprint(string path)
    {
        // SHM 是可重建的 WAL 索引，读取数据库本身也可能更新它，不能用于内容身份判断。
        return new SqliteStorageFingerprint(
            CaptureFileFingerprint(path, includeContentHash: false),
            CaptureFileFingerprint(path + "-wal", includeContentHash: false),
            CaptureFileFingerprint(path + "-journal", includeContentHash: false));
    }

    private static StorageFileFingerprint CaptureFileFingerprint(
        string path,
        bool includeContentHash)
    {
        var entryKind = GetPathEntryKind(path);
        if (entryKind != PathEntryKind.File)
            return new StorageFileFingerprint(entryKind, 0, default, default, string.Empty);

        var before = new FileInfo(path);
        before.Refresh();
        var length = before.Length;
        var creationTimeUtc = before.CreationTimeUtc;
        var lastWriteTimeUtc = before.LastWriteTimeUtc;
        var contentHash = includeContentHash
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            : string.Empty;

        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists
            || after.Length != length
            || after.CreationTimeUtc != creationTimeUtc
            || after.LastWriteTimeUtc != lastWriteTimeUtc)
        {
            throw new IOException($"文件在生成迁移指纹期间发生变化：{path}");
        }

        return new StorageFileFingerprint(
            PathEntryKind.File,
            length,
            creationTimeUtc,
            lastWriteTimeUtc,
            contentHash);
    }

    private static PathEntryKind GetPathEntryKind(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                ? PathEntryKind.Directory
                : PathEntryKind.File;
        }
        catch (FileNotFoundException)
        {
            return PathEntryKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PathEntryKind.Missing;
        }
    }

    private static bool ExpectedFileExists(string path)
    {
        return GetPathEntryKind(path) switch
        {
            PathEntryKind.Missing => false,
            PathEntryKind.File => true,
            _ => throw new InvalidDataException($"预期文件路径实际为目录：{path}")
        };
    }

    private static bool DirectoryExistsStrict(string path) =>
        GetPathEntryKind(path) == PathEntryKind.Directory;

    private static DateTime GetDirectoryLastWriteTimeUtcStrict(string path)
    {
        if (!DirectoryExistsStrict(path))
            throw new DirectoryNotFoundException($"旧数据候选目录不存在：{path}");

        return Directory.GetLastWriteTimeUtc(path);
    }

    private static bool PathExistsStrict(string path) =>
        GetPathEntryKind(path) != PathEntryKind.Missing;

    private static bool IsRetryableStorageException(Exception exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException)
            return false;

        if (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return true;
        }

        if (exception is SqliteException sqliteException)
        {
            var primaryCode = sqliteException.SqliteErrorCode & 0xff;
            return primaryCode is
                3   // 权限错误（SQLITE_PERM）
                or 5   // 资源繁忙（SQLITE_BUSY）
                or 6   // 数据库锁定（SQLITE_LOCKED）
                or 8   // 只读限制（SQLITE_READONLY）
                or 9   // 操作中断（SQLITE_INTERRUPT）
                or 10  // 输入输出错误（SQLITE_IOERR）
                or 13  // 存储空间不足（SQLITE_FULL）
                or 14  // 无法打开文件（SQLITE_CANTOPEN）
                or 15  // 锁协议错误（SQLITE_PROTOCOL）
                or 23; // 授权失败（SQLITE_AUTH）
        }

        return exception.InnerException != null
            && IsRetryableStorageException(exception.InnerException);
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
        => root.TryGetProperty(name, out value);

    private static bool TryGetStringProperty(JsonElement root, string name, out string value)
    {
        if (TryGetProperty(root, name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt32Property(JsonElement root, string name, out int value)
    {
        if (TryGetProperty(root, name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryDecodeBase64(string value, out byte[] decoded)
    {
        try
        {
            decoded = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            decoded = Array.Empty<byte>();
            return false;
        }
    }

    private static void PromoteSqliteSnapshot(string temporaryPath, string targetPath)
    {
        var backupSuffix = $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var movedArtifacts = new List<(string Original, string Backup)>();

        try
        {
            foreach (var artifact in EnumerateSqliteArtifacts(targetPath))
            {
                if (!ExpectedFileExists(artifact))
                    continue;

                var backup = artifact + backupSuffix;
                File.Move(artifact, backup, overwrite: false);
                movedArtifacts.Add((artifact, backup));
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        catch
        {
            for (var i = movedArtifacts.Count - 1; i >= 0; i--)
            {
                var (original, backup) = movedArtifacts[i];
                if (!ExpectedFileExists(original) && ExpectedFileExists(backup))
                    File.Move(backup, original, overwrite: false);
            }

            throw;
        }
    }

    private static void PromoteFileSnapshot(string temporaryPath, string targetPath)
    {
        string? backupPath = null;
        try
        {
            if (ExpectedFileExists(targetPath))
            {
                backupPath = targetPath
                    + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                File.Move(targetPath, backupPath, overwrite: false);
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        catch
        {
            if (!ExpectedFileExists(targetPath)
                && !string.IsNullOrWhiteSpace(backupPath)
                && ExpectedFileExists(backupPath))
            {
                File.Move(backupPath, targetPath, overwrite: false);
            }

            throw;
        }
    }

    private static IEnumerable<string> EnumerateSqliteArtifacts(string databasePath)
    {
        yield return databasePath;
        yield return databasePath + "-wal";
        yield return databasePath + "-shm";
        yield return databasePath + "-journal";
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不覆盖原始迁移异常。
        }
    }

    private static void CopyFileAtomically(string sourcePath, string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.migrating-{Guid.NewGuid():N}");

        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private sealed record SqliteDatabaseInspection(
        string Path,
        bool IsValid,
        bool HasAccountsTable,
        long AccountCount,
        bool HasBusinessData,
        DateTime LastWriteTimeUtc,
        string Error,
        InspectionFailureKind FailureKind)
    {
        public bool Exists => FailureKind != InspectionFailureKind.Missing;
        public bool IsRetryableFailure => FailureKind == InspectionFailureKind.Retryable;

        public static SqliteDatabaseInspection Missing(string path) =>
            new(
                path,
                false,
                false,
                0,
                false,
                default,
                "文件不存在",
                InspectionFailureKind.Missing);

        public static SqliteDatabaseInspection Invalid(
            string path,
            string error,
            DateTime lastWriteTimeUtc) =>
            new(
                path,
                false,
                false,
                0,
                false,
                lastWriteTimeUtc,
                error,
                InspectionFailureKind.Permanent);

        public static SqliteDatabaseInspection Retryable(
            string path,
            string error,
            DateTime lastWriteTimeUtc) =>
            new(
                path,
                false,
                false,
                0,
                false,
                lastWriteTimeUtc,
                error,
                InspectionFailureKind.Retryable);
    }

    private sealed record CredentialInspection(
        string Path,
        bool IsValid,
        bool IsUserModified,
        bool IsGeneratedDefault,
        DateTime UpdatedAtUtc,
        DateTime LastWriteTimeUtc,
        string Error,
        InspectionFailureKind FailureKind)
    {
        public bool Exists => FailureKind != InspectionFailureKind.Missing;
        public bool IsRetryableFailure => FailureKind == InspectionFailureKind.Retryable;

        public static CredentialInspection Missing(string path) =>
            new(
                path,
                false,
                false,
                false,
                default,
                default,
                "文件不存在",
                InspectionFailureKind.Missing);

        public static CredentialInspection Invalid(
            string path,
            string error,
            DateTime lastWriteTimeUtc) =>
            new(
                path,
                false,
                false,
                false,
                default,
                lastWriteTimeUtc,
                error,
                InspectionFailureKind.Permanent);

        public static CredentialInspection Retryable(
            string path,
            string error,
            DateTime lastWriteTimeUtc) =>
            new(
                path,
                false,
                false,
                false,
                default,
                lastWriteTimeUtc,
                error,
                InspectionFailureKind.Retryable);
    }

    private sealed record SqliteStorageFingerprint(
        StorageFileFingerprint Database,
        StorageFileFingerprint Wal,
        StorageFileFingerprint Journal);

    private sealed record StorageFileFingerprint(
        PathEntryKind EntryKind,
        long Length,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc,
        string ContentHash);

    private enum InspectionFailureKind
    {
        None,
        Missing,
        Permanent,
        Retryable
    }

    private enum PathEntryKind
    {
        Missing,
        File,
        Directory
    }
}
