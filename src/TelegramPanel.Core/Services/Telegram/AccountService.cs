using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Utils;
using WTelegram;
using AccountStatus = TelegramPanel.Core.Interfaces.AccountStatus;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 账号服务实现
/// </summary>
public class AccountService : IAccountService
{
    private readonly ITelegramClientPool _clientPool;
    private readonly ILogger<AccountService> _logger;
    private readonly IConfiguration _configuration;

    // 临时存储登录状态（实际项目应该使用数据库或缓存）
    private readonly Dictionary<int, string> _pendingLogins = new();
    private static readonly ConcurrentDictionary<int, QrLoginSession> QrLoginSessions = new();
    private static readonly ConcurrentDictionary<int, TelegramApiCredentials> PendingLoginApis = new();

    public AccountService(ITelegramClientPool clientPool, ILogger<AccountService> logger, IConfiguration configuration)
    {
        _clientPool = clientPool;
        _logger = logger;
        _configuration = configuration;
    }

    public Task<LoginResult> StartLoginAsync(
        int accountId,
        string phone,
        AccountProxyResolution proxyResolution,
        TelegramApiCredentials apiCredentials)
    {
        ArgumentNullException.ThrowIfNull(proxyResolution);
        return StartLoginCoreAsync(accountId, phone, proxyResolution, apiCredentials);
    }

    private async Task<LoginResult> StartLoginCoreAsync(
        int accountId,
        string phone,
        AccountProxyResolution proxyOverride,
        TelegramApiCredentials apiCredentials)
    {
        if (!TelegramApiProfilePool.TryNormalizeCredentials(apiCredentials.ApiId, apiCredentials.ApiHash, out var apiHash, out var apiHashReason))
        {
            return new LoginResult(false, null, $"Telegram API 配置无效：{apiHashReason}");
        }

        var apiId = apiCredentials.ApiId;
        var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
        Directory.CreateDirectory(sessionsPath);
        var normalizedPhone = NormalizePhoneForLogin(phone);
        var sessionPath = Path.Combine(sessionsPath, $"{normalizedPhone}.session");

        // 若已有旧的 SQLite 格式 session（Telethon/Pyrogram/Telegram Desktop 常见），会导致 WTelegramClient 直接读取失败，
        // 这里先自动备份，确保“手机号登录”能顺利重新生成 WTelegram 的 session。
        TryBackupSqliteSessionIfExists(sessionPath);

        _logger.LogInformation("Starting login for phone {Phone}", normalizedPhone);

        WTelegram.Client? client = null;

        try
        {
            try
            {
                client = await CreateLoginClientAsync();
            }
            catch (Exception ex) when (LooksLikeSessionApiMismatchOrCorrupted(ex))
            {
                // session 在创建 client 时也可能因为旧 ApiHash/密钥无法解密，这里同样自动备份后重建。
                TryBackupCorruptedSessionIfExists(sessionPath);
                await _clientPool.RemoveClientStrictAsync(accountId);

                client = await CreateLoginClientAsync();
            }

            string result;
            try
            {
                result = await client.Login(normalizedPhone);
            }
            catch (Exception ex) when (LooksLikeSessionApiMismatchOrCorrupted(ex))
            {
                // session 与 ApiId/ApiHash 不匹配或损坏，备份后重新开始登录流程
                TryBackupCorruptedSessionIfExists(sessionPath);
                await _clientPool.RemoveClientStrictAsync(accountId);

                client = await CreateLoginClientAsync();

                result = await client.Login(normalizedPhone);
            }

            _logger.LogInformation("Login flow next step for {Phone}: {Step}", normalizedPhone, result);

            var loginResult = result switch
            {
                "verification_code" => new LoginResult(false, "code", "请输入验证码"),
                "password" => new LoginResult(false, "password", "请输入两步验证密码"),
                "name" => new LoginResult(false, "signup", "需要注册新账号"),
                "email" => new LoginResult(false, "email", "该账号需要邮箱验证（请按提示填写邮箱并完成验证）"),
                "email_verification_code" => new LoginResult(false, "email_code", "请输入邮箱验证码"),
                _ when client.User != null => new LoginResult(true, null, "登录成功", MapToAccountInfo(accountId, client, apiId, apiHash)),
                _ => new LoginResult(false, null, $"未知状态: {result}")
            };

            if (!loginResult.Success && string.IsNullOrWhiteSpace(loginResult.NextStep))
            {
                try { await _clientPool.RemoveClientStrictAsync(accountId); } catch { }
            }

            if (loginResult.NextStep is "code" or "password")
                PendingLoginApis[accountId] = new TelegramApiCredentials(apiId, apiHash, apiCredentials.ProfileName);
            else
                PendingLoginApis.TryRemove(accountId, out _);
            return loginResult;
        }
        catch (Exception ex)
        {
            try
            {
                await _clientPool.RemoveClientStrictAsync(accountId);
                PendingLoginApis.TryRemove(accountId, out _);
            }
            catch
            {
            }

            var hint = BuildFriendlyStartLoginError(ex);
            _logger.LogWarning(ex, "StartLogin failed for phone {Phone} (accountId={AccountId}): {Hint}", normalizedPhone, accountId, hint);
            return new LoginResult(false, null, hint);
        }

        Task<WTelegram.Client> CreateLoginClientAsync() =>
            _clientPool.GetOrCreateClientAsync(
                accountId,
                apiId,
                apiHash,
                sessionPath,
                apiHash,
                normalizedPhone,
                null,
                proxyOverride);
    }

    public Task<QrLoginResult> StartQrLoginAsync(
        int loginId,
        AccountProxyResolution proxyResolution,
        TelegramApiCredentials apiCredentials)
    {
        ArgumentNullException.ThrowIfNull(proxyResolution);
        return StartQrLoginCoreAsync(loginId, proxyResolution, apiCredentials);
    }

    private async Task<QrLoginResult> StartQrLoginCoreAsync(
        int loginId,
        AccountProxyResolution proxyOverride,
        TelegramApiCredentials apiCredentials)
    {
        if (loginId <= 0)
            loginId = Random.Shared.Next(1, int.MaxValue);

        if (!TelegramApiProfilePool.TryNormalizeCredentials(apiCredentials.ApiId, apiCredentials.ApiHash, out var apiHash, out var apiHashReason))
            return new QrLoginResult(false, loginId, "failed", $"Telegram API 配置无效：{apiHashReason}");

        var apiId = apiCredentials.ApiId;

        var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
        Directory.CreateDirectory(sessionsPath);
        var tempPath = Path.Combine(sessionsPath, $".qr-login-{loginId}-{Guid.NewGuid():N}.session");

        await CancelQrLoginStrictAsync(loginId);

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var session = new QrLoginSession(loginId, tempPath, apiId, apiHash, cts);
        if (!QrLoginSessions.TryAdd(loginId, session))
        {
            cts.Dispose();
            return new QrLoginResult(false, loginId, "failed", "无法创建扫码登录会话，请重试");
        }

        try
        {
            session.Client = CreateStandaloneClient(
                apiId,
                apiHash,
                tempPath,
                apiHash,
                session.WaitForPassword,
                proxyOverride);
            session.LoginTask = RunQrLoginAsync(session);

            for (var i = 0; i < 40; i++)
            {
                if (!string.IsNullOrWhiteSpace(session.QrLoginUrl))
                    return session.ToResult();

                if (session.LoginTask.IsCompleted)
                    return await PollQrLoginAsync(loginId);

                await Task.Delay(100, CancellationToken.None);
            }

            return session.ToResult("pending", "二维码正在生成，请稍后刷新");
        }
        catch (Exception ex)
        {
            await CleanupQrLoginSessionStrictAsync(loginId, deleteSessionFile: true);
            _logger.LogWarning(ex, "Start QR login failed (loginId={LoginId})", loginId);
            return new QrLoginResult(false, loginId, "failed", BuildFriendlyQrLoginError(ex));
        }
    }

    public async Task<QrLoginResult> PollQrLoginAsync(int loginId)
    {
        if (!QrLoginSessions.TryGetValue(loginId, out var session))
            return new QrLoginResult(false, loginId, "expired", "扫码登录会话已失效，请重新生成二维码");

        if (session.LoginTask is { IsCompleted: true })
            await ApplyQrLoginTaskResultAsync(session);

        if (session.Status is "authorized" && session.Account != null)
        {
            await FinalizeQrLoginSessionAsync(session);
            return session.ToResult();
        }

        if (session.Status is "failed" or "expired")
        {
            await CleanupQrLoginSessionStrictAsync(loginId, deleteSessionFile: true);
            return session.ToResult();
        }

        if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            session.Status = "expired";
            session.Message = "二维码已过期，请重新生成";
            await CleanupQrLoginSessionStrictAsync(loginId, deleteSessionFile: true);
            return session.ToResult();
        }

        return session.ToResult();
    }

    public async Task<QrLoginResult> SubmitQrPasswordAsync(int loginId, string password)
    {
        if (!QrLoginSessions.TryGetValue(loginId, out var session))
            return new QrLoginResult(false, loginId, "expired", "扫码登录会话已失效，请重新生成二维码");

        password = password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(password))
            return session.ToResult("password", "请输入两步验证密码");

        if (!session.TrySubmitPassword(password))
            return session.ToResult("password", "当前扫码登录尚未进入二级密码校验状态，请稍后重试");

        session.Status = "pending";
        session.Message = "正在验证两步验证密码";

        for (var i = 0; i < 300; i++)
        {
            if (session.LoginTask is { IsCompleted: true })
            {
                await ApplyQrLoginTaskResultAsync(session);
                break;
            }

            if (session.Status is "authorized" or "failed" or "expired")
                break;

            if (session.Status == "password" && session.IsWaitingForPassword)
                return session.ToResult("password", "两步验证密码错误，请重新输入");

            await Task.Delay(100, CancellationToken.None);
        }

        if (session.Status is "authorized" && session.Account != null)
        {
            await FinalizeQrLoginSessionAsync(session);
            return session.ToResult();
        }

        if (session.Status is "failed" or "expired")
        {
            await CleanupQrLoginSessionStrictAsync(loginId, deleteSessionFile: true);
            return session.ToResult();
        }

        return session.ToResult();
    }

    public Task CancelQrLoginAsync(int loginId)
    {
        return CleanupQrLoginSessionStrictAsync(loginId, deleteSessionFile: true);
    }

    public Task CancelQrLoginStrictAsync(int loginId)
    {
        return CleanupQrLoginSessionStrictAsync(loginId, deleteSessionFile: true);
    }

    public Task ReleaseCompletedQrLoginAsync(int loginId)
    {
        return CleanupQrLoginSessionStrictAsync(loginId, deleteSessionFile: false);
    }

    private static string BuildFriendlyStartLoginError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;

        if (ex is FormatException
            || msg.Contains("hex", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("hexadecimal", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("十六进制", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("16进制", StringComparison.OrdinalIgnoreCase))
        {
            return "发送验证码失败：检测到 ApiHash 格式异常。请到【系统设置】重新填写 Telegram ApiHash（my.telegram.org 获取的 32 位十六进制字符串）。";
        }

        if (ex is IOException
            || msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("process cannot access", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("被另一进程使用", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("被另一个进程使用", StringComparison.OrdinalIgnoreCase))
        {
            return "发送验证码失败：session 文件被占用。请稍后重试；若你部署了多个实例共享同一 sessions 目录，请改为单实例或为每个实例使用独立 sessions 目录。";
        }

        return $"发送验证码失败：{ex.GetType().Name}: {ex.Message}";
    }

    public async Task<LoginResult> ResendCodeAsync(int accountId)
    {
        var client = _clientPool.GetClient(accountId)
            ?? throw new InvalidOperationException($"Client not found for account {accountId}");

        // WTelegram 约定：verification_code 提交空字符串会触发“通过另一种方式重发验证码”（短信/电话等）
        var result = await client.Login(string.Empty);
        _logger.LogInformation("Resend code requested for temp account {AccountId}, next step: {Step}", accountId, result);

        return result switch
        {
            "verification_code" => new LoginResult(false, "code", "已请求重新发送验证码"),
            "password" => new LoginResult(false, "password", "需要两步验证密码"),
            _ when client.User != null => new LoginResult(true, null, "登录成功", MapToAccountInfo(accountId, client, GetPendingLoginApi(accountId))),
            _ => new LoginResult(false, null, $"重新发送失败：{result}")
        };
    }

    private static bool LooksLikeSessionApiMismatchOrCorrupted(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash/id/key", StringComparison.OrdinalIgnoreCase);
    }

    private void TryBackupSqliteSessionIfExists(string sessionPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(sessionPath);
            if (!File.Exists(fullPath))
                return;

            if (!LooksLikeSqliteSession(fullPath))
                return;

            var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var name = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);
            var backupPath = Path.Combine(dir, $"{name}.sqlite.bak{ext}");
            File.Move(fullPath, backupPath, overwrite: true);
            _logger.LogWarning("Detected SQLite session, backed up from {SessionPath} to {BackupPath}", fullPath, backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backup sqlite session: {SessionPath}", sessionPath);
        }
    }

    private void TryBackupCorruptedSessionIfExists(string sessionPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(sessionPath);
            if (!File.Exists(fullPath))
                return;

            var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var name = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);
            var backupPath = Path.Combine(dir, $"{name}.corrupt.bak{ext}");
            File.Move(fullPath, backupPath, overwrite: true);
            _logger.LogWarning("Detected corrupted/mismatched session, backed up from {SessionPath} to {BackupPath}", fullPath, backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backup corrupted session: {SessionPath}", sessionPath);
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

    public async Task<LoginResult> SubmitCodeAsync(int accountId, string code)
    {
        var client = _clientPool.GetClient(accountId)
            ?? throw new InvalidOperationException($"Client not found for account {accountId}");

        code = (code ?? string.Empty).Trim();
        var result = await client.Login(code);

        return result switch
        {
            "password" => new LoginResult(false, "password", "请输入两步验证密码"),
            _ when client.User != null => new LoginResult(true, null, "登录成功", MapToAccountInfo(accountId, client, GetPendingLoginApi(accountId))),
            _ => new LoginResult(false, null, $"验证码错误或已过期: {result}")
        };
    }

    public async Task<LoginResult> SubmitPasswordAsync(int accountId, string password)
    {
        var client = _clientPool.GetClient(accountId)
            ?? throw new InvalidOperationException($"Client not found for account {accountId}");

        var result = await client.Login(password);

        return result switch
        {
            _ when client.User != null => new LoginResult(true, null, "登录成功", MapToAccountInfo(accountId, client, GetPendingLoginApi(accountId))),
            _ => new LoginResult(false, "password", "密码错误")
        };
    }

    public Task<AccountInfo?> GetAccountInfoAsync(int accountId)
    {
        var client = _clientPool.GetClient(accountId);
        if (client?.User == null) return Task.FromResult<AccountInfo?>(null);

        return Task.FromResult<AccountInfo?>(MapToAccountInfo(accountId, client, GetPendingLoginApi(accountId)));
    }

    public async Task SyncAccountDataAsync(int accountId)
    {
        var client = _clientPool.GetClient(accountId)
            ?? throw new InvalidOperationException($"Client not found for account {accountId}");

        _logger.LogInformation("Syncing data for account {AccountId}", accountId);

        // 获取所有对话
        var dialogs = await client.Messages_GetAllDialogs();

        _logger.LogInformation("Account {AccountId} has {Count} dialogs", accountId, dialogs.Dialogs.Length);

        // TODO: 保存到数据库
    }

    public Task<AccountStatus> CheckStatusAsync(int accountId)
    {
        var client = _clientPool.GetClient(accountId);

        if (client == null)
            return Task.FromResult(AccountStatus.Offline);

        if (client.User == null)
            return Task.FromResult(AccountStatus.NeedRelogin);

        return Task.FromResult(AccountStatus.Active);
    }

    public async Task ReleaseClientAsync(int accountId)
    {
        PendingLoginApis.TryRemove(accountId, out _);
        await _clientPool.RemoveClientAsync(accountId);
    }

    public async Task ReleaseClientStrictAsync(int accountId)
    {
        PendingLoginApis.TryRemove(accountId, out _);
        await _clientPool.RemoveClientStrictAsync(accountId);
    }

    private async Task RunQrLoginAsync(QrLoginSession session)
    {
        try
        {
            var client = session.Client ?? throw new InvalidOperationException("扫码登录客户端未初始化");
            var user = await client.LoginWithQRCode(
                qrDisplay: loginUrl =>
                {
                    session.QrLoginUrl = loginUrl;
                    session.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(55);
                    session.Status = "pending";
                    session.Message = "请使用 Telegram 扫描二维码并确认登录";
                },
                except_ids: Array.Empty<long>(),
                logoutFirst: false,
                ct: session.Cancellation.Token);

            if (user == null)
            {
                session.Status = "failed";
                session.Message = "Telegram 未返回扫码登录结果";
                return;
            }

            session.Account = MapToAccountInfo(session.LoginId, client, session.ApiId, session.ApiHash);
            session.Status = "authorized";
            session.Message = "扫码登录成功";
        }
        catch (OperationCanceledException)
        {
            session.Status = "expired";
            session.Message = "扫码登录已取消或已超时";
        }
        catch (Exception ex) when (LooksLikePasswordRequired(ex))
        {
            session.Status = "password";
            session.Message = "此账号启用了两步验证，请输入密码";
        }
        catch (Exception ex)
        {
            session.Status = "failed";
            session.Message = BuildFriendlyQrLoginError(ex);
            _logger.LogWarning(ex, "QR login task failed (loginId={LoginId})", session.LoginId);
        }
    }

    private async Task ApplyQrLoginTaskResultAsync(QrLoginSession session)
    {
        if (session.LoginTask == null)
            return;

        try
        {
            await session.LoginTask;
        }
        catch (OperationCanceledException)
        {
            session.Status = "expired";
            session.Message = "扫码登录已取消或已超时";
        }
        catch (Exception ex)
        {
            session.Status = "failed";
            session.Message = BuildFriendlyQrLoginError(ex);
            _logger.LogWarning(ex, "QR login task result failed (loginId={LoginId})", session.LoginId);
        }
    }

    private async Task FinalizeQrLoginSessionAsync(QrLoginSession session)
    {
        if (session.Account?.Phone == null)
            return;

        await session.LifecycleLock.WaitAsync();
        try
        {
            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            Directory.CreateDirectory(sessionsPath);
            var phoneDigits = PhoneNumberFormatter.NormalizeToDigits(session.Account.Phone);
            if (string.IsNullOrWhiteSpace(phoneDigits))
                return;

            var finalPath = Path.Combine(sessionsPath, $"{phoneDigits}.session");
            if (session.Client != null)
            {
                try
                {
                    await session.Client.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to strictly finalize QR login client (loginId={LoginId})",
                        session.LoginId);
                    throw new InvalidOperationException(
                        $"二维码登录 {session.LoginId} 的客户端无法确认已断开，已保留冻结代理",
                        ex);
                }

                session.Client = null;
            }

            try
            {
                if (File.Exists(session.TempSessionPath))
                {
                    TryBackupSqliteSessionIfExists(finalPath);
                    File.Move(session.TempSessionPath, finalPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                session.Status = "failed";
                session.Message = $"扫码登录成功，但保存 session 文件失败：{ex.Message}";
                _logger.LogWarning(
                    ex,
                    "Failed to move QR login session file (loginId={LoginId})",
                    session.LoginId);
                return;
            }

            session.KeepTempSession = true;
        }
        finally
        {
            session.LifecycleLock.Release();
        }
    }

    private WTelegram.Client CreateStandaloneClient(
        int apiId,
        string apiHash,
        string sessionPath,
        string sessionKey,
        Func<string>? passwordProvider,
        AccountProxyResolution proxyOverride)
    {
        ArgumentNullException.ThrowIfNull(proxyOverride);
        var accountProxy = proxyOverride.Proxy
                           ?? (proxyOverride.UseGlobalProxy
                               ? throw new InvalidOperationException(
                                   "全局代理路由未在首次连接前解析，已阻止降级为直连")
                               : null);
        var deviceProfile = TelegramClientDeviceProfile.ForStableKey($"{apiId}:{sessionPath}");

        string Config(string what)
        {
            return what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => sessionPath,
                "session_key" => sessionKey,
                "password" => passwordProvider?.Invoke() ?? null!,
                "app_id" => apiId.ToString(),
                "app_hash" => apiHash,
                "app_version" or "device_model" or "system_version" or "system_lang_code" or "lang_code" => deviceProfile.GetConfigValue(what)!,

                _ => null!
            };
        }

        var client = new WTelegram.Client(Config);
        TelegramClientProxyConfigurator.Apply(client, accountProxy);
        return client;
    }

    private async Task CleanupQrLoginSessionStrictAsync(
        int loginId,
        bool deleteSessionFile)
    {
        if (!QrLoginSessions.TryGetValue(loginId, out var session))
            return;

        await session.LifecycleLock.WaitAsync();
        try
        {
            if (!QrLoginSessions.TryGetValue(loginId, out var current)
                || !ReferenceEquals(current, session))
            {
                return;
            }

            try
            {
                session.CancelPasswordWait();
                session.Cancellation.Cancel();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cancel QR login wait (loginId={LoginId})", loginId);
            }

            if (session.Client != null)
            {
                try
                {
                    await session.Client.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to strictly dispose QR login client (loginId={LoginId})",
                        loginId);
                    throw new InvalidOperationException(
                        $"二维码登录 {loginId} 的旧 Telegram 客户端无法确认已断开",
                        ex);
                }

                session.Client = null;
            }

            var removed = ((ICollection<KeyValuePair<int, QrLoginSession>>)QrLoginSessions)
                .Remove(new KeyValuePair<int, QrLoginSession>(loginId, session));
            if (!removed)
                return;

            session.Cancellation.Dispose();
            if (deleteSessionFile && !session.KeepTempSession)
                TryDeleteFile(session.TempSessionPath);
        }
        finally
        {
            session.LifecycleLock.Release();
        }
    }

    private static TelegramApiCredentials GetPendingLoginApi(int accountId)
    {
        if (PendingLoginApis.TryGetValue(accountId, out var credentials))
            return credentials;

        return new TelegramApiCredentials(0, string.Empty);
    }

    private static bool LooksLikePasswordRequired(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("SESSION_PASSWORD_NEEDED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) && msg.Contains("NEEDED", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("config value for password", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFriendlyQrLoginError(Exception ex)
    {
        if (LooksLikePasswordRequired(ex))
            return "此账号启用了两步验证，请输入密码";

        var msg = (ex.Message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(msg))
            return $"扫码登录失败：{ex.GetType().Name}";

        if (msg.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
            return $"扫码登录触发 Telegram 限流：{msg}";

        return $"扫码登录失败：{ex.GetType().Name}: {msg}";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static AccountInfo MapToAccountInfo(int accountId, WTelegram.Client client, TelegramApiCredentials apiCredentials)
        => MapToAccountInfo(accountId, client, apiCredentials.ApiId, apiCredentials.ApiHash);

    private static AccountInfo MapToAccountInfo(int accountId, WTelegram.Client client, int apiId = 0, string? apiHash = null)
    {
        var user = client.User!;
        return new AccountInfo
        {
            Id = accountId,
            TelegramUserId = user.id,
            Phone = user.phone,
            Username = user.MainUsername,
            FirstName = user.first_name,
            LastName = user.last_name,
            ApiId = apiId,
            ApiHash = apiHash,
            Status = Models.AccountStatus.Active,
            LastActiveAt = DateTime.UtcNow
        };
    }

    private static string NormalizePhoneForLogin(string phone)
    {
        phone = (phone ?? string.Empty).Trim();
        if (phone.StartsWith("+", StringComparison.Ordinal))
            phone = phone[1..];
        if (phone.StartsWith("00", StringComparison.Ordinal))
            phone = phone[2..];

        Span<char> buf = stackalloc char[phone.Length];
        var n = 0;
        foreach (var ch in phone)
        {
            if (ch is >= '0' and <= '9')
                buf[n++] = ch;
        }

        if (n == 0)
            throw new ArgumentException("手机号格式不正确，请包含国家代码（例如：+8613800138000）", nameof(phone));

        return new string(buf[..n]);
    }

    private sealed class QrLoginSession
    {
        public QrLoginSession(int loginId, string tempSessionPath, int apiId, string apiHash, CancellationTokenSource cancellation)
        {
            LoginId = loginId;
            TempSessionPath = tempSessionPath;
            ApiId = apiId;
            ApiHash = apiHash;
            Cancellation = cancellation;
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        }

        public int LoginId { get; }
        public string TempSessionPath { get; }
        public int ApiId { get; }
        public string ApiHash { get; }
        public CancellationTokenSource Cancellation { get; }
        public SemaphoreSlim LifecycleLock { get; } = new(1, 1);
        public WTelegram.Client? Client { get; set; }
        public Task? LoginTask { get; set; }
        public string Status { get; set; } = "pending";
        public string? Message { get; set; } = "请使用 Telegram 扫描二维码并确认登录";
        public string? QrLoginUrl { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public AccountInfo? Account { get; set; }
        public bool KeepTempSession { get; set; }
        private readonly object _passwordLock = new();
        private TaskCompletionSource<string>? _passwordWaiter;

        public bool IsWaitingForPassword
        {
            get
            {
                lock (_passwordLock)
                    return _passwordWaiter != null;
            }
        }

        public string WaitForPassword()
        {
            TaskCompletionSource<string> waiter;
            lock (_passwordLock)
            {
                Status = "password";
                Message = "此账号启用了两步验证，请输入密码";
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                _passwordWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = _passwordWaiter;
            }

            using var _ = Cancellation.Token.Register(() => waiter.TrySetCanceled(Cancellation.Token));
            try
            {
                return waiter.Task.GetAwaiter().GetResult();
            }
            finally
            {
                lock (_passwordLock)
                {
                    if (ReferenceEquals(_passwordWaiter, waiter))
                        _passwordWaiter = null;
                }
            }
        }

        public bool TrySubmitPassword(string password)
        {
            lock (_passwordLock)
            {
                if (_passwordWaiter == null)
                    return false;

                _passwordWaiter.TrySetResult(password.Trim());
                return true;
            }
        }

        public void CancelPasswordWait()
        {
            lock (_passwordLock)
            {
                _passwordWaiter?.TrySetCanceled(Cancellation.Token);
                _passwordWaiter = null;
            }
        }

        public QrLoginResult ToResult(string? status = null, string? message = null)
        {
            if (!string.IsNullOrWhiteSpace(status))
                Status = status;
            if (!string.IsNullOrWhiteSpace(message))
                Message = message;

            return new QrLoginResult(
                Success: Status == "authorized" && Account != null,
                LoginId: LoginId,
                Status: Status,
                Message: Message,
                QrLoginUrl: QrLoginUrl,
                ExpiresAtUtc: ExpiresAtUtc,
                Account: Account);
        }
    }
}
