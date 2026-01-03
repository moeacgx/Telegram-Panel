using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public class BotChannelRepository : Repository<BotChannel>, IBotChannelRepository
{
    public BotChannelRepository(AppDbContext context) : base(context)
    {
    }

    private IQueryable<BotChannel> BuildQuery(int botId, int? categoryId, bool broadcastOnly, string? search)
    {
        // 兼容旧模块/旧 UI 约定：categoryId=0 表示“不筛选分类”
        if (categoryId == 0)
            categoryId = null;

        var query = _dbSet
            .AsNoTracking()
            .Include(x => x.Category)
            .Where(x => x.Members.Any(m => m.BotId == botId));

        // 约定：categoryId = -1 表示“未分类”（CategoryId == null）
        if (categoryId.HasValue)
        {
            if (categoryId.Value == -1)
                query = query.Where(x => x.CategoryId == null);
            else
                query = query.Where(x => x.CategoryId == categoryId.Value);
        }

        if (broadcastOnly)
            query = query.Where(x => x.IsBroadcast);

        search = (search ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search}%";
            query = query.Where(x =>
                EF.Functions.Like(x.Title, like)
                || (x.Username != null && EF.Functions.Like(x.Username, like)));
        }

        return query.OrderByDescending(x => x.SyncedAt);
    }

    public async Task<BotChannel?> GetByTelegramIdAsync(int botId, long telegramId)
    {
        return await _dbSet
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.TelegramId == telegramId && x.Members.Any(m => m.BotId == botId));
    }

    public async Task<IEnumerable<BotChannel>> GetForBotAsync(int botId, int? categoryId = null)
    {
        // 兼容旧模块/旧 UI 约定：categoryId=0 表示“不筛选分类”
        if (categoryId == 0)
            categoryId = null;

        var query = _dbSet
            .Include(x => x.Category)
            .Where(x => x.Members.Any(m => m.BotId == botId));

        // 约定：categoryId = -1 表示“未分类”（CategoryId == null）
        if (categoryId.HasValue)
        {
            if (categoryId.Value == -1)
                query = query.Where(x => x.CategoryId == null);
            else
                query = query.Where(x => x.CategoryId == categoryId.Value);
        }

        return await query
            .OrderByDescending(x => x.SyncedAt)
            .ToListAsync();
    }

    public async Task<BotChannel?> GetGlobalByTelegramIdAsync(long telegramId)
    {
        return await _dbSet
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.TelegramId == telegramId);
    }

    public async Task<(IReadOnlyList<BotChannel> Items, int TotalCount)> QueryPagedAsync(
        int botId,
        int? categoryId,
        bool broadcastOnly,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0) pageIndex = 0;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 500) pageSize = 500;

        var query = BuildQuery(botId, categoryId, broadcastOnly, search);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }
}
