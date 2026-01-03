using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public class BotChannelCategoryRepository : Repository<BotChannelCategory>, IBotChannelCategoryRepository
{
    public BotChannelCategoryRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<BotChannelCategory>> GetForBotAsync(int botId)
    {
        if (botId <= 0)
            return Array.Empty<BotChannelCategory>();

        // 分类已全局化：按“该 Bot 可管理的频道里出现过的分类”返回（更符合旧模块预期）
        var categoryIds = await _context.BotChannels
            .AsNoTracking()
            .Where(ch => ch.Members.Any(m => m.BotId == botId) && ch.CategoryId != null)
            .Select(ch => ch.CategoryId!.Value)
            .Distinct()
            .ToListAsync();

        if (categoryIds.Count == 0)
            return Array.Empty<BotChannelCategory>();

        return await _dbSet
            .AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<IEnumerable<BotChannelCategory>> GetAllOrderedAsync()
    {
        return await _dbSet
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<BotChannelCategory?> GetByNameAsync(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return await _dbSet.FirstOrDefaultAsync(x => x.Name == name);
    }
}
