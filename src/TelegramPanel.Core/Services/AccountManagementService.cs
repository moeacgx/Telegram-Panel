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
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await _accountRepository.QueryPagedAsync(categoryId, search, pageIndex, pageSize, cancellationToken);
        foreach (var a in items)
            FormatPhoneForDisplay(a);
        return (items, total);
    }

    public async Task<IReadOnlyList<Account>> QueryAccountsAsync(
        int? categoryId,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var list = await _accountRepository.QueryAsync(categoryId, search, cancellationToken);
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
