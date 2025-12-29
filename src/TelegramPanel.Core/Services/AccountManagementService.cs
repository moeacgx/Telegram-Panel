using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Utils;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 账号数据管理服务
/// </summary>
public class AccountManagementService
{
    private readonly IAccountRepository _accountRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IGroupRepository _groupRepository;
    private readonly ITelegramClientPool _clientPool;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AccountManagementService> _logger;

    public AccountManagementService(
        IAccountRepository accountRepository,
        IChannelRepository channelRepository,
        IGroupRepository groupRepository,
        ITelegramClientPool clientPool,
        IConfiguration configuration,
        ILogger<AccountManagementService> logger)
    {
        _accountRepository = accountRepository;
        _channelRepository = channelRepository;
        _groupRepository = groupRepository;
        _clientPool = clientPool;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Account?> GetAccountAsync(int id)
    {
        var account = await _accountRepository.GetByIdAsync(id);
        FormatPhoneForDisplay(account);
        return account;
    }

    public async Task<Account?> GetAccountByPhoneAsync(string phone)
    {
        var digits = PhoneNumberFormatter.NormalizeToDigits(phone);
        var account = await _accountRepository.GetByPhoneAsync(digits);
        FormatPhoneForDisplay(account);
        return account;
    }

    public async Task<IEnumerable<Account>> GetAllAccountsAsync()
    {
        var list = (await _accountRepository.GetAllAsync()).ToList();
        foreach (var a in list)
            FormatPhoneForDisplay(a);
        return list;
    }

    public async Task<(IReadOnlyList<Account> Items, int TotalCount)> QueryAccountsPagedAsync(
        int? categoryId,
        string? search,
        int pageIndex,
        int pageSize,
        bool onlyWaste = false,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await _accountRepository.QueryPagedAsync(categoryId, search, pageIndex, pageSize, onlyWaste, cancellationToken);
        foreach (var a in items)
            FormatPhoneForDisplay(a);
        return (items, total);
    }

    public async Task<IReadOnlyList<Account>> QueryAccountsAsync(
        int? categoryId,
        string? search,
        bool onlyWaste = false,
        CancellationToken cancellationToken = default)
    {
        var list = await _accountRepository.QueryAsync(categoryId, search, onlyWaste, cancellationToken);
        foreach (var a in list)
            FormatPhoneForDisplay(a);
        return list;
    }

    public async Task<IEnumerable<Account>> GetActiveAccountsAsync()
    {
        var list = (await _accountRepository.GetActiveAccountsAsync()).ToList();
        foreach (var a in list)
            FormatPhoneForDisplay(a);
        return list;
    }

    public async Task<IEnumerable<Account>> GetAccountsByCategoryAsync(int categoryId)
    {
        var list = (await _accountRepository.GetByCategoryAsync(categoryId)).ToList();
        foreach (var a in list)
            FormatPhoneForDisplay(a);
        return list;
    }

    public async Task<Account> CreateAccountAsync(Account account)
    {
        account.Phone = PhoneNumberFormatter.NormalizeToDigits(account.Phone);
        return await _accountRepository.AddAsync(account);
    }

    public async Task UpdateAccountAsync(Account account)
    {
        account.Phone = PhoneNumberFormatter.NormalizeToDigits(account.Phone);
        await _accountRepository.UpdateAsync(account);
    }

    public async Task DeleteAccountAsync(int id)
    {
        var account = await _accountRepository.GetByIdAsync(id);
        if (account != null)
        {
            try
            {
                // 先断开客户端，释放 session 文件锁
                await _clientPool.RemoveClientAsync(account.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove client for account {AccountId} before deletion", account.Id);
            }

            TryDeleteAccountFiles(account);
            await _accountRepository.DeleteAsync(account);
        }
    }

    /// <summary>
    /// 清理废号：强制尝试删除账号记录与 session 文件。
    /// - 删除前会先从 TelegramClientPool 释放客户端，避免 session 被占用
    /// - 若仍存在被占用的 session 文件，会重试并在最终失败时抛出异常（用于提示 UI）
    /// </summary>
    public async Task PurgeAccountAsync(int id, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByIdAsync(id);
        if (account == null)
            return;

        try
        {
            await _clientPool.RemoveClientAsync(account.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove client for account {AccountId} before purge", account.Id);
        }

        var sessionCandidates = ResolveSessionFileCandidates(account).ToList();
        var existingSessionFiles = sessionCandidates
            .Select(p => SafeGetFullPath(p))
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failedSessionDeletes = new List<string>();
        foreach (var sessionPath in existingSessionFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ok = await TryDeleteFileWithRetriesAsync(
                fullPath: sessionPath,
                maxAttempts: 6,
                baseDelayMs: 150,
                accountId: account.Id,
                cancellationToken: cancellationToken);

            if (!ok)
                failedSessionDeletes.Add(sessionPath);
        }

        if (failedSessionDeletes.Count > 0)
        {
            var hint = string.Join(Environment.NewLine, failedSessionDeletes.Take(5));
            if (failedSessionDeletes.Count > 5)
                hint += Environment.NewLine + $"...（仅展示前 5 个，共 {failedSessionDeletes.Count} 个）";

            throw new InvalidOperationException("无法删除 session 文件（可能仍被占用，请稍后重试）：\n" + hint);
        }

        // session 删除成功后，再尽力清理其它关联文件（json/备份等）
        TryDeleteAccountFiles(account);
        await _accountRepository.DeleteAsync(account);
    }

    private void TryDeleteAccountFiles(Account account)
    {
        try
        {
            var candidates = ResolveSessionFileCandidates(account);
            foreach (var path in candidates)
            {
                TryDeleteFile(path);
                TryDeleteFile(BuildBackupPath(path, "sqlite.bak"));
                TryDeleteFile(BuildBackupPath(path, "corrupt.bak"));
                TryDeleteFile(BuildBackupPath(path, "bak"));
                TryDeleteFile(BuildBackupPath(path, "wt"));

                var dir = Path.GetDirectoryName(path);
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(name))
                    TryDeleteFile(Path.Combine(dir, $"{name}.json"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete account files for {Phone}", account.Phone);
        }
    }

    private static string SafeGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private IEnumerable<string> ResolveSessionFileCandidates(Account account)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(account.SessionPath))
            AddCandidatePath(account.SessionPath);

        // 常见命名：sessions/<phone>.session
        var phoneDigits = NormalizePhone(account.Phone);
        if (!string.IsNullOrWhiteSpace(phoneDigits))
        {
            var sessionsPath = _configuration["Telegram:SessionsPath"] ?? "sessions";
            AddCandidatePath(Path.Combine(sessionsPath, $"{phoneDigits}.session"));
            AddCandidatePath(Path.Combine("sessions", $"{phoneDigits}.session"));
            AddCandidatePath(Path.Combine("src", "TelegramPanel.Web", "sessions", $"{phoneDigits}.session"));
        }

        return set;

        void AddCandidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Path.IsPathRooted(path))
                {
                    set.Add(Path.GetFullPath(path));
                    return;
                }

                // 1) 基于当前工作目录
                set.Add(Path.GetFullPath(path));

                // 2) 基于仓库根目录（如果能找到 TelegramPanel.sln）
                var repoRoot = TryFindRepoRoot();
                if (!string.IsNullOrWhiteSpace(repoRoot))
                    set.Add(Path.GetFullPath(Path.Combine(repoRoot, path)));
            }
            catch
            {
                // ignore invalid paths
            }
        }
    }

    private static string BuildBackupPath(string originalSessionPath, string suffix)
    {
        try
        {
            var fullPath = Path.GetFullPath(originalSessionPath);
            var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var name = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);
            return Path.Combine(dir, $"{name}.{suffix}{ext}");
        }
        catch
        {
            return originalSessionPath;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return;

            File.Delete(fullPath);
            _logger.LogInformation("Deleted file: {Path}", fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file: {Path}", path);
        }
    }

    private async Task<bool> TryDeleteFileWithRetriesAsync(
        string fullPath,
        int maxAttempts,
        int baseDelayMs,
        int accountId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return true;

        if (maxAttempts < 1) maxAttempts = 1;
        if (baseDelayMs < 0) baseDelayMs = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(fullPath))
                    return true;

                File.Delete(fullPath);
                _logger.LogInformation("Deleted file: {Path}", fullPath);
                return true;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException || ex is UnauthorizedAccessException))
            {
                // 典型场景：session 被 WTelegram/其它后台任务占用，先再移除一次 client，再做短退避
                try
                {
                    await _clientPool.RemoveClientAsync(accountId);
                }
                catch
                {
                    // ignore
                }

                var delayMs = baseDelayMs * attempt;
                if (delayMs > 3000) delayMs = 3000;
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file: {Path}", fullPath);
                return false;
            }
        }

        return !File.Exists(fullPath);
    }

    private static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        var digits = new char[phone.Length];
        var count = 0;
        foreach (var ch in phone)
        {
            if (ch >= '0' && ch <= '9')
                digits[count++] = ch;
        }
        return count == 0 ? string.Empty : new string(digits, 0, count);
    }

    private static void FormatPhoneForDisplay(Account? account)
    {
        if (account == null)
            return;

        // 只设置 DisplayPhone（非持久化字段），不修改 Phone（持久化字段）
        // 这样可以避免 EF Core 追踪 Phone 的变化导致 UNIQUE 约束冲突
        account.DisplayPhone = PhoneNumberFormatter.FormatWithCountryCode(account.Phone);
    }

    private static string? TryFindRepoRoot()
    {
        var current = Directory.GetCurrentDirectory();
        for (int i = 0; i < 10 && !string.IsNullOrWhiteSpace(current); i++)
        {
            if (File.Exists(Path.Combine(current, "TelegramPanel.sln")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    public async Task<(int channels, int groups)> GetAccountStatisticsAsync(int accountId)
    {
        var channels = await _channelRepository.GetByCreatorAccountAsync(accountId);
        var groups = await _groupRepository.GetByCreatorAccountAsync(accountId);

        return (channels.Count(), groups.Count());
    }

    public async Task SetAccountActiveStatusAsync(int accountId, bool isActive)
    {
        var account = await _accountRepository.GetByIdAsync(accountId);
        if (account != null)
        {
            account.IsActive = isActive;
            await _accountRepository.UpdateAsync(account);
        }
    }

    public async Task UpdateLastSyncTimeAsync(int accountId)
    {
        var account = await _accountRepository.GetByIdAsync(accountId);
        if (account != null)
        {
            account.LastSyncAt = DateTime.UtcNow;
            await _accountRepository.UpdateAsync(account);
        }
    }

    public async Task UpdateAccountCategoryAsync(int accountId, int? categoryId)
    {
        var account = await _accountRepository.GetByIdAsync(accountId);
        if (account != null)
        {
            account.CategoryId = categoryId;
            await _accountRepository.UpdateAsync(account);
        }
    }
}
