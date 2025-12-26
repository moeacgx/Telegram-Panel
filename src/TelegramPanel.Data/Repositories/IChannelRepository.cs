using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 频道仓储接口
/// </summary>
public interface IChannelRepository : IRepository<Channel>
{
    Task<Channel?> GetByTelegramIdAsync(long telegramId);
    Task<IEnumerable<Channel>> GetCreatedAsync();
    Task<IEnumerable<Channel>> GetByCreatorAccountAsync(int accountId);
    Task<IEnumerable<Channel>> GetForAccountAsync(int accountId, bool includeNonCreator);
    Task<IEnumerable<Channel>> GetByGroupAsync(int groupId);
    Task<IEnumerable<Channel>> GetBroadcastChannelsAsync();

    Task<(IReadOnlyList<Channel> Items, int TotalCount)> QueryForViewPagedAsync(
        int creatorAccountId,
        string? filterType,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);
}
