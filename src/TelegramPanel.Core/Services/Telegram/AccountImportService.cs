using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using System.IO.Compression;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 账号导入协调服务 - 整合Session导入和数据库保存
/// </summary>
public class AccountImportService
{
    private readonly ISessionImporter _sessionImporter;
    private readonly AccountManagementService _accountManagement;
    private readonly ILogger<AccountImportService> _logger;
    private readonly IConfiguration _configuration;

    public AccountImportService(
        ISessionImporter sessionImporter,
        AccountManagementService accountManagement,
        ILogger<AccountImportService> logger,
        IConfiguration configuration)
    {
        _sessionImporter = sessionImporter;
        _accountManagement = accountManagement;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// 从浏览器上传的文件导入账号
    /// </summary>
    public async Task<List<ImportResult>> ImportFromBrowserFilesAsync(
        IReadOnlyList<IBrowserFile> files,
        int apiId,
        string apiHash,
        int? categoryId = null)
    {
        var results = new List<ImportResult>();

        foreach (var file in files)
        {
            try
            {
                // 保存文件到临时目录
                var tempPath = Path.Combine(Path.GetTempPath(), file.Name);
                await using var fileStream = new FileStream(tempPath, FileMode.Create);
                await file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024) // 10MB limit
                    .CopyToAsync(fileStream);

                fileStream.Close();

                // 导入Session
                var result = await _sessionImporter.ImportFromSessionFileAsync(tempPath, apiId, apiHash);

                // 如果导入成功，保存到数据库
                if (result.Success && result.UserId.HasValue)
                {
                    var account = new Account
                    {
                        Phone = result.Phone!,
                        UserId = result.UserId.Value,
                        Username = result.Username,
                        SessionPath = result.SessionPath!,
                        ApiId = apiId,
                        ApiHash = apiHash,
                        IsActive = true,
                        CategoryId = categoryId,
                        CreatedAt = DateTime.UtcNow,
                        LastSyncAt = DateTime.UtcNow
                    };

                    try
                    {
                        await _accountManagement.CreateAccountAsync(account);
                        _logger.LogInformation("Account saved to database: {Phone}", account.Phone);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save account to database: {Phone}", account.Phone);
                        result = new ImportResult(
                            false,
                            result.Phone,
                            result.UserId,
                            result.Username,
                            result.SessionPath,
                            $"Session imported but database save failed: {ex.Message}"
                        );
                    }
                }

                results.Add(result);

                // 清理临时文件
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 忽略临时文件删除失败
                }

                // 延迟避免频繁连接
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process file: {FileName}", file.Name);
                results.Add(new ImportResult(false, null, null, null, null, $"File processing failed: {ex.Message}"));
            }
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Browser file import completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    /// <summary>
    /// 从StringSession导入账号
    /// </summary>
    public async Task<ImportResult> ImportFromStringSessionAsync(
        string sessionString,
        int apiId,
        string apiHash,
        int? categoryId = null)
    {
        var result = await _sessionImporter.ImportFromStringSessionAsync(sessionString, apiId, apiHash);

        // 如果导入成功，保存到数据库
        if (result.Success && result.UserId.HasValue)
        {
            var account = new Account
            {
                Phone = result.Phone!,
                UserId = result.UserId.Value,
                Username = result.Username,
                SessionPath = result.SessionPath!,
                ApiId = apiId,
                ApiHash = apiHash,
                IsActive = true,
                CategoryId = categoryId,
                CreatedAt = DateTime.UtcNow,
                LastSyncAt = DateTime.UtcNow
            };

            try
            {
                await _accountManagement.CreateAccountAsync(account);
                _logger.LogInformation("Account saved to database: {Phone}", account.Phone);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save account to database: {Phone}", account.Phone);
                return new ImportResult(
                    false,
                    result.Phone,
                    result.UserId,
                    result.Username,
                    result.SessionPath,
                    $"Session imported but database save failed: {ex.Message}"
                );
            }
        }

        return result;
    }

    /// <summary>
    /// 从浏览器上传的 zip 压缩包导入账号（每个账号目录下包含一个 json + 一个 session）
    /// </summary>
    public async Task<List<ImportResult>> ImportFromZipAsync(
        IBrowserFile zipFile,
        int? categoryId = null)
    {
        var results = new List<ImportResult>();

        if (zipFile == null)
        {
            results.Add(new ImportResult(false, null, null, null, null, "未选择压缩包文件"));
            return results;
        }

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"telegram-panel-import-{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), $"telegram-panel-import-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(extractDir);

            await using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var upload = zipFile.OpenReadStream(maxAllowedSize: 200 * 1024 * 1024))
            {
                await upload.CopyToAsync(fs);
            }

            // 注意：部分第三方打包工具会生成“目录条目但包含数据”的非标准 zip，
            // ZipFile.ExtractToDirectory 会直接抛异常。这里改为手动解压并容错处理。
            await ExtractZipToDirectorySafeAsync(tempZipPath, extractDir);

            var jsonFiles = Directory.EnumerateFiles(extractDir, "*.json", SearchOption.AllDirectories).ToList();
            if (jsonFiles.Count == 0)
            {
                results.Add(new ImportResult(false, null, null, null, null, "压缩包内未找到任何 .json 文件"));
                return results;
            }

            var importedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var jsonPath in jsonFiles)
            {
                var result = await ImportFromPackageEntryAsync(jsonPath, categoryId);
                if (result.Phone != null && !importedPhones.Add(result.Phone))
                {
                    results.Add(new ImportResult(false, result.Phone, result.UserId, result.Username, result.SessionPath, "重复账号已跳过"));
                    continue;
                }

                results.Add(result);
            }

            var successCount = results.Count(r => r.Success);
            _logger.LogInformation("Zip import completed: {Success}/{Total} successful", successCount, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zip import failed");
            results.Add(new ImportResult(false, null, null, null, null, $"压缩包导入失败: {ex.Message}"));
            return results;
        }
        finally
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
        }
    }

    private async Task ExtractZipToDirectorySafeAsync(string zipPath, string destinationDirectory)
    {
        var destRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destRoot);
        var destRootWithSep = destRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destRoot
            : destRoot + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
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

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            try
            {
                await using var entryStream = entry.Open();
                await using var outStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await entryStream.CopyToAsync(outStream);
            }
            catch (Exception ex)
            {
                // 容错：单个条目失败不影响整体导入
                _logger.LogWarning(ex, "Failed to extract zip entry: {Entry}", entry.FullName);
            }
        }
    }

    private async Task<ImportResult> ImportFromPackageEntryAsync(string jsonPath, int? categoryId)
    {
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!TryGetInt(root, out var apiId, "api_id", "app_id"))
                return new ImportResult(false, null, null, null, null, $"json 缺少 api_id: {jsonPath}");

            if (!TryGetString(root, out var apiHash, "api_hash", "app_hash") || string.IsNullOrWhiteSpace(apiHash))
                return new ImportResult(false, null, null, null, null, $"json 缺少 api_hash: {jsonPath}");

            if (!TryGetString(root, out var phone, "phone") || string.IsNullOrWhiteSpace(phone))
                return new ImportResult(false, null, null, null, null, $"json 缺少 phone: {jsonPath}");

            phone = phone.Trim();

            _ = TryGetLong(root, out var userId, "user_id", "uid");
            _ = TryGetString(root, out var username, "username");
            username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
            _ = TryGetString(root, out var firstName, "first_name", "firstName");
            _ = TryGetString(root, out var lastName, "last_name", "lastName");
            var nickname = BuildNickname(firstName, lastName, username);

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

            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            Directory.CreateDirectory(sessionsPath);
            var targetSessionPath = Path.Combine(sessionsPath, $"{phone}.session");
            File.Copy(sessionCandidate, targetSessionPath, overwrite: true);

            // 入库：存在则更新，不存在则创建
            var existing = await _accountManagement.GetAccountByPhoneAsync(phone);
            if (existing != null)
            {
                existing.UserId = userId ?? existing.UserId;
                existing.Username = username ?? existing.Username;
                existing.Nickname = nickname ?? existing.Nickname;
                existing.SessionPath = targetSessionPath;
                existing.ApiId = apiId;
                existing.ApiHash = apiHash.Trim();
                existing.IsActive = true;
                existing.LastSyncAt = DateTime.UtcNow;
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
                    IsActive = true,
                    CategoryId = categoryId,
                    CreatedAt = DateTime.UtcNow,
                    LastSyncAt = DateTime.UtcNow
                };

                await _accountManagement.CreateAccountAsync(account);
            }

            return new ImportResult(true, phone, userId, username, targetSessionPath, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import package entry from {JsonPath}", jsonPath);
            return new ImportResult(false, null, null, null, null, ex.Message);
        }

        string extractDirFallback() => Path.GetTempPath();
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
}
