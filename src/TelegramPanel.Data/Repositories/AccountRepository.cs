using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号仓储实现
/// </summary>
public class AccountRepository : Repository<Account>, IAccountRepository
{
    public AccountRepository(AppDbContext context) : base(context)
    {
    }

    private IQueryable<Account> BuildQuery(int? categoryId, string? search)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(a => a.Category)
            .AsQueryable();

        if (categoryId.HasValue && categoryId.Value > 0)
            query = query.Where(a => a.CategoryId == categoryId.Value);

        search = (search ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            // 注意：SQLite LIKE 在默认 collation 下对 ASCII 通常不区分大小写；这里用 Like 保持可翻译性与可读性
            var like = $"%{search}%";
            if (long.TryParse(search, out var uid) && uid > 0)
            {
                query = query.Where(a =>
                    a.UserId == uid
                    || EF.Functions.Like(a.Phone, like)
                    || (a.Nickname != null && EF.Functions.Like(a.Nickname, like))
                    || (a.Username != null && EF.Functions.Like(a.Username, like)));
            }
            else
            {
                query = query.Where(a =>
                    EF.Functions.Like(a.Phone, like)
                    || (a.Nickname != null && EF.Functions.Like(a.Nickname, like))
                    || (a.Username != null && EF.Functions.Like(a.Username, like)));
            }
        }

        return query.OrderByDescending(a => a.Id);
    }

    public override async Task<Account?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(a => a.Category)
            .Include(a => a.Channels)
            .Include(a => a.Groups)
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public override async Task<IEnumerable<Account>> GetAllAsync()
    {
        return await _dbSet
            .Include(a => a.Category)
            .ToListAsync();
    }

    public async Task<Account?> GetByPhoneAsync(string phone)
    {
        return await _dbSet
            .Include(a => a.Category)
            .FirstOrDefaultAsync(a => a.Phone == phone);
    }

    public async Task<Account?> GetByUserIdAsync(long userId)
    {
        return await _dbSet
            .Include(a => a.Category)
            .FirstOrDefaultAsync(a => a.UserId == userId);
    }

    public async Task<IEnumerable<Account>> GetByCategoryAsync(int categoryId)
    {
        return await _dbSet
            .Include(a => a.Category)
            .Where(a => a.CategoryId == categoryId)
            .ToListAsync();
    }

    public async Task<IEnumerable<Account>> GetActiveAccountsAsync()
    {
        return await _dbSet
            .Include(a => a.Category)
            .Where(a => a.IsActive)
            .ToListAsync();
    }

    public async Task<(IReadOnlyList<Account> Items, int TotalCount)> QueryPagedAsync(
        int? categoryId,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0) pageIndex = 0;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 500) pageSize = 500;

        var query = BuildQuery(categoryId, search);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<Account>> QueryAsync(
        int? categoryId,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(categoryId, search);
        return await query.ToListAsync(cancellationToken);
    }
}
