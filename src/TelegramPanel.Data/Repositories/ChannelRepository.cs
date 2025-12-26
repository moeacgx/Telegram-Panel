using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 频道仓储实现
/// </summary>
public class ChannelRepository : Repository<Channel>, IChannelRepository
{
    public ChannelRepository(AppDbContext context) : base(context)
    {
    }

    private IQueryable<Channel> BuildForViewQuery(int creatorAccountId, string? filterType, string? search)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .AsQueryable();

        // 列表只展示“系统创建”的频道（有 CreatorAccountId）
        if (creatorAccountId <= 0)
            query = query.Where(c => c.CreatorAccountId != null);
        else
            query = query.Where(c => c.CreatorAccountId == creatorAccountId);

        filterType = (filterType ?? "all").Trim().ToLowerInvariant();
        if (filterType == "public")
            query = query.Where(c => c.Username != null && c.Username != "");
        else if (filterType == "private")
            query = query.Where(c => c.Username == null || c.Username == "");

        search = (search ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search}%";
            query = query.Where(c =>
                EF.Functions.Like(c.Title, like)
                || (c.Username != null && EF.Functions.Like(c.Username, like)));
        }

        return query.OrderByDescending(c => c.SyncedAt);
    }

    public override async Task<Channel?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public override async Task<IEnumerable<Channel>> GetAllAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<Channel?> GetByTelegramIdAsync(long telegramId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .FirstOrDefaultAsync(c => c.TelegramId == telegramId);
    }

    public async Task<IEnumerable<Channel>> GetCreatedAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => c.CreatorAccountId != null)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetByCreatorAccountAsync(int accountId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => c.CreatorAccountId == accountId)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetForAccountAsync(int accountId, bool includeNonCreator)
    {
        var links = _context.Set<AccountChannel>()
            .Where(x => x.AccountId == accountId && (includeNonCreator || x.IsCreator));

        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => links.Any(x => x.ChannelId == c.Id))
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetByGroupAsync(int groupId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => c.GroupId == groupId)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetBroadcastChannelsAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Where(c => c.IsBroadcast)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<(IReadOnlyList<Channel> Items, int TotalCount)> QueryForViewPagedAsync(
        int creatorAccountId,
        string? filterType,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0) pageIndex = 0;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 500) pageSize = 500;

        var query = BuildForViewQuery(creatorAccountId, filterType, search);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }
}
