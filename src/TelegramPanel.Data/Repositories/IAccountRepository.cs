using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号仓储接口
/// </summary>
public interface IAccountRepository : IRepository<Account>
{
    Task<Account?> GetByPhoneAsync(string phone);
    Task<Account?> GetByUserIdAsync(long userId);
    Task<IEnumerable<Account>> GetByCategoryAsync(int categoryId);
    Task<IEnumerable<Account>> GetActiveAccountsAsync();

    Task<(IReadOnlyList<Account> Items, int TotalCount)> QueryPagedAsync(
        int? categoryId,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Account>> QueryAsync(
        int? categoryId,
        string? search,
        CancellationToken cancellationToken = default);
}
