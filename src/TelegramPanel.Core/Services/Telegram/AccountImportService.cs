using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Utils;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using System.IO.Compression;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 账号导入协调服务 - 整合Session导入和数据库保存
/// </summary>
public class AccountImportService
{
    public const string ManagedWarpRequestPrefix = "telegram-panel.internal.import.";
    public const int MaxWarpPerAccountImportCount = 10;
    private const int MaxZipEntryCount = 5_000;
    private const long MaxZipEntryBytes = 100L * 1024 * 1024;
    private const long MaxZipExtractedBytes = 1024L * 1024 * 1024;

    private readonly ISessionImporter _sessionImporter;
    private readonly AppDbContext _db;
    private readonly AccountManagementService _accountManagement;
    private readonly ILogger<AccountImportService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProxyManagementService _proxyManagement;
    private readonly TemporaryWarpClaimStore _temporaryWarpClaims;
    private readonly IWarpProxyUsageGuard? _warpProxyUsageGuard;
    private readonly TelegramApiProfilePool _apiProfilePool;

    public static bool IsManagedWarpRequestId(string? requestId) =>
        !string.IsNullOrWhiteSpace(requestId)
        && requestId.Trim().StartsWith(
            ManagedWarpRequestPrefix,
            StringComparison.OrdinalIgnoreCase);

    public AccountImportService(
        ISessionImporter sessionImporter,
        AppDbContext db,
        AccountManagementService accountManagement,
        ILogger<AccountImportService> logger,
        IConfiguration configuration,
        ProxyManagementService proxyManagement,
        TemporaryWarpClaimStore temporaryWarpClaims,
        IWarpProxyUsageGuard? warpProxyUsageGuard = null,
        TelegramApiProfilePool? apiProfilePool = null)
    {
        _sessionImporter = sessionImporter;
        _db = db;
        _accountManagement = accountManagement;
        _logger = logger;
        _configuration = configuration;
        _proxyManagement = proxyManagement;
        _temporaryWarpClaims = temporaryWarpClaims;
        _warpProxyUsageGuard = warpProxyUsageGuard;
        _apiProfilePool = apiProfilePool ?? new TelegramApiProfilePool(configuration);
    }

    /// <summary>
    /// 从浏览器上传的文件导入账号（Blazor 入口，委托给 Stream 通用入口）。
    /// </summary>
    public async Task<List<ImportResult>> ImportFromBrowserFilesAsync(
        IReadOnlyList<IBrowserFile> files,
        int apiId,
        string apiHash,
        int? categoryId = null,
        AccountProxyBindingInput? proxyBinding = null,
        CancellationToken cancellationToken = default,
        string? deviceProfileKey = null)
    {
        var results = new List<ImportResult>();
        if (IsWarpPerAccountStrategy(proxyBinding) && files.Count > MaxWarpPerAccountImportCount)
        {
            throw new ArgumentException(
                $"创建一对一 WARP 单次最多导入 {MaxWarpPerAccountImportCount} 个账号，当前选择了 {files.Count} 个");
        }

        var importedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            await using var upload = file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024);
            results.Add(await ExecuteImportWithProxyAsync(
                file.Name,
                proxyBinding,
                importedPhones,
                    proxy => ImportFromSessionFileStreamAsync(
                        file.Name,
                        upload,
                        apiId,
                        apiHash,
                        proxy,
                        cancellationToken,
                        deviceProfileKey),
                cancellationToken,
                result => PersistImportedSessionAsync(
                    result,
                    apiId,
                    apiHash,
                    categoryId,
                    twoFactorPassword: null,
                    deviceProfileKey)));
            await Task.Delay(500, cancellationToken);
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Browser file import completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    /// <summary>
    /// 从通用 Stream 上传导入账号（Minimal API/Vue 入口）。
    /// </summary>
    public async Task<List<ImportResult>> ImportFromSessionFileStreamsAsync(
        IReadOnlyList<AccountImportFile> files,
        int apiId,
        string apiHash,
        int? categoryId = null,
        AccountProxyBindingInput? proxyBinding = null,
        CancellationToken cancellationToken = default,
        string? deviceProfileKey = null)
    {
        var results = new List<ImportResult>();
        if (IsWarpPerAccountStrategy(proxyBinding) && files.Count > MaxWarpPerAccountImportCount)
        {
            throw new ArgumentException(
                $"创建一对一 WARP 单次最多导入 {MaxWarpPerAccountImportCount} 个账号，当前选择了 {files.Count} 个");
        }

        var importedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            results.Add(await ExecuteImportWithProxyAsync(
                file.FileName,
                proxyBinding,
                importedPhones,
                proxy => ImportFromSessionFileStreamAsync(
                    file.FileName,
                    file.Content,
                    apiId,
                    apiHash,
                    proxy,
                    cancellationToken,
                    deviceProfileKey),
                cancellationToken,
                result => PersistImportedSessionAsync(
                    result,
                    apiId,
                    apiHash,
                    categoryId,
                    twoFactorPassword: null,
                    deviceProfileKey)));
            await Task.Delay(500, cancellationToken);
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Stream file import completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    public async Task<List<ImportResult>> ImportFromSessionFileStreamsBalancedAsync(
        IReadOnlyList<AccountImportFile> files,
        int? categoryId = null,
        AccountProxyBindingInput? proxyBinding = null,
        CancellationToken cancellationToken = default,
        string? deviceProfileKey = null)
    {
        var results = new List<ImportResult>();
        if (IsWarpPerAccountStrategy(proxyBinding) && files.Count > MaxWarpPerAccountImportCount)
        {
            throw new ArgumentException(
                $"创建一对一 WARP 单次最多导入 {MaxWarpPerAccountImportCount} 个账号，当前选择了 {files.Count} 个");
        }

        var importedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var api = await _apiProfilePool.SelectForNewAccountAsync(_accountManagement);
            results.Add(await ExecuteImportWithProxyAsync(
                file.FileName,
                proxyBinding,
                importedPhones,
                proxy => ImportFromSessionFileStreamAsync(
                    file.FileName,
                    file.Content,
                    api.ApiId,
                    api.ApiHash,
                    proxy,
                    cancellationToken,
                    deviceProfileKey),
                cancellationToken,
                result => PersistImportedSessionAsync(
                    result,
                    api.ApiId,
                    api.ApiHash,
                    categoryId,
                    twoFactorPassword: null,
                    deviceProfileKey)));
            await Task.Delay(500, cancellationToken);
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Balanced stream file import completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    private async Task<ImportResult> ImportFromSessionFileStreamAsync(
        string fileName,
        Stream content,
        int apiId,
        string apiHash,
        ProxyConnectionOptions? proxy,
        CancellationToken cancellationToken,
        string? deviceProfileKey)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
            safeFileName = "upload.session";

        var tempDir = Path.Combine(Path.GetTempPath(), $"telegram-panel-session-{Guid.NewGuid():N}");
        var tempPath = Path.Combine(tempDir, safeFileName);

        try
        {
            Directory.CreateDirectory(tempDir);
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(fileStream, cancellationToken);
            }

            var result = await ImportFromSessionFileDeferredAsync(
                tempPath,
                apiId,
                apiHash,
                proxy: proxy,
                deviceProfileKey: deviceProfileKey,
                cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process file: {FileName}", fileName);
            return new ImportResult(false, null, null, null, null, $"文件处理失败：{FormatException(ex)}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // 忽略临时文件删除失败
            }
        }
    }

    /// <summary>
    /// 从StringSession导入账号
    /// </summary>
    public async Task<ImportResult> ImportFromStringSessionAsync(
        string sessionString,
        int apiId,
        string apiHash,
        int? categoryId = null,
        AccountProxyBindingInput? proxyBinding = null,
        CancellationToken cancellationToken = default,
        string? deviceProfileKey = null)
    {
        return await ExecuteImportWithProxyAsync(
            "string-session",
            proxyBinding,
            importedPhones: null,
            async proxy =>
            {
                return await ImportFromStringSessionDeferredAsync(
                    sessionString,
                    apiId,
                    apiHash,
                    proxy,
                    cancellationToken,
                    deviceProfileKey);
            },
            cancellationToken,
            result => PersistImportedSessionAsync(
                result,
                apiId,
                apiHash,
                categoryId,
                twoFactorPassword: null,
                deviceProfileKey));
    }

    public async Task<ImportResult> ImportFromStringSessionBalancedAsync(
        string sessionString,
        int? categoryId = null,
        AccountProxyBindingInput? proxyBinding = null,
        CancellationToken cancellationToken = default,
        string? deviceProfileKey = null)
    {
        var api = await _apiProfilePool.SelectForNewAccountAsync(_accountManagement);
        return await ImportFromStringSessionAsync(
            sessionString,
            api.ApiId,
            api.ApiHash,
            categoryId,
            proxyBinding,
            cancellationToken,
            deviceProfileKey);
    }

    /// <summary>
    /// 从浏览器上传的 zip 压缩包导入账号（每个账号目录下包含一个 json + 一个 session）
    /// </summary>
    public async Task<List<ImportResult>> ImportFromZipAsync(
        IBrowserFile zipFile,
        int? categoryId = null,
        string? twoFactorPassword = null,
        AccountProxyBindingInput? proxyBinding = null,
        CancellationToken cancellationToken = default,
        string? perAccountProxyText = null,
        string? deviceProfileKey = null)
    {
        var results = new List<ImportResult>();

        if (zipFile == null)
        {
            results.Add(new ImportResult(false, null, null, null, null, "未选择压缩包文件"));
            return results;
        }

        await using var upload = zipFile.OpenReadStream(maxAllowedSize: 200 * 1024 * 1024);
        return await ImportFromZipStreamAsync(
            zipFile.Name,
            upload,
            categoryId,
            twoFactorPassword,
            proxyBinding,
            cancellationToken,
            perAccountProxyText,
            deviceProfileKey);
    }

    /// <summary>
    /// 从通用 Stream 上传的 zip 压缩包导入账号。
    /// </summary>
    public async Task<List<ImportResult>> ImportFromZipStreamAsync(
        string fileName,
        Stream zipStream,
        int? categoryId = null,
        string? twoFactorPassword = null,
        AccountProxyBindingInput? proxyBinding = null,
        CancellationToken cancellationToken = default,
        string? perAccountProxyText = null,
        string? deviceProfileKey = null)
    {
        var results = new List<ImportResult>();

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"telegram-panel-import-{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"telegram-panel-import-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(extractDir);

            await using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await zipStream.CopyToAsync(fs, cancellationToken);
            }

            // 注意：部分第三方打包工具会生成“目录条目但包含数据”的非标准 zip，
            // ZipFile.ExtractToDirectory 会直接抛异常。这里改为手动解压并容错处理。
            await ExtractZipToDirectorySafeAsync(tempZipPath, extractDir, cancellationToken);

            var tdataDirs = OrderImportPaths(extractDir, FindTdataDirectories(extractDir));
            var allJsonFiles = Directory.EnumerateFiles(extractDir, "*.json", SearchOption.AllDirectories).ToList();
            var jsonFiles = OrderImportPaths(
                extractDir,
                allJsonFiles.Where(path => !IsPathInsideAnyDirectory(path, tdataDirs)));
            var candidateCount = jsonFiles.Count > 0 ? jsonFiles.Count : tdataDirs.Count;
            var skippedTdataJsonCount = allJsonFiles.Count - jsonFiles.Count;
            if (skippedTdataJsonCount > 0)
            {
                _logger.LogInformation(
                    "Skipping {Count} json files inside tdata directories during zip import",
                    skippedTdataJsonCount);
            }
            if (IsWarpPerAccountStrategy(proxyBinding) && candidateCount > MaxWarpPerAccountImportCount)
            {
                throw new AccountImportProxyBatchException(
                    $"创建一对一 WARP 单次最多导入 {MaxWarpPerAccountImportCount} 个账号，压缩包识别到 {candidateCount} 个账号");
            }


            if (jsonFiles.Count == 0 && tdataDirs.Count == 0)
            {
                results.Add(new ImportResult(false, null, null, null, null, "压缩包内未找到任何账号配置 json 或可识别的 tdata 目录"));
                return results;
            }

            if (perAccountProxyText != null && proxyBinding != null)
                throw new AccountImportProxyBatchException("逐账号批量代理不能与其他代理策略同时使用");

            // 纯 tdata 导入需要系统配置至少一个可用 Telegram API。每个账号会在导入时
            // 按 API 配置池重新选择，避免整批固定在同一个 API 上。
            if (jsonFiles.Count == 0 && !_apiProfilePool.HasUsableApi())
            {
                results.Add(new ImportResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    "检测到 tdata 数据包，但系统未配置全局 Telegram API（ApiId/ApiHash）；请先到【系统设置】配置后再导入"));
                return results;
            }

            IReadOnlyList<PreparedAccountImportProxy>? proxyAssignments = null;
            if (perAccountProxyText != null)
            {
                proxyAssignments = await _proxyManagement.PreparePerAccountImportProxiesAsync(
                    perAccountProxyText,
                    candidateCount,
                    cancellationToken);
            }

            if (jsonFiles.Count == 0)
            {
                results.AddRange(await ImportFromTdataDirectoriesAsync(
                    tdataDirs,
                    extractDir,
                    categoryId,
                    twoFactorPassword,
                    proxyBinding,
                    proxyAssignments,
                    cancellationToken,
                    deviceProfileKey));
                var tdataSuccess = results.Count(r => r.Success);
                _logger.LogInformation("Tdata import completed: {Success}/{Total} successful", tdataSuccess, results.Count);
                return results;
            }

            var importedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < jsonFiles.Count; index++)
            {
                var jsonPath = jsonFiles[index];
                var assignment = proxyAssignments?[index];
                var effectiveBinding = BuildZipProxyBinding(proxyBinding, assignment);
                var result = await ExecuteImportWithProxyAsync(
                    Path.GetFileNameWithoutExtension(jsonPath),
                    effectiveBinding,
                    importedPhones: null,
                    proxy => ImportFromPackageEntryAsync(
                        jsonPath,
                        categoryId,
                        twoFactorPassword,
                        importedPhones,
                        proxy,
                        cancellationToken,
                        deviceProfileKey),
                    cancellationToken);
                results.Add(AttachZipImportMetadata(
                    result,
                    BuildImportSourceKey(extractDir, jsonPath),
                    assignment));
            }

            var successCount = results.Count(r => r.Success);
            _logger.LogInformation("Zip import completed: {Success}/{Total} successful", successCount, results.Count);
            return results;
        }
        catch (AccountImportProxyBatchException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zip import failed: {FileName}", fileName);
            results.Add(new ImportResult(false, null, null, null, null, $"压缩包导入失败: {ex.Message}"));
            return results;
        }
        finally
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
        }
    }

    private async Task ExtractZipToDirectorySafeAsync(
        string zipPath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var destRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destRoot);
        var destRootWithSep = destRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destRoot
            : destRoot + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxZipEntryCount)
            throw new InvalidDataException($"压缩包条目数超过上限 {MaxZipEntryCount}");

        long totalExtractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
                continue;

            var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            // 目录条目：Name 为空或以分隔符结尾
            if (string.IsNullOrEmpty(entry.Name) || normalized.EndsWith("/", StringComparison.Ordinal))
            {
                var dirRel = normalized.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(dirRel))
                    continue;

                var dirPath = Path.GetFullPath(Path.Combine(destRoot, dirRel));
                if (!dirPath.StartsWith(destRootWithSep, StringComparison.OrdinalIgnoreCase) && !string.Equals(dirPath, destRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"压缩包包含非法路径（Zip Slip）：{entry.FullName}");

                Directory.CreateDirectory(dirPath);
                continue;
            }

            var filePath = Path.GetFullPath(Path.Combine(destRoot, normalized));
            if (!filePath.StartsWith(destRootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"压缩包包含非法路径（Zip Slip）：{entry.FullName}");
            if (entry.Length > MaxZipEntryBytes)
                throw new InvalidDataException($"压缩包条目超过 100MB：{entry.FullName}");
            if (totalExtractedBytes + entry.Length > MaxZipExtractedBytes)
                throw new InvalidDataException("压缩包解压后总大小超过 1GB");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await using var entryStream = entry.Open();
            await using var outStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long entryExtractedBytes = 0;
            while (true)
            {
                var read = await entryStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                entryExtractedBytes += read;
                totalExtractedBytes += read;
                if (entryExtractedBytes > MaxZipEntryBytes)
                    throw new InvalidDataException($"压缩包条目超过 100MB：{entry.FullName}");
                if (totalExtractedBytes > MaxZipExtractedBytes)
                    throw new InvalidDataException("压缩包解压后总大小超过 1GB");
                await outStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
    }

    private async Task<ImportResult> ImportFromPackageEntryAsync(
        string jsonPath,
        int? categoryId,
        string? twoFactorPassword,
        HashSet<string>? importedPhones,
        ProxyConnectionOptions? proxy,
        CancellationToken cancellationToken,
        string? deviceProfileKey)
    {
        var databaseMutationStarted = false;
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath, cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetInt(root, out var apiId, "api_id", "app_id", "apiId", "appId"))
                return new ImportResult(false, null, null, null, null, $"json 缺少 api_id: {jsonPath}");

            if (!TryGetString(root, out var apiHash, "api_hash", "app_hash", "apiHash", "appHash") || string.IsNullOrWhiteSpace(apiHash))
                return new ImportResult(false, null, null, null, null, $"json 缺少 api_hash: {jsonPath}");

            if (!TryGetString(root, out var phone, "phone", "phone_number", "phoneNumber") || string.IsNullOrWhiteSpace(phone))
            {
                if (!TryInferPhone(root, jsonPath, out phone))
                    return new ImportResult(false, null, null, null, null, $"json 缺少 phone: {jsonPath}");
            }

            phone = PhoneNumberFormatter.NormalizeToDigits(phone);
            if (string.IsNullOrWhiteSpace(phone))
                return new ImportResult(false, null, null, null, null, $"json phone 无效: {jsonPath}");
            if (importedPhones?.Contains(phone) == true)
            {
                return new ImportResult(
                    false,
                    phone,
                    null,
                    null,
                    null,
                    "重复账号已跳过");
            }

            _ = TryGetLong(root, out var userId, "user_id", "uid", "userId");
            _ = TryGetString(root, out var username, "username");
            username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
            _ = TryGetString(root, out var firstName, "first_name", "firstName");
            _ = TryGetString(root, out var lastName, "last_name", "lastName");
            var nickname = BuildNickname(firstName, lastName, username);
            _ = TryGetString(root, out var sessionKey, "session_string", "sessionString");
            sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? null : sessionKey.Trim();

            var dir = Path.GetDirectoryName(jsonPath) ?? extractDirFallback();
            var baseName = Path.GetFileNameWithoutExtension(jsonPath);
            var sessionCandidate = Path.Combine(dir, $"{baseName}.session");
            if (!File.Exists(sessionCandidate))
            {
                sessionCandidate = Directory.EnumerateFiles(dir, "*.session", SearchOption.TopDirectoryOnly).FirstOrDefault()
                    ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(sessionCandidate) || !File.Exists(sessionCandidate))
            {
                return new ImportResult(false, phone, userId, username, null, "未找到对应的 .session 文件");
            }

            // 尝试从 2fa.txt 读取二级密码（优先于用户输入的统一密码）
            var twoFaFromFile = await TryRead2faFileAsync(dir);
            var effectiveTwoFactorPassword = !string.IsNullOrWhiteSpace(twoFaFromFile) ? twoFaFromFile : twoFactorPassword;

            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            Directory.CreateDirectory(sessionsPath);
            var targetSessionPath = Path.Combine(sessionsPath, $"{phone}.session");

            string? convertedSessionPath = null;
            try
            {
                var validatedSessionPath = sessionCandidate;

                // 有些来源的 .session 实际上是 SQLite（Telethon/Pyrogram/Telegram Desktop 等），WTelegram 不能直接读取。
                // 先转换到临时候选文件，数据库保存成功前不覆盖正式 Session。
                if (LooksLikeSqliteSession(sessionCandidate))
                {
                    convertedSessionPath = Path.Combine(
                        sessionsPath,
                        $".{phone}.package-convert-{Guid.NewGuid():N}.session");
                    SessionDataConverter.SessionConvertResult converted;
                    if (string.IsNullOrWhiteSpace(sessionKey))
                    {
                        // 没有 session_string 也不阻挡：直接从 sqlite 里取 dc/auth_key 转换为 WTelegram session
                        converted = await SessionDataConverter.TryCreateWTelegramSessionFromTelethonSqliteFileAsync(
                            sqliteSessionPath: sessionCandidate,
                            apiId: apiId,
                            apiHash: apiHash.Trim(),
                            targetSessionPath: convertedSessionPath,
                            phone: phone,
                            userId: userId,
                            logger: _logger,
                            proxy: proxy,
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        converted = await SessionDataConverter.TryCreateWTelegramSessionFromSessionStringAsync(
                            sessionString: sessionKey,
                            apiId: apiId,
                            apiHash: apiHash.Trim(),
                            targetSessionPath: convertedSessionPath,
                            phone: phone,
                            userId: userId,
                            logger: _logger,
                            proxy: proxy,
                            cancellationToken: cancellationToken);
                    }

                    if (!converted.Ok)
                    {
                        var reason = converted.Reason ?? "未知原因";
                        return new ImportResult(false, phone, userId, username, null,
                            $"该 .session 为 SQLite 格式，但转换/校验失败：{reason}（通常表示账号已掉线/被登出/会话失效，需要重新登录生成新 session）");
                    }

                    validatedSessionPath = convertedSessionPath;
                }

                using var replacement = AtomicSessionFileReplacement.Create(targetSessionPath);
                File.Copy(validatedSessionPath, replacement.StagingPath, overwrite: false);
                replacement.Apply();

                // 入库：存在则更新，不存在则创建。保存失败时 using 会自动恢复旧 Session。
                databaseMutationStarted = true;
                var existing = await _accountManagement.GetAccountByPhoneAsync(phone);
                if (existing != null)
                {
                    existing.UserId = userId ?? existing.UserId;
                    existing.Username = username ?? existing.Username;
                    existing.Nickname = nickname ?? existing.Nickname;
                    existing.SessionPath = targetSessionPath;
                    existing.ApiId = apiId;
                    existing.ApiHash = apiHash.Trim();
                    if (!string.IsNullOrWhiteSpace(deviceProfileKey))
                        existing.DeviceProfileKey = deviceProfileKey.Trim();
                    // 代理绑定完成前保持停用，避免后台任务在短暂窗口使用旧路由。
                    existing.IsActive = false;
                    existing.LastSyncAt = DateTime.UtcNow;

                    // 仅在提供了二级密码时更新，避免覆盖已有密码
                    if (!string.IsNullOrWhiteSpace(effectiveTwoFactorPassword))
                        existing.TwoFactorPassword = effectiveTwoFactorPassword.Trim();
                    await _accountManagement.UpdateAccountAsync(existing);
                }
                else
                {
                    var account = new Account
                    {
                        Phone = phone,
                        UserId = userId ?? 0,
                        Nickname = nickname,
                        Username = username,
                        SessionPath = targetSessionPath,
                        ApiId = apiId,
                        ApiHash = apiHash.Trim(),
                        DeviceProfileKey = string.IsNullOrWhiteSpace(deviceProfileKey) ? null : deviceProfileKey.Trim(),
                        IsActive = false,
                        CategoryId = categoryId,
                        TwoFactorPassword = string.IsNullOrWhiteSpace(effectiveTwoFactorPassword) ? null : effectiveTwoFactorPassword.Trim(),
                        CreatedAt = DateTime.UtcNow,
                        LastSyncAt = DateTime.UtcNow
                    };
                    await _accountManagement.CreateAccountAsync(account);
                }

                replacement.Commit();
                if (replacement.CleanupError != null)
                {
                    _logger.LogWarning(
                        replacement.CleanupError,
                        "Package Session committed but rollback backup cleanup is pending: {BackupPath}",
                        replacement.BackupPath);
                }
                importedPhones?.Add(phone);
                return new ImportResult(true, phone, userId, username, targetSessionPath, null);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(convertedSessionPath) && File.Exists(convertedSessionPath))
                {
                    try
                    {
                        File.Delete(convertedSessionPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean temporary converted Session {SessionPath}", convertedSessionPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (databaseMutationStarted)
                ClearFailedImportTracking();
            _logger.LogError(ex, "Failed to import package entry from {JsonPath}", jsonPath);
            return new ImportResult(false, null, null, null, null, FormatException(ex));
        }

        string extractDirFallback() => Path.GetTempPath();
    }

    private async Task<List<ImportResult>> ImportFromTdataDirectoriesAsync(
        IReadOnlyList<string> tdataDirectories,
        string importRoot,
        int? categoryId,
        string? twoFactorPassword,
        AccountProxyBindingInput? proxyBinding,
        IReadOnlyList<PreparedAccountImportProxy>? proxyAssignments,
        CancellationToken cancellationToken,
        string? deviceProfileKey)
    {
        var results = new List<ImportResult>();

        if (!_apiProfilePool.HasUsableApi())
        {
            results.Add(new ImportResult(
                false,
                null,
                null,
                null,
                null,
                "检测到 tdata 数据包，但系统未配置全局 Telegram API（ApiId/ApiHash）或启用的 API 配置；请先到【系统设置】配置后再导入"));
            return results;
        }

        var importedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < tdataDirectories.Count; index++)
        {
            var tdataDir = tdataDirectories[index];
            var assignment = proxyAssignments?[index];
            var effectiveBinding = BuildZipProxyBinding(proxyBinding, assignment);
            var api = await _apiProfilePool.SelectForNewAccountAsync(_accountManagement);
            var result = await ExecuteImportWithProxyAsync(
                Path.GetFileName(tdataDir),
                effectiveBinding,
                importedPhones,
                proxy => ImportFromTdataDirectoryAsync(
                    tdataDir,
                    api.ApiId,
                    api.ApiHash,
                    proxy,
                    cancellationToken,
                    deviceProfileKey),
                cancellationToken,
                result => PersistImportedSessionAsync(
                    result,
                    api.ApiId,
                    api.ApiHash,
                    categoryId,
                    twoFactorPassword,
                    deviceProfileKey));
            results.Add(AttachZipImportMetadata(
                result,
                BuildImportSourceKey(importRoot, tdataDir),
                assignment));
        }

        return results;
    }

    private async Task<ImportResult> ImportFromTdataDirectoryAsync(
        string tdataDir,
        int apiId,
        string apiHash,
        ProxyConnectionOptions? proxy,
        CancellationToken cancellationToken,
        string? deviceProfileKey)
    {
        string? tempSessionPath = null;
        try
        {
            var converted = await TdataSessionBridge.TryConvertToTelethonStringSessionAsync(tdataDir, _logger);
            if (!converted.Ok || string.IsNullOrWhiteSpace(converted.SessionString))
            {
                return new ImportResult(false, null, converted.UserId, null, null,
                    $"tdata 解析失败（{tdataDir}）：{converted.Error ?? "未知原因"}");
            }

            var phoneSeed = converted.UserId?.ToString() ?? Path.GetFileName(tdataDir);
            if (string.IsNullOrWhiteSpace(phoneSeed))
                phoneSeed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

            tempSessionPath = Path.Combine(Path.GetTempPath(), $"telegram-panel-tdata-{Guid.NewGuid():N}.session");
            var writeResult = await SessionDataConverter.TryCreateWTelegramSessionFromSessionStringAsync(
                sessionString: converted.SessionString,
                apiId: apiId,
                apiHash: apiHash,
                targetSessionPath: tempSessionPath,
                phone: phoneSeed,
                userId: converted.UserId,
                logger: _logger,
                proxy: proxy,
                cancellationToken: cancellationToken);
            if (!writeResult.Ok)
            {
                return new ImportResult(false, null, converted.UserId, null, null,
                    $"tdata 会话转换失败：{writeResult.Reason ?? "未知原因"}");
            }

            var imported = await ImportFromSessionFileDeferredAsync(
                tempSessionPath,
                apiId,
                apiHash,
                userId: converted.UserId,
                phoneHint: phoneSeed,
                sessionKey: apiHash,
                proxy: proxy,
                cancellationToken: cancellationToken,
                deviceProfileKey: deviceProfileKey);
            if (!imported.Success)
            {
                return new ImportResult(
                    false,
                    imported.Phone,
                    imported.UserId ?? converted.UserId,
                    imported.Username,
                    imported.SessionPath,
                    $"tdata 导入失败：{imported.Error ?? "未知原因"}");
            }

            return imported;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import tdata directory: {TdataDir}", tdataDir);
            return new ImportResult(false, null, null, null, null, $"tdata 导入异常：{FormatException(ex)}");
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tempSessionPath) && File.Exists(tempSessionPath))
                    File.Delete(tempSessionPath);
            }
            catch
            {
                // 忽略临时文件删除失败
            }
        }
    }

    private async Task<ImportResult> ExecuteImportWithProxyAsync(
        string stableKeySeed,
        AccountProxyBindingInput? binding,
        HashSet<string>? importedPhones,
        Func<ProxyConnectionOptions?, Task<ImportResult>> import,
        CancellationToken cancellationToken,
        Func<ImportResult, Task<ImportResult>>? persist = null)
    {
        if (binding == null)
        {
            return new ImportResult(
                false,
                null,
                null,
                null,
                null,
                "请先明确选择账号首次连接出口：已有代理、自动分配已有 WARP、已配置的全局代理或明确直连");
        }

        var requestedStrategy = (binding.Strategy ?? string.Empty).Trim().ToLowerInvariant();
        var selectedBinding = binding;
        var operationNonce = Guid.NewGuid().ToString("N");
        PreparedImportProxy prepared = default;
        ImportResult? result = null;
        int? stagedAccountId = null;
        var keepOwnedWarp = false;
        try
        {
            if (requestedStrategy == "warp_pool")
            {
                (selectedBinding, prepared) = await PrepareWarpPoolImportProxyAsync(
                    stableKeySeed,
                    operationNonce,
                    cancellationToken);
            }
            else if (requestedStrategy == "warp_per_account")
            {
                (selectedBinding, prepared) = await PrepareWarpPerAccountImportProxyAsync(
                    stableKeySeed,
                    operationNonce,
                    cancellationToken);
            }
            else
            {
                prepared = await PrepareImportProxyAsync(
                    stableKeySeed,
                    operationNonce,
                    selectedBinding,
                    cancellationToken);
            }
            result = await import(prepared.Connection);
            if (!result.Success)
            {
                result.PendingSessionReplacement?.Dispose();
                return result with { PendingSessionReplacement = null };
            }
            if (persist == null && result.PendingSessionReplacement != null)
            {
                throw new InvalidOperationException(
                    "延迟提交的 Session 必须在账号保存流程中确认或回滚");
            }

            var phone = PhoneNumberFormatter.NormalizeToDigits(result.Phone);
            if (importedPhones != null
                && !string.IsNullOrWhiteSpace(phone)
                && importedPhones.Contains(phone))
            {
                if (string.Equals(
                        selectedBinding.Strategy,
                        "direct",
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        result.PendingSessionReplacement?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogCritical(
                            ex,
                            "Failed to roll back duplicate direct-import Session for {Phone}",
                            phone);
                        return new ImportResult(
                            false,
                            phone,
                            result.UserId,
                            result.Username,
                            result.SessionPath,
                            $"检测到重复账号，但 Session 自动回滚失败：{FormatException(ex)}");
                    }

                    return new ImportResult(
                        false,
                        phone,
                        result.UserId,
                        result.Username,
                        null,
                        "重复账号已跳过");
                }

                // 每个条目都必须把本次实际验证过的出口绑定到最终账号。
                // 若首条绑定失败，后续重复账号仍可重试；Resin 也需继承最新临时 Lease。
                _logger.LogInformation(
                    "Duplicate account {Phone} will be rebound to the route used by the latest validated session",
                    phone);
            }

            if (persist != null)
            {
                result = await persist(result);
                if (!result.Success)
                    return result;
                phone = PhoneNumberFormatter.NormalizeToDigits(result.Phone);
            }

            if (importedPhones != null && !string.IsNullOrWhiteSpace(phone))
                importedPhones.Add(phone);
            if (string.IsNullOrWhiteSpace(phone))
            {
                result.PendingSessionReplacement?.Dispose();
                return result with
                {
                    Success = false,
                    PendingSessionReplacement = null,
                    Error = "账号已导入，但代理设置失败：导入结果缺少有效手机号"
                };
            }

            var account = await _accountManagement.GetAccountByPhoneAsync(phone);
            if (account == null)
            {
                return result with
                {
                    Phone = phone,
                    Error = "账号已导入，但代理设置失败：未找到已导入账号"
                };
            }

            stagedAccountId = account.Id;
            // 已有账号可能仍有按旧路由创建的客户端；在绑定前先严格释放。
            await _proxyManagement.ReleaseAccountClientStrictAsync(account.Id);
            await ValidatePreparedImportRouteAsync(
                selectedBinding,
                prepared,
                cancellationToken);

            var operation = await _proxyManagement.BindAccountsAsync(
                new[] { account.Id },
                selectedBinding,
                cancellationToken,
                expectedConnection: prepared.Connection);
            var item = operation.Items.FirstOrDefault(x => x.AccountId == account.Id);
            if (item?.Success != true)
            {
                return result with
                {
                    Phone = phone,
                    Error = $"账号已导入，但代理设置失败，账号已保持停用：{item?.Error ?? item?.Summary ?? "未知原因"}"
                };
            }

            // 一旦新创建的 WARP 绑定到账号，就不能再做失败补偿删除；否则账号会指向已清理的代理。
            keepOwnedWarp = prepared.OwnedWarpProxyId.HasValue;

            if (prepared.TemporaryResinLease != null
                && prepared.Connection != null
                && !string.IsNullOrWhiteSpace(prepared.TemporaryResinLeaseKey))
            {
                // 每次导入都必须把本次验证出口继承给稳定身份，不能沿用未经本次验证的旧 Lease。
                var inherited = await _proxyManagement.InheritImportResinLeaseBestEffortAsync(
                    prepared.Connection,
                    prepared.TemporaryResinLease.Platform,
                    prepared.TemporaryResinLeaseKey,
                    $"tg_account_{account.Id}",
                    cancellationToken);
                if (!inherited)
                {
                    // 临时身份与稳定身份之间无法确认沿用同一出口时，禁止启用账号。
                    // 否则后台任务的首次正式连接可能暴露不同 IP，破坏首连出口冻结语义。
                    await KeepImportedAccountInactiveBestEffortAsync(account.Id);
                    return result with
                    {
                        Phone = phone,
                        Error = "账号已导入，但 Resin Lease 继承失败，无法保证正式连接沿用验证出口，账号已保持停用"
                    };
                }
            }

            await _accountManagement.SetAccountActiveStatusAsync(account.Id, true);
            importedPhones?.Add(phone);
            var preparedProxy = requestedStrategy is "warp_pool" or "warp_per_account"
                ? prepared.Connection
                : null;
            return result with
            {
                Phone = phone,
                ProxyId = preparedProxy?.ProxyId ?? result.ProxyId,
                ProxyName = preparedProxy?.Name ?? result.ProxyName,
                ProxyEgressIp = preparedProxy == null
                    ? result.ProxyEgressIp
                    : prepared.EgressIp ?? result.ProxyEgressIp
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import account with selected proxy");
            var hadPendingSession = result?.PendingSessionReplacement != null;
            Exception? rollbackError = null;
            try
            {
                result?.PendingSessionReplacement?.Dispose();
            }
            catch (Exception rollbackException)
            {
                rollbackError = rollbackException;
                _logger.LogCritical(
                    rollbackException,
                    "Failed to roll back pending Session after import orchestration error");
            }

            if (result?.Success == true)
            {
                if (!hadPendingSession)
                {
                    await KeepImportedAccountInactiveBestEffortAsync(stagedAccountId);
                    return result with
                    {
                        Error = $"账号已导入，但代理设置失败，账号已保持停用：{FormatException(ex)}"
                    };
                }

                return result with
                {
                    Success = false,
                    PendingSessionReplacement = null,
                    Error = rollbackError == null
                        ? $"账号导入未完成，Session 已回滚：{FormatException(ex)}"
                        : $"账号导入失败，且 Session 自动回滚失败：{FormatException(rollbackError)}"
                };
            }

            return new ImportResult(
                false,
                result?.Phone,
                result?.UserId,
                result?.Username,
                result?.SessionPath,
                $"代理准备或账号导入失败：{FormatException(ex)}");
        }
        finally
        {
            if (prepared.TemporaryResinLease != null
                && !string.IsNullOrWhiteSpace(prepared.TemporaryResinLeaseKey))
            {
                await _proxyManagement.ReleaseImportResinLeaseBestEffortAsync(
                    prepared.TemporaryResinLease,
                    prepared.TemporaryResinLeaseKey,
                    CancellationToken.None);
            }

            prepared.WarpUsageClaim?.Dispose();
            if (!keepOwnedWarp && prepared.OwnedWarpProxyId is > 0)
                await DeleteOwnedWarpBestEffortAsync(prepared.OwnedWarpProxyId.Value);
            prepared.TemporaryWarpRequestClaim?.Dispose();
        }
    }

    private async Task<PreparedImportProxy> PrepareImportProxyAsync(
        string stableKeySeed,
        string operationNonce,
        AccountProxyBindingInput? binding,
        CancellationToken cancellationToken)
    {
        if (binding == null)
            throw new ArgumentException("请先明确选择账号首次连接出口");

        var strategy = (binding.Strategy ?? string.Empty).Trim().ToLowerInvariant();
        if (strategy == "direct")
            return default;
        if (strategy == "global")
        {
            var stableImportKey = BuildImportStableKey(stableKeySeed, operationNonce);
            var selectedGlobalId = _proxyManagement.GetEnabledGlobalProxyId();
            var selectedGlobal = selectedGlobalId is > 0
                ? await _proxyManagement.GetAsync(
                    selectedGlobalId.Value,
                    cancellationToken: cancellationToken)
                : null;
            if (selectedGlobalId is > 0 && selectedGlobal is not { IsEnabled: true })
                throw new InvalidOperationException("全局代理引用的已有代理不存在或已停用");
            if (selectedGlobal?.Kind == OutboundProxyKinds.Warp
                && _temporaryWarpClaims.OwnsRequest(selectedGlobal.WarpProfile?.RequestId))
            {
                throw new InvalidOperationException(
                    "全局 WARP 正被另一个账号首次连接流程使用，请稍后重试");
            }

            IDisposable? warpUsageClaim = null;
            try
            {
                if (selectedGlobal?.Kind == OutboundProxyKinds.Warp
                    && _warpProxyUsageGuard != null)
                {
                    warpUsageClaim = _warpProxyUsageGuard.TryAcquireUsage(selectedGlobal.Id)
                        ?? throw new InvalidOperationException(
                            "全局 WARP 正在维护或被另一个首次连接流程使用，请稍后重试；账号尚未发起首次连接");
                }

                // 已有代理直接使用同一次数据库快照构造连接，确保 Resin 控制面
                // 快照、WARP 租约与首连实际使用的代理完全一致。
                var globalProxy = selectedGlobal == null
                    ? await _proxyManagement.ResolveGlobalProxyRequiredAsync(
                        stableImportKey,
                        cancellationToken,
                        _configuration)
                    : AccountProxyResolver.BuildConnectionOptions(
                        selectedGlobal,
                        stableImportKey);
                var resinLease = selectedGlobal?.Kind == OutboundProxyKinds.Resin
                    ? new ResinLeaseControlSnapshot(
                        selectedGlobal.Id,
                        selectedGlobal.ResinAdminUrl,
                        selectedGlobal.ResinAdminToken,
                        selectedGlobal.ResinPlatform)
                    : null;
                return new PreparedImportProxy(
                    globalProxy,
                    resinLease,
                    resinLease == null ? null : stableImportKey,
                    stableImportKey,
                    warpUsageClaim);
            }
            catch
            {
                warpUsageClaim?.Dispose();
                throw;
            }
        }

        if (strategy == "existing")
        {
            if (binding.ProxyId is not > 0)
                throw new ArgumentException("请选择已有代理");

            var proxy = await _proxyManagement.GetAsync(
                binding.ProxyId.Value,
                cancellationToken: cancellationToken);
            if (proxy is not { IsEnabled: true })
                throw new KeyNotFoundException("所选代理不存在或已停用");
            if (proxy.ManagedWgcfProfile != null)
                throw new InvalidOperationException("受管 WireGuard 只能由已入库账号单独绑定，导入首次连接不能使用");
            if (proxy.Kind == OutboundProxyKinds.Warp
                && _temporaryWarpClaims.OwnsRequest(proxy.WarpProfile?.RequestId))
            {
                throw new InvalidOperationException(
                    "所选 WARP 正被另一个账号首次连接流程使用，请稍后重试");
            }

            var stableImportKey = BuildImportStableKey(stableKeySeed, operationNonce);
            var resinLease = proxy.Kind == OutboundProxyKinds.Resin
                ? new ResinLeaseControlSnapshot(
                    proxy.Id,
                    proxy.ResinAdminUrl,
                    proxy.ResinAdminToken,
                    proxy.ResinPlatform)
                : null;
            var connection = AccountProxyResolver.BuildConnectionOptions(
                proxy,
                stableImportKey);
            if (binding.ExpectedConnection != null
                && !SameConnection(binding.ExpectedConnection, connection))
            {
                throw new ProxyBindingConflictException(
                    "批量代理检测后连接参数已变化，账号尚未发起首次连接，请重新导入");
            }
            var frozenConnection = binding.ExpectedConnection ?? connection;
            IDisposable? warpUsageClaim = null;
            if (proxy.Kind == OutboundProxyKinds.Warp && _warpProxyUsageGuard != null)
            {
                warpUsageClaim = _warpProxyUsageGuard.TryAcquireUsage(proxy.Id)
                    ?? throw new InvalidOperationException(
                        "所选 WARP 正在维护或被另一个首次连接流程使用，请稍后重试；账号尚未发起首次连接");
            }
            return new PreparedImportProxy(
                frozenConnection,
                resinLease,
                resinLease != null ? stableImportKey : null,
                stableImportKey,
                warpUsageClaim);
        }

        throw new ArgumentException(
            "导入代理策略仅支持 direct、global、existing、warp_pool 或 warp_per_account");
    }

    /// <summary>
    /// 校验导入专用代理策略。自动 WARP 池只复用已经存在且运行中的容器，
    /// 不允许回退到创建新容器或面板直连。
    /// </summary>
    public async Task ValidateImportProxyBindingAsync(
        AccountProxyBindingInput binding,
        CancellationToken cancellationToken = default)
    {
        var strategy = (binding.Strategy ?? string.Empty).Trim().ToLowerInvariant();
        if (strategy == "warp_per_account")
        {
            await _proxyManagement.ValidateBindingInputAsync(
                new AccountProxyBindingInput("warp_per_account"),
                cancellationToken);
            return;
        }

        if (strategy == "warp_pool")
        {
            _ = await ListAvailableWarpPoolAsync(cancellationToken);
            return;
        }

        if (strategy is not ("direct" or "global" or "existing"))
            throw new ArgumentException("导入代理策略仅支持 direct、global、existing、warp_pool 或 warp_per_account");

        await _proxyManagement.ValidateBindingInputAsync(binding, cancellationToken);
    }

    private async Task<(AccountProxyBindingInput Binding, PreparedImportProxy Prepared)>
        PrepareWarpPoolImportProxyAsync(
            string stableKeySeed,
            string operationNonce,
            CancellationToken cancellationToken)
    {
        var candidates = await ListAvailableWarpPoolAsync(cancellationToken);
        InvalidOperationException? busyError = null;

        foreach (var proxy in candidates)
        {
            var binding = new AccountProxyBindingInput("existing", proxy.Id);
            try
            {
                var prepared = await PrepareImportProxyAsync(
                    stableKeySeed,
                    operationNonce,
                    binding,
                    cancellationToken);
                return (binding, prepared);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("首次连接流程使用", StringComparison.Ordinal)
                || ex.Message.Contains("正在维护", StringComparison.Ordinal))
            {
                busyError = ex;
            }
        }

        throw new InvalidOperationException(
            "现有 WARP 当前都在维护或被其他首次连接流程占用，请稍后重试；未创建新容器",
            busyError);
    }

    private async Task<(AccountProxyBindingInput Binding, PreparedImportProxy Prepared)>
        PrepareWarpPerAccountImportProxyAsync(
            string stableKeySeed,
            string operationNonce,
            CancellationToken cancellationToken)
    {
        var stableImportKey = BuildImportStableKey(stableKeySeed, operationNonce);
        var requestId = $"{ManagedWarpRequestPrefix}{stableImportKey}";
        IDisposable? requestClaim = null;
        IDisposable? warpUsageClaim = null;
        OutboundProxy? newProxy = null;

        try
        {
            requestClaim = _temporaryWarpClaims.ClaimRequest(requestId);
            newProxy = await _proxyManagement.CreateWarpAsync(
                BuildImportWarpDisplayName(stableKeySeed),
                requestId,
                cancellationToken);
            if (_warpProxyUsageGuard != null)
            {
                warpUsageClaim = _warpProxyUsageGuard.TryAcquireUsage(newProxy.Id)
                    ?? throw new InvalidOperationException(
                        "新创建的 WARP 正在维护或被其他首次连接流程占用，账号尚未发起首次连接");
            }

            var connection = AccountProxyResolver.BuildConnectionOptions(
                newProxy,
                stableImportKey);
            return (
                new AccountProxyBindingInput(
                    "existing",
                    newProxy.Id,
                    ExpectedConnection: connection),
                new PreparedImportProxy(
                    connection,
                    TemporaryResinLease: null,
                    TemporaryResinLeaseKey: null,
                    stableImportKey,
                    warpUsageClaim,
                    requestClaim,
                    newProxy.Id,
                    newProxy.EgressIp));
        }
        catch
        {
            warpUsageClaim?.Dispose();
            if (newProxy != null)
                await DeleteOwnedWarpBestEffortAsync(newProxy.Id);
            requestClaim?.Dispose();
            throw;
        }
    }

    private async Task<IReadOnlyList<OutboundProxy>> ListAvailableWarpPoolAsync(
        CancellationToken cancellationToken)
    {
        var proxies = await _proxyManagement.ListAsync(cancellationToken);
        var candidates = proxies
            .Where(x => x.IsEnabled
                        && x.Kind == OutboundProxyKinds.Warp
                        && x.WarpProfile is
                        {
                            DesiredEnabled: true,
                            Status: "active"
                        }
                        && !_temporaryWarpClaims.OwnsRequest(x.WarpProfile.RequestId))
            .OrderBy(x => x.Accounts.Count)
            .ThenBy(x => x.Id)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "没有可自动分配的已有 WARP；请先在代理管理中准备并启用 WARP，系统不会为导入创建新容器");
        }

        return candidates;
    }

    private Task<ImportResult> ImportFromSessionFileDeferredAsync(
        string filePath,
        int apiId,
        string apiHash,
        long? userId = null,
        string? phoneHint = null,
        string? sessionKey = null,
        ProxyConnectionOptions? proxy = null,
        CancellationToken cancellationToken = default,
        string? deviceProfileKey = null)
    {
        return _sessionImporter is IDeferredSessionImporter deferred
            ? deferred.ImportFromSessionFileDeferredAsync(
                filePath,
                apiId,
                apiHash,
                userId,
                phoneHint,
                sessionKey,
                proxy,
                cancellationToken,
                deviceProfileKey)
            : _sessionImporter.ImportFromSessionFileAsync(
                filePath,
                apiId,
                apiHash,
                userId,
                phoneHint,
                sessionKey,
                proxy,
                cancellationToken);
    }

    private Task<ImportResult> ImportFromStringSessionDeferredAsync(
        string sessionString,
        int apiId,
        string apiHash,
        ProxyConnectionOptions? proxy = null,
        CancellationToken cancellationToken = default,
        string? deviceProfileKey = null)
    {
        return _sessionImporter is IDeferredSessionImporter deferred
            ? deferred.ImportFromStringSessionDeferredAsync(
                sessionString,
                apiId,
                apiHash,
                proxy,
                cancellationToken,
                deviceProfileKey)
            : _sessionImporter.ImportFromStringSessionAsync(
                sessionString,
                apiId,
                apiHash,
                proxy,
                cancellationToken);
    }

    private async Task ValidatePreparedImportRouteAsync(
        AccountProxyBindingInput binding,
        PreparedImportProxy prepared,
        CancellationToken cancellationToken)
    {
        var strategy = (binding.Strategy ?? string.Empty).Trim().ToLowerInvariant();
        ProxyConnectionOptions? current;
        if (strategy == "global")
        {
            current = await _proxyManagement.ResolveGlobalProxyAsync(
                prepared.StableIdentityKey ?? "tg_import_validation",
                cancellationToken);
        }
        else if (strategy == "existing")
        {
            var proxyId = binding.ProxyId;
            if (proxyId is not > 0 || string.IsNullOrWhiteSpace(prepared.StableIdentityKey))
                throw new InvalidOperationException("导入期间冻结的代理快照不完整");

            var proxy = await _proxyManagement.GetAsync(
                proxyId.Value,
                cancellationToken: cancellationToken);
            if (proxy is not { IsEnabled: true })
                throw new KeyNotFoundException("导入期间所选代理已被删除或停用");

            current = AccountProxyResolver.BuildConnectionOptions(
                proxy,
                prepared.StableIdentityKey);
        }
        else
        {
            current = null;
        }

        if (!SameConnection(prepared.Connection, current))
        {
            throw new InvalidOperationException(
                "导入期间代理连接参数已变化，已阻止切换出口并保持账号停用，请重新导入");
        }
    }

    private static bool SameConnection(
        ProxyConnectionOptions? expected,
        ProxyConnectionOptions? current)
    {
        if (expected == null || current == null)
            return expected == null && current == null;

        return expected.ProxyId == current.ProxyId
               && string.Equals(expected.Kind, current.Kind, StringComparison.Ordinal)
               && string.Equals(expected.Protocol, current.Protocol, StringComparison.Ordinal)
               && string.Equals(expected.Host, current.Host, StringComparison.OrdinalIgnoreCase)
               && expected.Port == current.Port
               && string.Equals(expected.Username, current.Username, StringComparison.Ordinal)
               && string.Equals(expected.Password, current.Password, StringComparison.Ordinal)
               && string.Equals(expected.Secret, current.Secret, StringComparison.Ordinal);
    }

    private static string BuildImportStableKey(string seed, string operationNonce)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"{seed ?? string.Empty}\n{operationNonce}");
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return $"tg_import_{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static bool IsWarpPerAccountStrategy(AccountProxyBindingInput? binding) =>
        string.Equals(
            (binding?.Strategy ?? string.Empty).Trim(),
            "warp_per_account",
            StringComparison.OrdinalIgnoreCase);

    private static string BuildImportWarpDisplayName(string stableKeySeed)
    {
        var label = Path.GetFileNameWithoutExtension(stableKeySeed);
        if (string.IsNullOrWhiteSpace(label))
            label = "账号";
        label = label.Trim();
        if (label.Length > 48)
            label = label[..48];
        return $"WARP · 导入 {label}";
    }

    private readonly record struct PreparedImportProxy(
        ProxyConnectionOptions? Connection,
        ResinLeaseControlSnapshot? TemporaryResinLease,
        string? TemporaryResinLeaseKey,
        string? StableIdentityKey,
        IDisposable? WarpUsageClaim,
        IDisposable? TemporaryWarpRequestClaim = null,
        int? OwnedWarpProxyId = null,
        string? EgressIp = null);

    private async Task DeleteOwnedWarpBestEffortAsync(int proxyId)
    {
        try
        {
            await _proxyManagement.DeleteAsync(proxyId, CancellationToken.None);
        }
        catch (KeyNotFoundException)
        {
            // 已被其他清理流程删除。
        }
        catch (ProxyInUseException ex)
        {
            _logger.LogWarning(
                ex,
                "Import-created WARP proxy {ProxyId} is already bound and will be retained",
                proxyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to clean import-created WARP proxy {ProxyId}",
                proxyId);
        }
    }

    private async Task KeepImportedAccountInactiveBestEffortAsync(int? accountId)
    {
        if (accountId is not > 0)
            return;

        try
        {
            await _accountManagement.SetAccountActiveStatusAsync(accountId.Value, false);
            // 即使账号已经停用，也必须确认旧客户端真正断开；普通释放会吞掉
            // DisposeAsync 失败并丢失引用，无法证明旧出口已经停止。
            await _proxyManagement.ReleaseAccountClientStrictAsync(accountId.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to keep partially imported account {AccountId} inactive", accountId);
        }
    }

    private async Task<ImportResult> PersistImportedSessionAsync(
        ImportResult result,
        int apiId,
        string apiHash,
        int? categoryId,
        string? twoFactorPassword,
        string? deviceProfileKey)
    {
        if (!result.Success)
            return result;
        if (!result.UserId.HasValue)
        {
            try
            {
                result.PendingSessionReplacement?.Dispose();
                return new ImportResult(
                    false,
                    result.Phone,
                    null,
                    result.Username,
                    result.SessionPath,
                    "Session 导入未完成：导入结果缺少用户 ID，文件已回滚");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to roll back imported Session without user ID");
                return new ImportResult(
                    false,
                    result.Phone,
                    null,
                    result.Username,
                    result.SessionPath,
                    $"Session 导入结果缺少用户 ID，且文件自动回滚失败：{FormatException(ex)}");
            }
        }

        var pendingSession = result.PendingSessionReplacement;
        try
        {
            var phone = PhoneNumberFormatter.NormalizeToDigits(result.Phone);
            if (string.IsNullOrWhiteSpace(phone))
                throw new InvalidOperationException("导入结果缺少有效手机号");

            var existing = await _accountManagement.GetAccountByPhoneAsync(phone);
            if (existing != null)
            {
                existing.UserId = result.UserId.Value;
                existing.Username = result.Username;
                existing.SessionPath = result.SessionPath!;
                existing.ApiId = apiId;
                existing.ApiHash = apiHash.Trim();
                if (!string.IsNullOrWhiteSpace(deviceProfileKey))
                    existing.DeviceProfileKey = deviceProfileKey.Trim();
                // 路由绑定完成前禁止后台任务选择该账号。
                existing.IsActive = false;
                existing.CategoryId = categoryId ?? existing.CategoryId;
                existing.LastSyncAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(twoFactorPassword))
                    existing.TwoFactorPassword = twoFactorPassword.Trim();
                await _accountManagement.UpdateAccountAsync(existing);
            }
            else
            {
                var account = new Account
                {
                    Phone = phone,
                    UserId = result.UserId.Value,
                    Username = result.Username,
                    SessionPath = result.SessionPath!,
                    ApiId = apiId,
                    ApiHash = apiHash.Trim(),
                    DeviceProfileKey = string.IsNullOrWhiteSpace(deviceProfileKey) ? null : deviceProfileKey.Trim(),
                    IsActive = false,
                    CategoryId = categoryId,
                    TwoFactorPassword = string.IsNullOrWhiteSpace(twoFactorPassword) ? null : twoFactorPassword.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    LastSyncAt = DateTime.UtcNow
                };

                await _accountManagement.CreateAccountAsync(account);
            }

            pendingSession?.Commit();
            if (pendingSession?.CleanupError != null)
            {
                _logger.LogWarning(
                    pendingSession.CleanupError,
                    "Imported Session committed but rollback backup cleanup is pending: {BackupPath}",
                    pendingSession.BackupPath);
            }
            pendingSession?.Dispose();
            _logger.LogInformation("Account saved to database: {Phone}", phone);
            return result with
            {
                Phone = phone,
                PendingSessionReplacement = null
            };
        }
        catch (Exception ex)
        {
            ClearFailedImportTracking();
            Exception? rollbackError = null;
            try
            {
                pendingSession?.Dispose();
            }
            catch (Exception rollbackException)
            {
                rollbackError = rollbackException;
                _logger.LogCritical(
                    rollbackException,
                    "Failed to roll back imported Session after database save failure: {Phone}",
                    result.Phone);
            }

            _logger.LogError(ex, "Failed to save account to database: {Phone}", result.Phone);
            var error = $"Session 导入未完成，数据库保存失败且文件已回滚：{FormatException(ex)}";
            if (rollbackError != null)
                error = $"Session 导入未完成，数据库保存失败，且文件自动回滚失败：{FormatException(rollbackError)}";
            return new ImportResult(
                false,
                result.Phone,
                result.UserId,
                result.Username,
                result.SessionPath,
                error);
        }
    }

    private void ClearFailedImportTracking()
    {
        try
        {
            _db.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear tracked entities after account import failure");
        }
    }

    private bool TryGetGlobalTelegramApi(out int apiId, out string apiHash)
    {
        if (!TelegramApiProfilePool.TrySelectDefault(_configuration, out var credentials, out _))
        {
            apiId = 0;
            apiHash = string.Empty;
            return false;
        }

        apiId = credentials.ApiId;
        apiHash = credentials.ApiHash;
        return true;
    }

    private static bool IsPathInsideAnyDirectory(string filePath, IReadOnlyCollection<string> directories)
    {
        if (directories.Count == 0)
            return false;

        var fullFilePath = Path.GetFullPath(filePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var directory in directories)
        {
            var fullDirectoryPath = Path.GetFullPath(directory);
            var fullDirectoryPathWithSep = fullDirectoryPath.EndsWith(Path.DirectorySeparatorChar)
                ? fullDirectoryPath
                : fullDirectoryPath + Path.DirectorySeparatorChar;

            if (fullFilePath.StartsWith(fullDirectoryPathWithSep, comparison))
                return true;
        }

        return false;
    }

    private static List<string> OrderImportPaths(
        string importRoot,
        IEnumerable<string> paths) =>
        paths
            .Distinct(StringComparer.Ordinal)
            .Select(path => new
            {
                Path = path,
                Key = BuildImportSourceKey(importRoot, path)
            })
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Path)
            .ToList();

    private static string BuildImportSourceKey(string importRoot, string path)
    {
        var key = Path.GetRelativePath(importRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (key == ".")
        {
            key = Path.GetFileName(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        return string.IsNullOrWhiteSpace(key) ? "account" : key;
    }

    private static AccountProxyBindingInput? BuildZipProxyBinding(
        AccountProxyBindingInput? fallback,
        PreparedAccountImportProxy? assignment) =>
        assignment == null
            ? fallback
            : new AccountProxyBindingInput(
                "existing",
                assignment.ProxyId,
                ExpectedConnection: assignment.ExpectedConnection);

    private static ImportResult AttachZipImportMetadata(
        ImportResult result,
        string sourceKey,
        PreparedAccountImportProxy? assignment) =>
        result with
        {
            SourceKey = sourceKey,
            ProxyLine = assignment?.SourceLine,
            ProxyId = assignment?.ProxyId,
            ProxyName = assignment?.ProxyName,
            ProxyEgressIp = assignment?.EgressIp
        };

    private static List<string> FindTdataDirectories(string rootDirectory)
    {
        var result = new List<string>();
        if (LooksLikeTdataDirectory(rootDirectory))
            result.Add(rootDirectory);

        foreach (var dir in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories))
        {
            if (LooksLikeTdataDirectory(dir))
                result.Add(dir);
        }

        return result
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool LooksLikeTdataDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            var hasKeyFile = File.Exists(Path.Combine(directory, "key_datas"))
                             || File.Exists(Path.Combine(directory, "key_data"));
            var hasTdataData = Directory.EnumerateFileSystemEntries(directory, "D877F783D5D3EF8C*", SearchOption.TopDirectoryOnly).Any();
            return hasKeyFile || hasTdataData;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试从目录中读取 2fa.txt 文件内容作为二级密码
    /// </summary>
    private static async Task<string?> TryRead2faFileAsync(string directory)
    {
        try
        {
            // 支持多种常见文件名
            var possibleNames = new[] { "2fa.txt", "2FA.txt", "2fa", "2FA", "twofa.txt", "password.txt" };
            foreach (var name in possibleNames)
            {
                var filePath = Path.Combine(directory, name);
                if (!File.Exists(filePath))
                    continue;

                var content = await File.ReadAllTextAsync(filePath);
                var password = content?.Trim();
                if (!string.IsNullOrWhiteSpace(password))
                    return password;
            }
        }
        catch
        {
            // 读取失败不影响导入流程
        }

        return null;
    }

    private static string? BuildNickname(string? firstName, string? lastName, string? username)
    {
        firstName = string.IsNullOrWhiteSpace(firstName) ? null : firstName.Trim();
        lastName = string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim();
        var display = string.Join(" ", new[] { firstName, lastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(display))
            return display;
        return string.IsNullOrWhiteSpace(username) ? null : username.Trim();
    }

    private static bool TryInferPhone(System.Text.Json.JsonElement root, string jsonPath, out string? phone)
    {
        // 兼容一些导出：json 里 phone 可能为 null，但文件名/ session_file 会带 +手机号
        if (!TryGetString(root, out phone, "session_file", "sessionFile") || string.IsNullOrWhiteSpace(phone))
            phone = Path.GetFileNameWithoutExtension(jsonPath);

        phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        return !string.IsNullOrWhiteSpace(phone);
    }

    private static bool LooksLikeSqliteSession(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[16];
            var read = fs.Read(header);
            if (read < 15) return false;
            var text = System.Text.Encoding.ASCII.GetString(header[..15]);
            return string.Equals(text, "SQLite format 3", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // session_string 转换逻辑已收敛到 SessionDataConverter

    private static bool TryGetString(System.Text.Json.JsonElement root, out string? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                value = prop.GetString();
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetInt(System.Text.Json.JsonElement root, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetInt32(out value))
                    return true;

                if (prop.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(prop.GetString(), out value))
                    return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetLong(System.Text.Json.JsonElement root, out long? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == System.Text.Json.JsonValueKind.Number && prop.TryGetInt64(out var l))
                {
                    value = l;
                    return true;
                }

                if (prop.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(prop.GetString(), out var ls))
                {
                    value = ls;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    private static string FormatException(Exception ex)
    {
        // 把 inner exception 展开，避免 UI 只显示 “See the inner exception for details.”
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            var msg = (current.Message ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(msg))
                messages.Add(msg);

            if (messages.Count >= 5)
                break;
        }

        return messages.Count == 0 ? "未知错误" : string.Join(" | ", messages.Distinct());
    }
}

public sealed record AccountImportFile(string FileName, Stream Content);
