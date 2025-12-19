using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 数据同步服务（页面层复用：Home/Settings/Channels）
/// </summary>
public class DataSyncService
{
    private readonly AccountManagementService _accountManagement;
    private readonly ChannelManagementService _channelManagement;
    private readonly GroupManagementService _groupManagement;
    private readonly IChannelService _channelService;
    private readonly IGroupService _groupService;
    private readonly ILogger<DataSyncService> _logger;

    public DataSyncService(
        AccountManagementService accountManagement,
        ChannelManagementService channelManagement,
        GroupManagementService groupManagement,
        IChannelService channelService,
        IGroupService groupService,
        ILogger<DataSyncService> logger)
    {
        _accountManagement = accountManagement;
        _channelManagement = channelManagement;
        _groupManagement = groupManagement;
        _channelService = channelService;
        _groupService = groupService;
        _logger = logger;
    }

    public async Task<SyncSummary> SyncAllActiveAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _accountManagement.GetActiveAccountsAsync();
        return await SyncAccountsAsync(accounts, cancellationToken);
    }

    public async Task<SyncSummary> SyncAccountAsync(int accountId, CancellationToken cancellationToken)
    {
        var account = await _accountManagement.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"账号不存在：{accountId}");

        return await SyncAccountsAsync(new[] { account }, cancellationToken);
    }

    public async Task<SyncSummary> SyncAccountsAsync(IEnumerable<Account> accounts, CancellationToken cancellationToken)
    {
        var summary = new SyncSummary();

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 同步频道：仅同步“频道创建人=本账号”的频道
                var channelInfos = await _channelService.GetOwnedChannelsAsync(account.Id);
                var keepChannelIds = new List<int>(capacity: channelInfos.Count);

                foreach (var channelInfo in channelInfos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var channel = new TelegramPanel.Data.Entities.Channel
                    {
                        TelegramId = channelInfo.TelegramId,
                        AccessHash = channelInfo.AccessHash,
                        Title = channelInfo.Title,
                        Username = channelInfo.Username,
                        IsBroadcast = channelInfo.IsBroadcast,
                        MemberCount = channelInfo.MemberCount,
                        About = channelInfo.About,
                        CreatorAccountId = account.Id,
                        CreatedAt = channelInfo.CreatedAt
                    };

                    var saved = await _channelManagement.CreateOrUpdateChannelAsync(channel);
                    keepChannelIds.Add(saved.Id);

                    summary.TotalChannelsSynced++;
                }

                // 同步群组：保持原逻辑（仅创建的群组）
                var groups = await _groupService.GetOwnedGroupsAsync(account.Id);
                foreach (var groupInfo in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var group = new TelegramPanel.Data.Entities.Group
                    {
                        TelegramId = groupInfo.TelegramId,
                        AccessHash = groupInfo.AccessHash,
                        Title = groupInfo.Title,
                        Username = groupInfo.Username,
                        MemberCount = groupInfo.MemberCount,
                        About = null,
                        CreatorAccountId = account.Id
                    };

                    await _groupManagement.CreateOrUpdateGroupAsync(group);
                    summary.TotalGroupsSynced++;
                }

                await _accountManagement.UpdateLastSyncTimeAsync(account.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account sync failed: {AccountId}", account.Id);
                summary.AccountFailures.Add((account.Id, account.Phone, ex.Message));
            }
        }

        return summary;
    }

    public sealed class SyncSummary
    {
        public int TotalChannelsSynced { get; set; }
        public int TotalGroupsSynced { get; set; }
        public List<(int AccountId, string Phone, string Error)> AccountFailures { get; } = new();
    }
}
