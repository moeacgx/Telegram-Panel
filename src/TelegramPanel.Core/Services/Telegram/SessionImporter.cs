using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Session导入服务实现
/// </summary>
public class SessionImporter : ISessionImporter, IDeferredSessionImporter
{
    private readonly ILogger<SessionImporter> _logger;
    private readonly IConfiguration _configuration;

    public SessionImporter(IConfiguration configuration, ILogger<SessionImporter> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ImportResult> ImportFromSessionFileAsync(
        string filePath,
        int apiId,
        string apiHash,
        long? userId = null,
        string? phoneHint = null,
        string? sessionKey = null,
        ProxyConnectionOptions? proxy = null,
        CancellationToken cancellationToken = default) =>
        await ImportFromSessionFileCoreAsync(
            filePath,
            apiId,
            apiHash,
            userId,
            phoneHint,
            sessionKey,
            proxy,
            deferCommit: false,
            cancellationToken);

    async Task<ImportResult> IDeferredSessionImporter.ImportFromSessionFileDeferredAsync(
        string filePath,
        int apiId,
        string apiHash,
        long? userId,
        string? phoneHint,
        string? sessionKey,
        ProxyConnectionOptions? proxy,
        CancellationToken cancellationToken) =>
        await ImportFromSessionFileCoreAsync(
            filePath,
            apiId,
            apiHash,
            userId,
            phoneHint,
            sessionKey,
            proxy,
            deferCommit: true,
            cancellationToken);

    private async Task<ImportResult> ImportFromSessionFileCoreAsync(
        string filePath,
        int apiId,
        string apiHash,
        long? userId,
        string? phoneHint,
        string? sessionKey,
        ProxyConnectionOptions? proxy,
        bool deferCommit,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new ImportResult(false, null, null, null, null, $"Session file not found: {filePath}");
        }

        AtomicSessionFileReplacement? replacement = null;
        var replacementTransferred = false;
        try
        {
            _logger.LogInformation("Importing session from file: {FilePath}", filePath);

            // 一些来源的 .session 实际上是 SQLite 格式（常见于 Telegram Desktop 导出）
            // WTelegramClient 无法直接使用这类 session，用户应使用带 json 的压缩包导入或重新登录生成新 session。
            if (LooksLikeSqliteSession(filePath))
            {
                return new ImportResult(
                    false,
                    phoneHint,
                    userId,
                    null,
                    null,
                    "该 .session 为 SQLite 格式（通常来自 Telegram Desktop），本项目不支持直接导入单个 .session；请使用包含 .json + .session 的压缩包导入，或重新登录生成新的 session。");
            }

            // 复制到sessions目录
            var fileName = Path.GetFileName(filePath);
            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            Directory.CreateDirectory(sessionsPath);
            var targetPath = Path.Combine(sessionsPath, fileName);

            replacement = AtomicSessionFileReplacement.Create(targetPath);
            File.Copy(filePath, replacement.StagingPath, overwrite: false);
            var deviceProfile = TelegramClientDeviceProfile.ForStableKey($"{apiId}:{targetPath}");

            // 使用 config 回调设置 session 路径
            string Config(string what) => what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => replacement.StagingPath,
                "session_key" => string.IsNullOrWhiteSpace(sessionKey) ? null! : sessionKey,
                "app_id" => apiId.ToString(),
                "app_hash" => apiHash,
                "app_version" or "device_model" or "system_version" or "system_lang_code" or "lang_code" => deviceProfile.GetConfigValue(what)!,

                _ => null!
            };

            User? self;
            using (var client = new Client(Config))
            {
                TelegramImportProxyConfigurator.Apply(client, proxy, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await client.ConnectAsync();

                self = client.User;
                if (self == null)
                {
                    try
                    {
                        var users = await client.Users_GetUsers(InputUser.Self);
                        self = users.OfType<User>().FirstOrDefault();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch self user after session connect: {SessionPath}", targetPath);
                    }
                }
            }

            if (self != null)
            {
                _logger.LogInformation("Session imported successfully for user {UserId}", self.id);
                replacement.Apply();
                var imported = new ImportResult(
                    Success: true,
                    Phone: self.phone,
                    UserId: self.id,
                    Username: self.MainUsername,
                    SessionPath: targetPath
                );
                if (!deferCommit)
                {
                    replacement.Commit();
                    if (replacement.CleanupError != null)
                    {
                        _logger.LogWarning(
                            replacement.CleanupError,
                            "Session imported but rollback backup cleanup is pending: {BackupPath}",
                            replacement.BackupPath);
                    }
                    return imported;
                }

                replacementTransferred = true;
                return imported with
                {
                    PendingSessionReplacement = replacement
                };
            }

            return new ImportResult(false, null, null, null, null, "Session exists but user not logged in");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import session from {FilePath}", filePath);
            return new ImportResult(false, null, null, null, null, ex.Message);
        }
        finally
        {
            if (!replacementTransferred)
                replacement?.Dispose();
        }
    }

    public async Task<List<ImportResult>> BatchImportSessionFilesAsync(
        string[] filePaths,
        int apiId,
        string apiHash,
        ProxyConnectionOptions? proxy = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ImportResult>();

        foreach (var filePath in filePaths)
        {
            var result = await ImportFromSessionFileAsync(
                filePath,
                apiId,
                apiHash,
                proxy: proxy,
                cancellationToken: cancellationToken);
            results.Add(result);

            // 短暂延迟避免频繁连接
            await Task.Delay(500, cancellationToken);
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Batch import completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    public async Task<ImportResult> ImportFromStringSessionAsync(
        string sessionString,
        int apiId,
        string apiHash,
        ProxyConnectionOptions? proxy = null,
        CancellationToken cancellationToken = default) =>
        await ImportFromStringSessionCoreAsync(
            sessionString,
            apiId,
            apiHash,
            proxy,
            deferCommit: false,
            cancellationToken);

    async Task<ImportResult> IDeferredSessionImporter.ImportFromStringSessionDeferredAsync(
        string sessionString,
        int apiId,
        string apiHash,
        ProxyConnectionOptions? proxy,
        CancellationToken cancellationToken) =>
        await ImportFromStringSessionCoreAsync(
            sessionString,
            apiId,
            apiHash,
            proxy,
            deferCommit: true,
            cancellationToken);

    private async Task<ImportResult> ImportFromStringSessionCoreAsync(
        string sessionString,
        int apiId,
        string apiHash,
        ProxyConnectionOptions? proxy,
        bool deferCommit,
        CancellationToken cancellationToken)
    {
        try
        {
            // WTelegramClient 使用二进制session文件，不直接支持StringSession
            // 需要将base64字符串解码并保存为文件

            var sessionData = Convert.FromBase64String(sessionString);
            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            Directory.CreateDirectory(sessionsPath);
            var sessionPath = Path.Combine(sessionsPath, $"{Guid.NewGuid():N}.session");
            using var temporarySession = AtomicSessionFileReplacement.Create(sessionPath);
            await File.WriteAllBytesAsync(temporarySession.StagingPath, sessionData, cancellationToken);
            var deviceProfile = TelegramClientDeviceProfile.ForStableKey($"{apiId}:{sessionString}");

            // 使用 config 回调设置 session 路径
            string Config(string what) => what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => temporarySession.StagingPath,
                "app_id" => apiId.ToString(),
                "app_hash" => apiHash,
                "app_version" or "device_model" or "system_version" or "system_lang_code" or "lang_code" => deviceProfile.GetConfigValue(what)!,

                _ => null!
            };

            User? self;
            using (var client = new Client(Config))
            {
                TelegramImportProxyConfigurator.Apply(client, proxy, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await client.ConnectAsync();

                self = client.User;
                if (self == null)
                {
                    try
                    {
                        var users = await client.Users_GetUsers(InputUser.Self);
                        self = users.OfType<User>().FirstOrDefault();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch self user after string session connect: {SessionPath}", sessionPath);
                    }
                }
            }

            if (self != null)
            {
                // 使用原子替换，避免覆盖手机号 Session 时先破坏旧文件。
                var newPath = Path.Combine(sessionsPath, $"{self.phone}.session");
                var finalSession = AtomicSessionFileReplacement.Create(newPath);
                try
                {
                    File.Copy(temporarySession.StagingPath, finalSession.StagingPath, overwrite: false);
                    temporarySession.Dispose();
                    finalSession.Apply();
                    var imported = new ImportResult(
                        Success: true,
                        Phone: self.phone,
                        UserId: self.id,
                        Username: self.MainUsername,
                        SessionPath: newPath
                    );
                    if (!deferCommit)
                    {
                        finalSession.Commit();
                        if (finalSession.CleanupError != null)
                        {
                            _logger.LogWarning(
                                finalSession.CleanupError,
                                "StringSession imported but rollback backup cleanup is pending: {BackupPath}",
                                finalSession.BackupPath);
                        }
                        finalSession.Dispose();
                        return imported;
                    }

                    return imported with
                    {
                        PendingSessionReplacement = finalSession
                    };
                }
                catch
                {
                    finalSession.Dispose();
                    throw;
                }
            }

            return new ImportResult(false, null, null, null, null, "Invalid session string");
        }
        catch (FormatException)
        {
            return new ImportResult(false, null, null, null, null, "Invalid base64 format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from string session");
            return new ImportResult(false, null, null, null, null, ex.Message);
        }
    }

    public Task<bool> ValidateSessionAsync(string sessionPath)
    {
        if (!File.Exists(sessionPath))
        {
            return Task.FromResult(false);
        }

        try
        {
            // 简单检查文件大小（有效session通常大于0字节）
            var fileInfo = new FileInfo(sessionPath);
            return Task.FromResult(fileInfo.Length > 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
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
}

/// <summary>
/// 为导入阶段的短生命周期客户端应用与账号客户端池一致的代理连接方式。
/// </summary>
internal static class TelegramImportProxyConfigurator
{
    public static void Apply(
        Client client,
        ProxyConnectionOptions? proxy,
        CancellationToken cancellationToken = default) =>
        TelegramClientProxyConfigurator.Apply(client, proxy, cancellationToken);
}

/// <summary>
/// 为所有 WTelegram 客户端统一应用数据库代理快照。
/// </summary>
internal static class TelegramClientProxyConfigurator
{
    public static void Apply(
        Client client,
        ProxyConnectionOptions? proxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (proxy is { Protocol: OutboundProxyProtocols.Http or OutboundProxyProtocols.Socks5 })
        {
            client.TcpHandler = (address, port) =>
                ProxyTcpConnector.ConnectAsync(address, port, proxy, cancellationToken);
            return;
        }

        if (proxy is not { Protocol: OutboundProxyProtocols.MtProto })
            return;
        if (string.IsNullOrWhiteSpace(proxy.Secret))
            throw new InvalidOperationException($"MTProxy {proxy.ProxyId} 缺少 Secret");

        client.MTProxyUrl = $"https://t.me/proxy?server={Uri.EscapeDataString(proxy.Host)}"
                            + $"&port={proxy.Port}"
                            + $"&secret={Uri.EscapeDataString(proxy.Secret)}";
    }
}
