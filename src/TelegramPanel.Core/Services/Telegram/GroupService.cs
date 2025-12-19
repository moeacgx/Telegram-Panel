using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TL;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 群组服务实现
/// </summary>
public class GroupService : IGroupService
{
    private readonly ITelegramClientPool _clientPool;
    private readonly AccountManagementService _accountManagement;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GroupService> _logger;

    public GroupService(
        ITelegramClientPool clientPool,
        AccountManagementService accountManagement,
        IConfiguration configuration,
        ILogger<GroupService> logger)
    {
        _clientPool = clientPool;
        _accountManagement = accountManagement;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<GroupInfo>> GetOwnedGroupsAsync(int accountId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        var ownedGroups = new List<GroupInfo>();
        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var (id, chat) in dialogs.chats)
        {
            // 处理基础群组（Chat类型，非Channel）
            // 注意：基础 Chat 类型无法直接判断创建者，需要通过 GetFullChat 获取
            if (chat is Chat basicChat && basicChat.IsActive)
            {
                try
                {
                    // 获取完整信息来判断是否为创建者
                    var fullChat = await client.Messages_GetFullChat(basicChat.id);
                    if (fullChat.full_chat is ChatFull cf && cf.participants is ChatParticipants cp)
                    {
                        // 检查当前用户是否为创建者
                        var creator = cp.participants.OfType<ChatParticipantCreator>()
                            .FirstOrDefault(p => p.user_id == client.User!.id);
                        if (creator != null)
                        {
                            ownedGroups.Add(new GroupInfo
                            {
                                TelegramId = basicChat.id,
                                Title = basicChat.title,
                                MemberCount = basicChat.participants_count,
                                CreatorAccountId = accountId,
                                SyncedAt = DateTime.UtcNow
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get full chat info for {ChatId}", basicChat.id);
                }
            }
            // 处理超级群组（Channel类型但megagroup=true, 即 !IsChannel）
            else if (chat is Channel channel && channel.IsActive && !channel.IsChannel)
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
                    ownedGroups.Add(new GroupInfo
                    {
                        TelegramId = channel.id,
                        AccessHash = channel.access_hash,
                        Title = channel.title,
                        Username = channel.MainUsername,
                        MemberCount = fullChannel.full_chat.ParticipantsCount,
                        CreatorAccountId = accountId,
                        SyncedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get full group info for {GroupId}", channel.id);
                }
            }
        }

        _logger.LogInformation("Found {Count} owned groups for account {AccountId}", ownedGroups.Count, accountId);
        return ownedGroups;
    }

    public async Task<GroupInfo?> GetGroupInfoAsync(int accountId, long groupId)
    {
        var groups = await GetOwnedGroupsAsync(accountId);
        return groups.FirstOrDefault(g => g.TelegramId == groupId);
    }

    public async Task<string> ExportJoinLinkAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await client.Messages_GetAllDialogs();

        // 基础群组（Chat）
        var basic = dialogs.chats.Values.OfType<Chat>().FirstOrDefault(c => c.IsActive && c.id == groupId);
        if (basic != null)
        {
            var invite = await client.Messages_ExportChatInvite(basic);
            var link = invite switch
            {
                ChatInviteExported e => e.link,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(link))
                throw new InvalidOperationException("无法导出群组邀请链接（可能无权限）");

            return link;
        }

        // 超级群组（Channel 但 megagroup=true，即 !IsChannel）
        var mega = dialogs.chats.Values.OfType<Channel>().FirstOrDefault(c => c.IsActive && !c.IsChannel && c.id == groupId);
        if (mega != null)
        {
            if (!string.IsNullOrWhiteSpace(mega.MainUsername))
                return $"https://t.me/{mega.MainUsername}";

            var invite = await client.Messages_ExportChatInvite(mega);
            var link = invite switch
            {
                ChatInviteExported e => e.link,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(link))
                throw new InvalidOperationException("无法导出群组邀请链接（可能无权限）");

            return link;
        }

        throw new InvalidOperationException($"群组 {groupId} not found");
    }

    public async Task<List<ChannelAdminInfo>> GetAdminsAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await client.Messages_GetAllDialogs();

        // 基础群组（Chat）
        var basic = dialogs.chats.Values.OfType<Chat>().FirstOrDefault(c => c.IsActive && c.id == groupId);
        if (basic != null)
        {
            var fullChat = await client.Messages_GetFullChat(basic.id);
            if (fullChat.full_chat is not ChatFull cf || cf.participants is not ChatParticipants cp)
                return new List<ChannelAdminInfo>();

            var list = new List<ChannelAdminInfo>();
            foreach (var p in cp.participants)
            {
                long userId;
                var isCreator = p is ChatParticipantCreator;

                if (p is ChatParticipantCreator creator)
                {
                    userId = creator.user_id;
                }
                else if (p is ChatParticipantAdmin admin)
                {
                    userId = admin.user_id;
                }
                else
                {
                    continue;
                }

                fullChat.users.TryGetValue(userId, out var u);
                list.Add(new ChannelAdminInfo(
                    UserId: userId,
                    Username: u?.MainUsername,
                    FirstName: u?.first_name,
                    LastName: u?.last_name,
                    IsCreator: isCreator,
                    Rank: null
                ));
            }

            return list
                .OrderByDescending(x => x.IsCreator)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 超级群组（Channel but !IsChannel）
        var mega = dialogs.chats.Values.OfType<Channel>().FirstOrDefault(c => c.IsActive && !c.IsChannel && c.id == groupId);
        if (mega != null)
        {
            var participants = await client.Channels_GetParticipants(mega, new ChannelParticipantsAdmins());
            var list = new List<ChannelAdminInfo>();

            foreach (var p in participants.participants)
            {
                long userId;
                string? rank = null;
                var isCreator = p is ChannelParticipantCreator;

                if (p is ChannelParticipantAdmin admin)
                {
                    userId = admin.user_id;
                    rank = string.IsNullOrWhiteSpace(admin.rank) ? null : admin.rank;
                }
                else if (p is ChannelParticipantCreator creator)
                {
                    userId = creator.user_id;
                    rank = string.IsNullOrWhiteSpace(creator.rank) ? null : creator.rank;
                }
                else
                {
                    continue;
                }

                participants.users.TryGetValue(userId, out var u);
                list.Add(new ChannelAdminInfo(
                    UserId: userId,
                    Username: u?.MainUsername,
                    FirstName: u?.first_name,
                    LastName: u?.last_name,
                    IsCreator: isCreator,
                    Rank: rank
                ));
            }

            return list
                .OrderByDescending(x => x.IsCreator)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        throw new InvalidOperationException($"群组 {groupId} not found");
    }

    private async Task<WTelegram.Client> GetOrCreateConnectedClientAsync(int accountId)
    {
        var existing = _clientPool.GetClient(accountId);
        if (existing?.User != null)
            return existing;

        var account = await _accountManagement.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"账号不存在：{accountId}");

        var apiId = ResolveApiId(account);
        var apiHash = ResolveApiHash(account);
        var sessionKey = ResolveSessionKey(account, apiHash);

        if (string.IsNullOrWhiteSpace(account.SessionPath))
            throw new InvalidOperationException("账号缺少 SessionPath，无法创建 Telegram 客户端");

        var absoluteSessionPath = Path.GetFullPath(account.SessionPath);
        if (System.IO.File.Exists(absoluteSessionPath) && LooksLikeSqliteSession(absoluteSessionPath))
        {
            var ok = await SessionDataConverter.TryConvertSqliteSessionFromJsonAsync(
                phone: account.Phone,
                apiId: account.ApiId,
                apiHash: account.ApiHash,
                sqliteSessionPath: absoluteSessionPath,
                logger: _logger
            );

            if (!ok)
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件为 SQLite 格式：{account.SessionPath}，本项目无法直接复用。" +
                    "已尝试从本地 json（例如 sessions/<手机号>.json 或 session数据/<手机号>/*.json）读取 session_string 自动转换但失败；" +
                    "请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再操作。");
            }
        }

        await _clientPool.RemoveClientAsync(accountId);
        var client = await _clientPool.GetOrCreateClientAsync(accountId, apiId, apiHash, account.SessionPath, sessionKey, account.Phone, account.UserId);

        try
        {
            await client.ConnectAsync();
            if (client.User == null && (client.UserId != 0 || account.UserId != 0))
                await client.LoginUserIfNeeded(reloginOnFailedResume: false);
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

    private int ResolveApiId(TelegramPanel.Data.Entities.Account account)
    {
        if (int.TryParse(_configuration["Telegram:ApiId"], out var globalApiId) && globalApiId > 0)
            return globalApiId;
        if (account.ApiId > 0)
            return account.ApiId;
        throw new InvalidOperationException("未配置全局 ApiId，且账号缺少 ApiId");
    }

    private string ResolveApiHash(TelegramPanel.Data.Entities.Account account)
    {
        var global = _configuration["Telegram:ApiHash"];
        if (!string.IsNullOrWhiteSpace(global))
            return global.Trim();
        if (!string.IsNullOrWhiteSpace(account.ApiHash))
            return account.ApiHash.Trim();
        throw new InvalidOperationException("未配置全局 ApiHash，且账号缺少 ApiHash");
    }

    private static string ResolveSessionKey(TelegramPanel.Data.Entities.Account account, string apiHash)
    {
        return !string.IsNullOrWhiteSpace(account.ApiHash) ? account.ApiHash.Trim() : apiHash.Trim();
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
        return SessionDataConverter.LooksLikeSqliteSession(filePath);
    }
}
