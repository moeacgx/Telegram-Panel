using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
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

    public AccountService(ITelegramClientPool clientPool, ILogger<AccountService> logger, IConfiguration configuration)
    {
        _clientPool = clientPool;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<LoginResult> StartLoginAsync(int accountId, string phone)
    {
        if (!int.TryParse(_configuration["Telegram:ApiId"], out var apiId) || apiId <= 0
            || string.IsNullOrWhiteSpace(_configuration["Telegram:ApiHash"]))
        {
            return new LoginResult(false, null, "请先在【系统设置】中配置全局 Telegram API（ApiId/ApiHash）");
        }

        var apiHash = _configuration["Telegram:ApiHash"]!.Trim();
        var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
        Directory.CreateDirectory(sessionsPath);
        var sessionPath = Path.Combine(sessionsPath, $"{phone}.session");

        // 若已有旧的 SQLite 格式 session（Telethon/Pyrogram/Telegram Desktop 常见），会导致 WTelegramClient 直接读取失败，
        // 这里先自动备份，确保“手机号登录”能顺利重新生成 WTelegram 的 session。
        TryBackupSqliteSessionIfExists(sessionPath);

        _logger.LogInformation("Starting login for phone {Phone}", phone);

        var client = await _clientPool.GetOrCreateClientAsync(accountId, apiId, apiHash, sessionPath);

        string result;
        try
        {
            result = await client.Login(phone);
        }
        catch (Exception ex) when (LooksLikeSessionApiMismatchOrCorrupted(ex))
        {
            // session 与 ApiId/ApiHash 不匹配或损坏，备份后重新开始登录流程
            TryBackupCorruptedSessionIfExists(sessionPath);
            await _clientPool.RemoveClientAsync(accountId);

            client = await _clientPool.GetOrCreateClientAsync(accountId, apiId, apiHash, sessionPath);
            result = await client.Login(phone);
        }

        return result switch
        {
            "verification_code" => new LoginResult(false, "code", "请输入验证码"),
            "password" => new LoginResult(false, "password", "请输入两步验证密码"),
            "name" => new LoginResult(false, "signup", "需要注册新账号"),
            _ when client.User != null => new LoginResult(true, null, "登录成功", MapToAccountInfo(accountId, client)),
            _ => new LoginResult(false, null, $"未知状态: {result}")
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

        var result = await client.Login(code);

        return result switch
        {
            "password" => new LoginResult(false, "password", "请输入两步验证密码"),
            _ when client.User != null => new LoginResult(true, null, "登录成功", MapToAccountInfo(accountId, client)),
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
            _ when client.User != null => new LoginResult(true, null, "登录成功", MapToAccountInfo(accountId, client)),
            _ => new LoginResult(false, "password", "密码错误")
        };
    }

    public Task<AccountInfo?> GetAccountInfoAsync(int accountId)
    {
        var client = _clientPool.GetClient(accountId);
        if (client?.User == null) return Task.FromResult<AccountInfo?>(null);

        return Task.FromResult<AccountInfo?>(MapToAccountInfo(accountId, client));
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

    private static AccountInfo MapToAccountInfo(int accountId, WTelegram.Client client)
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
            Status = Models.AccountStatus.Active,
            LastActiveAt = DateTime.UtcNow
        };
    }
}
