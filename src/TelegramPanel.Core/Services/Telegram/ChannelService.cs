using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 频道服务实现
/// </summary>
public class ChannelService : IChannelService
{
    private readonly ITelegramClientPool _clientPool;
    private readonly AccountManagementService _accountManagement;
    private readonly ILogger<ChannelService> _logger;

    public ChannelService(
        ITelegramClientPool clientPool,
        AccountManagementService accountManagement,
        ILogger<ChannelService> logger)
    {
        _clientPool = clientPool;
        _accountManagement = accountManagement;
        _logger = logger;
    }

    public async Task<List<ChannelInfo>> GetOwnedChannelsAsync(int accountId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        var ownedChannels = new List<ChannelInfo>();
        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var (id, chat) in dialogs.chats)
        {
            // 只处理频道（Channel类型且IsChannel=true表示广播频道）
            if (chat is Channel channel && channel.IsActive)
            {
                try
                {
                    // 通过获取管理员列表来检查当前用户是否为创建者
                    var participants = await client.Channels_GetParticipants(channel, new ChannelParticipantsAdmins());
                    var isCreator = participants.participants
                        .OfType<ChannelParticipantCreator>()
                        .Any(p => p.user_id == client.User!.id);

                    if (!isCreator) continue;

                    var fullChannel = await client.Channels_GetFullChannel(channel);

                    ownedChannels.Add(new ChannelInfo
                    {
                        TelegramId = channel.id,
                        AccessHash = channel.access_hash,
                        Title = channel.title,
                        Username = channel.MainUsername,
                        IsBroadcast = channel.IsChannel,
                        MemberCount = fullChannel.full_chat.ParticipantsCount,
                        About = (fullChannel.full_chat as ChannelFull)?.about,
                        CreatorAccountId = accountId,
                        CreatedAt = channel.IsChannel ? null : channel.date,
                        SyncedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get channel info for {ChannelId}", channel.id);
                }
            }
        }

        _logger.LogInformation("Found {Count} owned channels for account {AccountId}", ownedChannels.Count, accountId);
        return ownedChannels;
    }

    public async Task<ChannelInfo> CreateChannelAsync(int accountId, string title, string about, bool isPublic = false)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        _logger.LogInformation("Creating channel '{Title}' for account {AccountId}", title, accountId);

        var updates = await client.Channels_CreateChannel(
            title: title,
            about: about,
            broadcast: true  // true=频道, false=超级群组
        );

        var channel = updates.Chats.Values.OfType<Channel>().FirstOrDefault()
            ?? throw new InvalidOperationException("Channel creation failed");

        return new ChannelInfo
        {
            TelegramId = channel.id,
            AccessHash = channel.access_hash,
            Title = channel.title,
            IsBroadcast = true,
            CreatorAccountId = accountId,
            SyncedAt = DateTime.UtcNow
        };
    }

    public async Task<bool> SetChannelVisibilityAsync(int accountId, long channelId, bool isPublic, string? username = null)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        var channel = await GetChannelByIdAsync(client, channelId);
        if (channel == null) return false;

        if (isPublic)
        {
            if (string.IsNullOrEmpty(username))
                throw new ArgumentException("Username is required for public channels");

            // 检查用户名是否可用
            var available = await client.Channels_CheckUsername(channel, username);
            if (!available)
                throw new InvalidOperationException($"Username '{username}' is not available");

            await client.Channels_UpdateUsername(channel, username);
        }
        else
        {
            // 移除用户名使频道变为私密
            await client.Channels_UpdateUsername(channel, string.Empty);
        }

        return true;
    }

    public async Task<InviteResult> InviteUserAsync(int accountId, long channelId, string username)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        try
        {
            var channel = await GetChannelByIdAsync(client, channelId)
                ?? throw new InvalidOperationException($"Channel {channelId} not found");

            var resolved = await client.Contacts_ResolveUsername(username);
            await client.AddChatUser(channel, resolved.User);

            _logger.LogInformation("Successfully invited @{Username} to channel {ChannelId}", username, channelId);
            return new InviteResult(username, true);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Failed to invite @{Username}: {Error}", username, ex.Message);
            return new InviteResult(username, false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error inviting @{Username}", username);
            return new InviteResult(username, false, ex.Message);
        }
    }

    public async Task<List<InviteResult>> BatchInviteUsersAsync(int accountId, long channelId, List<string> usernames, int delayMs = 2000)
    {
        var results = new List<InviteResult>();

        foreach (var username in usernames)
        {
            var result = await InviteUserAsync(accountId, channelId, username);
            results.Add(result);

            // 防风控延迟
            if (usernames.IndexOf(username) < usernames.Count - 1)
            {
                await Task.Delay(delayMs + Random.Shared.Next(500, 1500)); // 添加随机延迟
            }
        }

        var successCount = results.Count(r => r.Success);
        _logger.LogInformation("Batch invite completed: {Success}/{Total} successful", successCount, results.Count);

        return results;
    }

    public async Task<bool> SetAdminAsync(int accountId, long channelId, string username, Interfaces.AdminRights rights, string title = "Admin")
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        var channel = await GetChannelByIdAsync(client, channelId)
            ?? throw new InvalidOperationException($"Channel {channelId} not found");

        var resolved = await client.Contacts_ResolveUsername(username);

        var chatAdminRights = ConvertAdminRights(rights);

        await client.Channels_EditAdmin(channel, resolved.User, chatAdminRights, title);

        _logger.LogInformation("Set @{Username} as admin in channel {ChannelId}", username, channelId);
        return true;
    }

    public async Task<List<SetAdminResult>> BatchSetAdminsAsync(int accountId, long channelId, List<AdminRequest> requests)
    {
        var results = new List<SetAdminResult>();

        foreach (var request in requests)
        {
            try
            {
                await SetAdminAsync(accountId, channelId, request.Username, request.Rights, request.Title);
                results.Add(new SetAdminResult(request.Username, true));
            }
            catch (Exception ex)
            {
                results.Add(new SetAdminResult(request.Username, false, ex.Message));
            }

            // 延迟
            if (requests.IndexOf(request) < requests.Count - 1)
            {
                await Task.Delay(1000 + Random.Shared.Next(500, 1000));
            }
        }

        return results;
    }

    public async Task<bool> SetForwardingAllowedAsync(int accountId, long channelId, bool allowed)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var channel = await GetChannelByIdAsync(client, channelId)
            ?? throw new InvalidOperationException($"Channel {channelId} not found");

        // messages.toggleNoForwards: true 表示“保护内容”（禁止转发/保存）
        await client.Messages_ToggleNoForwards(channel, !allowed);
        return true;
    }

    #region Private Methods

    private async Task<Channel?> GetChannelByIdAsync(Client client, long channelId)
    {
        var dialogs = await client.Messages_GetAllDialogs();
        return dialogs.chats.Values.OfType<Channel>().FirstOrDefault(c => c.id == channelId);
    }

    private async Task<Client> GetOrCreateConnectedClientAsync(int accountId)
    {
        var existing = _clientPool.GetClient(accountId);
        if (existing?.User != null)
            return existing;

        var account = await _accountManagement.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"账号不存在：{accountId}");

        if (account.ApiId <= 0 || string.IsNullOrWhiteSpace(account.ApiHash))
            throw new InvalidOperationException("账号缺少 ApiId/ApiHash，无法创建 Telegram 客户端");

        if (string.IsNullOrWhiteSpace(account.SessionPath))
            throw new InvalidOperationException("账号缺少 SessionPath，无法创建 Telegram 客户端");

        var absoluteSessionPath = Path.GetFullPath(account.SessionPath);
        if (File.Exists(absoluteSessionPath) && LooksLikeSqliteSession(absoluteSessionPath))
            throw new InvalidOperationException($"该账号的 Session 文件为 SQLite 格式（常见于 Telethon/Pyrogram/Telegram Desktop）：{account.SessionPath}。本项目基于 WTelegramClient，无法直接复用该格式；请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再操作。");

        await _clientPool.RemoveClientAsync(accountId);
        var client = await _clientPool.GetOrCreateClientAsync(accountId, account.ApiId, account.ApiHash, account.SessionPath);

        try
        {
            await client.ConnectAsync();
        }
        catch (Exception ex)
        {
            if (LooksLikeSessionApiMismatchOrCorrupted(ex))
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件无法解析（通常是 ApiId/ApiHash 与生成 session 时不一致，或 session 文件已损坏）。" +
                    "请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再操作。",
                    ex);
            }

            throw new InvalidOperationException($"Telegram 会话加载失败：{ex.Message}", ex);
        }

        if (client.User == null)
            throw new InvalidOperationException("账号未登录或 session 已失效，请重新登录生成新的 session");

        return client;
    }

    private static bool LooksLikeSessionApiMismatchOrCorrupted(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Use the correct api_hash/id/key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSqliteSession(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[16];
            var read = fs.Read(header);
            if (read < 15) return false;
            var text = System.Text.Encoding.ASCII.GetString(header[..15]);
            return string.Equals(text, "SQLite format 3", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static ChatAdminRights ConvertAdminRights(Interfaces.AdminRights rights)
    {
        var flags = ChatAdminRights.Flags.other;

        if (rights.HasFlag(Interfaces.AdminRights.ChangeInfo))
            flags |= ChatAdminRights.Flags.change_info;
        if (rights.HasFlag(Interfaces.AdminRights.PostMessages))
            flags |= ChatAdminRights.Flags.post_messages;
        if (rights.HasFlag(Interfaces.AdminRights.EditMessages))
            flags |= ChatAdminRights.Flags.edit_messages;
        if (rights.HasFlag(Interfaces.AdminRights.DeleteMessages))
            flags |= ChatAdminRights.Flags.delete_messages;
        if (rights.HasFlag(Interfaces.AdminRights.BanUsers))
            flags |= ChatAdminRights.Flags.ban_users;
        if (rights.HasFlag(Interfaces.AdminRights.InviteUsers))
            flags |= ChatAdminRights.Flags.invite_users;
        if (rights.HasFlag(Interfaces.AdminRights.PinMessages))
            flags |= ChatAdminRights.Flags.pin_messages;
        if (rights.HasFlag(Interfaces.AdminRights.ManageCall))
            flags |= ChatAdminRights.Flags.manage_call;
        if (rights.HasFlag(Interfaces.AdminRights.AddAdmins))
            flags |= ChatAdminRights.Flags.add_admins;
        if (rights.HasFlag(Interfaces.AdminRights.Anonymous))
            flags |= ChatAdminRights.Flags.anonymous;
        if (rights.HasFlag(Interfaces.AdminRights.ManageTopics))
            flags |= ChatAdminRights.Flags.manage_topics;

        return new ChatAdminRights { flags = flags };
    }

    #endregion
}
