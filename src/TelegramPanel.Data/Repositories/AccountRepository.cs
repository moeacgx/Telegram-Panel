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

    private IQueryable<Account> BuildQuery(int? categoryId, string? search, bool onlyWaste)
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

            // 允许用户直接粘贴 “+86 138 0013 8000” 等格式，统一提取纯数字后匹配 Phone 字段
            var phoneDigits = NormalizeDigits(search);
            var phoneLike = phoneDigits.Length > 0 ? $"%{phoneDigits}%" : like;
            if (long.TryParse(search, out var uid) && uid > 0)
            {
                query = query.Where(a =>
                    a.UserId == uid
                    || EF.Functions.Like(a.Phone, phoneLike)
                    || (a.Nickname != null && EF.Functions.Like(a.Nickname, like))
                    || (a.Username != null && EF.Functions.Like(a.Username, like)));
            }
            else
            {
                query = query.Where(a =>
                    EF.Functions.Like(a.Phone, phoneLike)
                    || (a.Nickname != null && EF.Functions.Like(a.Nickname, like))
                    || (a.Username != null && EF.Functions.Like(a.Username, like)));
            }
        }

        if (onlyWaste)
        {
            // 仅废号：基于“上次 Telegram 状态检测摘要（Summary）”做筛选。
            // 注意：未检测的账号（Summary 为空）不会被包含。
            query = query.Where(a =>
                a.TelegramStatusSummary != null
                && (
                    // 账号封禁/停用
                    EF.Functions.Like(a.TelegramStatusSummary, "%账号被封禁%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%被封禁/停用%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%USER_DEACTIVATED%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%PHONE_NUMBER_BANNED%")

                    // Session 失效/损坏
                    || EF.Functions.Like(a.TelegramStatusSummary, "%Session 失效%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%AUTH_KEY_UNREGISTERED%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%Session 无法读取%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%Can't read session block%")

                    // 受限/冻结/未登录
                    || EF.Functions.Like(a.TelegramStatusSummary, "%账号受限%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%Restricted%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%账号被冻结%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%FROZEN_METHOD_INVALID%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%需要两步验证密码%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%SESSION_PASSWORD_NEEDED%")

                    // 注销/删除
                    || EF.Functions.Like(a.TelegramStatusSummary, "%账号已注销%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%已注销/被删除%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%被删除%")

                    // 连接/探测失败（按“废号”处理）
                    || EF.Functions.Like(a.TelegramStatusSummary, "%连接失败%")
                    || EF.Functions.Like(a.TelegramStatusSummary, "%创建频道探测失败%")
                ));
        }

        return query.OrderByDescending(a => a.Id);
    }

    private static string NormalizeDigits(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        Span<char> buf = stackalloc char[text.Length];
        var n = 0;
        foreach (var ch in text)
        {
            if (ch is >= '0' and <= '9')
                buf[n++] = ch;
        }

        return n == 0 ? string.Empty : new string(buf[..n]);
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
        bool onlyWaste,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0) pageIndex = 0;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 500) pageSize = 500;

        var query = BuildQuery(categoryId, search, onlyWaste);
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
        bool onlyWaste,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(categoryId, search, onlyWaste);
        return await query.ToListAsync(cancellationToken);
    }
}
