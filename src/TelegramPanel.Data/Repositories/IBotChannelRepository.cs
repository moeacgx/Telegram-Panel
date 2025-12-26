using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

public interface IBotChannelRepository : IRepository<BotChannel>
{
    Task<BotChannel?> GetByTelegramIdAsync(int botId, long telegramId);
    Task<IEnumerable<BotChannel>> GetForBotAsync(int botId, int? categoryId = null);

    Task<(IReadOnlyList<BotChannel> Items, int TotalCount)> QueryPagedAsync(
        int botId,
        int? categoryId,
        bool broadcastOnly,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);
}
