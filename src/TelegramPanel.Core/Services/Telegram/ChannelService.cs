using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChannelService> _logger;

    public ChannelService(
        ITelegramClientPool clientPool,
        AccountManagementService accountManagement,
        IConfiguration configuration,
        ILogger<ChannelService> logger)
    {
        _clientPool = clientPool;
        _accountManagement = accountManagement;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ChannelInfo>> GetOwnedChannelsAsync(int accountId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        var channels = new List<ChannelInfo>();
        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var (_, chat) in dialogs.chats)
        {
            if (chat is not Channel channel || !channel.IsActive)
                continue;

            try
            {
                var isCreator =
                    ReadBool(channel, "creator", "Creator", "is_creator", "IsCreator")
                    || ReadFlagsHas(channel, flagName: "creator", memberNames: new[] { "flags", "Flags" });
                if (!isCreator)
                    continue;

                var memberCount = ReadInt(channel, 0, "participants_count", "ParticipantsCount", "participantsCount", "memberCount", "MemberCount");
                string? about = null;
                try
                {
                    var fullChannel = await client.Channels_GetFullChannel(channel);
                    memberCount = fullChannel.full_chat.ParticipantsCount;
                    about = (fullChannel.full_chat as ChannelFull)?.about;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get full channel info for {ChannelId}", channel.id);
                }

                channels.Add(new ChannelInfo
                {
                    TelegramId = channel.id,
                    AccessHash = channel.access_hash,
                    Title = channel.title,
                    Username = channel.MainUsername,
                    IsBroadcast = channel.IsChannel,
                    MemberCount = memberCount,
                    About = about,
                    CreatorAccountId = accountId,
                    IsCreator = true,
                    IsAdmin = true,
                    CreatedAt = channel.date,
                    SyncedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get owned channel info for {ChannelId}", channel.id);
            }
        }

        _logger.LogInformation("Found {Count} owned channels for account {AccountId}", channels.Count, accountId);
        return channels;
    }

    public async Task<List<ChannelInfo>> GetAdminedChannelsAsync(int accountId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        var channels = new List<ChannelInfo>();
        var dialogs = await client.Messages_GetAllDialogs();

        foreach (var (_, chat) in dialogs.chats)
        {
            if (chat is not Channel channel || !channel.IsActive)
                continue;

            try
            {
                // 先用 dialogs 里自带的权限信息过滤，避免对“非管理员频道”调用管理员接口导致 400 CHAT_ADMIN_REQUIRED
                // （也能大幅提升同步速度）
                var isCreator =
                    ReadBool(channel, "creator", "Creator", "is_creator", "IsCreator")
                    || ReadFlagsHas(channel, flagName: "creator", memberNames: new[] { "flags", "Flags" });
                var adminRights = ReadObject(channel, "admin_rights", "AdminRights", "adminRights");
                var isAdmin = isCreator || adminRights != null;
                if (!isAdmin)
                    continue;

                var memberCount = ReadInt(channel, 0, "participants_count", "ParticipantsCount", "participantsCount", "memberCount", "MemberCount");
                string? about = null;
                try
                {
                    var fullChannel = await client.Channels_GetFullChannel(channel);
                    memberCount = fullChannel.full_chat.ParticipantsCount;
                    about = (fullChannel.full_chat as ChannelFull)?.about;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get full channel info for {ChannelId}", channel.id);
                }

                channels.Add(new ChannelInfo
                {
                    TelegramId = channel.id,
                    AccessHash = channel.access_hash,
                    Title = channel.title,
                    Username = channel.MainUsername,
                    IsBroadcast = channel.IsChannel,
                    MemberCount = memberCount,
                    About = about,
                    CreatorAccountId = isCreator ? accountId : null,
                    IsCreator = isCreator,
                    IsAdmin = true,
                    CreatedAt = channel.date,
                    SyncedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get channel info for {ChannelId}", channel.id);
            }
        }

        _logger.LogInformation("Found {Count} admined channels for account {AccountId}", channels.Count, accountId);
        return channels;
    }

    private static bool ReadBool(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value) && value is bool b)
                return b;
        }
        return false;
    }

    private static bool ReadFlagsHas(object obj, string flagName, string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            if (!TryReadMember(obj, memberName, out var value) || value == null)
                continue;

            // 1) flags 直接就是枚举（最常见）
            if (value is Enum enumValue)
            {
                var flagEnum = TryGetEnumFlag(enumValue.GetType(), flagName);
                if (flagEnum != null)
                    return enumValue.HasFlag(flagEnum);
                continue;
            }

            // 2) flags 是 int/long 等基础类型（少见），尝试用嵌套 Flags 枚举来解码
            if (value is int intFlags)
                return ReadNumericFlagsHas(obj, flagName, intFlags);
            if (value is long longFlags)
                return ReadNumericFlagsHas(obj, flagName, longFlags);
        }

        return false;
    }

    private static bool ReadNumericFlagsHas(object obj, string flagName, long flagsValue)
    {
        var flagsType = obj.GetType().GetNestedType("Flags");
        if (flagsType == null || !flagsType.IsEnum)
            return false;

        var flagEnum = TryGetEnumFlag(flagsType, flagName);
        if (flagEnum == null)
            return false;

        var flagValue = Convert.ToInt64(flagEnum);
        return (flagsValue & flagValue) == flagValue;
    }

    private static Enum? TryGetEnumFlag(Type enumType, string flagName)
    {
        var names = Enum.GetNames(enumType);
        var match = names.FirstOrDefault(n => string.Equals(n, flagName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return null;

        return (Enum)Enum.Parse(enumType, match);
    }

    private static int ReadInt(object obj, int fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value))
            {
                if (value is int i) return i;
                if (value is long l) return unchecked((int)l);
                if (value is short s) return s;
                if (value is byte by) return by;
            }
        }
        return fallback;
    }

    private static object? ReadObject(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value) && value != null)
                return value;
        }
        return null;
    }

    private static bool TryReadMember(object obj, string name, out object? value)
    {
        var type = obj.GetType();

        var prop = type.GetProperty(name);
        if (prop != null && prop.CanRead)
        {
            value = prop.GetValue(obj);
            return true;
        }

        var field = type.GetField(name);
        if (field != null)
        {
            value = field.GetValue(obj);
            return true;
        }

        value = null;
        return false;
    }

    public async Task<ChannelInfo> CreateChannelAsync(int accountId, string title, string about, bool isPublic = false)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        _logger.LogInformation("Creating channel '{Title}' for account {AccountId}", title, accountId);

        UpdatesBase updates;
        try
        {
            updates = await client.Channels_CreateChannel(
                title: title,
                about: about,
                broadcast: true  // true=频道, false=超级群组
            );
        }
        catch (RpcException ex) when (ex.Code == 420 && string.Equals(ex.Message, "FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Telegram 返回 FROZEN_METHOD_INVALID：当前 ApiId/ApiHash 或账号被 Telegram 限制调用创建频道接口。" +
                "建议：在【系统设置】更换全局 ApiId/ApiHash（推荐使用你自己在 my.telegram.org 申请的），" +
                "并重新导入/重新登录生成 session 后再试。",
                ex);
        }

        var channel = updates.Chats.Values.OfType<Channel>().FirstOrDefault()
            ?? throw new InvalidOperationException("Channel creation failed");

        return new ChannelInfo
        {
            TelegramId = channel.id,
            AccessHash = channel.access_hash,
            Title = channel.title,
            IsBroadcast = true,
            CreatorAccountId = accountId,
            IsCreator = true,
            IsAdmin = true,
            CreatedAt = channel.date,
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

        // 权限校验：Telegram 可能会静默“削减”你请求的权限（典型：执行账号本身没有 add_admins/promote 权限）
        try
        {
            var granted = await TryGetGrantedAdminRightsAsync(client, channel, resolved.User.id);
            if (granted.HasValue)
            {
                var missing = rights & ~granted.Value;
                if (missing != Interfaces.AdminRights.None)
                {
                    throw new InvalidOperationException(
                        $"已设置管理员，但部分权限未生效：{FormatAdminRights(missing)}。" +
                        "请确认：执行账号是该频道创建者，或执行账号在频道内拥有“添加管理员”权限。");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 不阻塞主流程：有些频道无法拉取管理员列表（例如权限不足/风控），只记 debug 方便排查
            _logger.LogDebug(ex, "Verify admin rights failed for channel {ChannelId}", channelId);
        }

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

    public async Task<string> ExportJoinLinkAsync(int accountId, long channelId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var channel = await GetChannelByIdAsync(client, channelId)
            ?? throw new InvalidOperationException($"Channel {channelId} not found");

        if (!string.IsNullOrWhiteSpace(channel.MainUsername))
            return $"https://t.me/{channel.MainUsername}";

        var invite = await client.Messages_ExportChatInvite(channel);
        var link = invite switch
        {
            ChatInviteExported e => e.link,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(link))
            throw new InvalidOperationException("无法导出邀请链接（可能无权限）");

        return link;
    }

    public async Task<List<ChannelAdminInfo>> GetAdminsAsync(int accountId, long channelId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var channel = await GetChannelByIdAsync(client, channelId)
            ?? throw new InvalidOperationException($"Channel {channelId} not found");

        var participants = await client.Channels_GetParticipants(channel, new ChannelParticipantsAdmins());

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

    public async Task<bool> UpdateChannelInfoAsync(int accountId, long channelId, string title, string? about)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var channel = await GetChannelByIdAsync(client, channelId)
            ?? throw new InvalidOperationException($"Channel {channelId} not found");

        title = (title ?? string.Empty).Trim();
        about = string.IsNullOrWhiteSpace(about) ? null : about.Trim();

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("频道标题不能为空", nameof(title));

        await client.Channels_EditTitle(channel, title);

        if (about != null)
            await client.Messages_EditChatAbout(channel, about);

        return true;
    }

    public async Task<bool> KickUserAsync(int accountId, long channelId, string username, bool permanentBan = false)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var channel = await GetChannelByIdAsync(client, channelId)
            ?? throw new InvalidOperationException($"Channel {channelId} not found");

        username = (username ?? string.Empty).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("请输入要踢出的用户名", nameof(username));

        var resolved = await client.Contacts_ResolveUsername(username);
        var user = resolved.User;
        if (user.access_hash == 0)
            throw new InvalidOperationException("无法获取用户 access_hash，无法执行踢人");

        var peer = new InputPeerUser(user.id, user.access_hash);

        // 非永久：用短时间 ban 达到“踢出但可再加入”的效果
        var until = permanentBan ? DateTime.UtcNow.AddYears(100) : DateTime.UtcNow.AddSeconds(60);
        var rights = new ChatBannedRights
        {
            flags = ChatBannedRights.Flags.view_messages,
            until_date = until
        };

        await client.Channels_EditBanned(channel, peer, rights);
        return true;
    }

    #region Private Methods

    private static IReadOnlyList<long> GetCandidateChannelIds(long channelId)
    {
        // 兼容 Bot API 的 chat_id 形式：-100xxxxxxxxxx
        // 不同库对 channel_id 的表示可能存在 unsigned/signed 差异，这里同时尝试多个候选值匹配 dialogs 里的 Channel.id。
        var list = new List<long>(capacity: 3) { channelId };

        if (channelId <= -1000000000000L)
        {
            // Bot chat_id = -1000000000000 - channel_id
            var raw = (-channelId) - 1000000000000L;
            list.Add(raw);
            list.Add(unchecked((int)raw));
        }

        return list.Distinct().ToList();
    }

    private async Task<Channel?> GetChannelByIdAsync(Client client, long channelId)
    {
        var candidates = GetCandidateChannelIds(channelId);
        var candidateSet = candidates.Count == 1 ? null : candidates.ToHashSet();

        var dialogs = await client.Messages_GetAllDialogs();
        return dialogs.chats.Values
            .OfType<Channel>()
            .FirstOrDefault(c => candidateSet?.Contains(c.id) ?? c.id == channelId);
    }

    private async Task<Client> GetOrCreateConnectedClientAsync(int accountId)
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
        if (File.Exists(absoluteSessionPath) && LooksLikeSqliteSession(absoluteSessionPath))
        {
            var converted = await SessionDataConverter.TryConvertSqliteSessionFromJsonAsync(
                phone: account.Phone,
                apiId: account.ApiId,
                apiHash: account.ApiHash,
                sqliteSessionPath: absoluteSessionPath,
                logger: _logger
            );

            if (!converted.Ok)
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件为 SQLite 格式：{account.SessionPath}，本项目无法直接复用。" +
                    "已尝试从本地 json（例如 sessions/<手机号>.json 或 session数据/<手机号>/*.json）读取 session_string 自动转换但失败；" +
                    $"原因：{converted.Reason}。请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再操作。");
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
        // session 文件加密 key（session_key）必须与生成该 session 的 key 一致
        // 这里优先使用账号自带 ApiHash（导入时保存的），否则退回 apiHash（全局）
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

    private static ChatAdminRights ConvertAdminRights(Interfaces.AdminRights rights)
    {
        // 注意：不要默认带上 unknown/other flag。某些情况下会导致服务端“净化/削减”你请求的权限。
        ChatAdminRights.Flags flags = 0;

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

    private static Interfaces.AdminRights ConvertAdminRights(ChatAdminRights rights)
    {
        var r = Interfaces.AdminRights.None;
        var flags = rights.flags;

        if (flags.HasFlag(ChatAdminRights.Flags.change_info))
            r |= Interfaces.AdminRights.ChangeInfo;
        if (flags.HasFlag(ChatAdminRights.Flags.post_messages))
            r |= Interfaces.AdminRights.PostMessages;
        if (flags.HasFlag(ChatAdminRights.Flags.edit_messages))
            r |= Interfaces.AdminRights.EditMessages;
        if (flags.HasFlag(ChatAdminRights.Flags.delete_messages))
            r |= Interfaces.AdminRights.DeleteMessages;
        if (flags.HasFlag(ChatAdminRights.Flags.ban_users))
            r |= Interfaces.AdminRights.BanUsers;
        if (flags.HasFlag(ChatAdminRights.Flags.invite_users))
            r |= Interfaces.AdminRights.InviteUsers;
        if (flags.HasFlag(ChatAdminRights.Flags.pin_messages))
            r |= Interfaces.AdminRights.PinMessages;
        if (flags.HasFlag(ChatAdminRights.Flags.manage_call))
            r |= Interfaces.AdminRights.ManageCall;
        if (flags.HasFlag(ChatAdminRights.Flags.add_admins))
            r |= Interfaces.AdminRights.AddAdmins;
        if (flags.HasFlag(ChatAdminRights.Flags.anonymous))
            r |= Interfaces.AdminRights.Anonymous;
        if (flags.HasFlag(ChatAdminRights.Flags.manage_topics))
            r |= Interfaces.AdminRights.ManageTopics;

        return r;
    }

    private async Task<Interfaces.AdminRights?> TryGetGrantedAdminRightsAsync(Client client, Channel channel, long userId)
    {
        // 取管理员列表（通常很小）。若频道管理员非常多，仍可能只返回一部分；这种场景下校验只作为“尽力而为”。
        var participants = await client.Channels_GetParticipants(channel, new ChannelParticipantsAdmins());
        foreach (var p in participants.participants)
        {
            if (p is ChannelParticipantCreator creator && creator.user_id == userId)
            {
                // 创建者权限视为 FullAdmin（至少包含本系统可表达的权限）
                return Interfaces.AdminRights.FullAdmin | Interfaces.AdminRights.Anonymous | Interfaces.AdminRights.ManageTopics;
            }

            if (p is ChannelParticipantAdmin admin && admin.user_id == userId)
            {
                if (admin.admin_rights == null)
                    return Interfaces.AdminRights.None;
                return ConvertAdminRights(admin.admin_rights);
            }
        }

        return null;
    }

    private static string FormatAdminRights(Interfaces.AdminRights rights)
    {
        if (rights == Interfaces.AdminRights.None)
            return "无";

        var parts = new List<string>();
        void Add(Interfaces.AdminRights r, string label)
        {
            if (rights.HasFlag(r))
                parts.Add(label);
        }

        Add(Interfaces.AdminRights.ChangeInfo, "修改信息");
        Add(Interfaces.AdminRights.PostMessages, "发消息");
        Add(Interfaces.AdminRights.EditMessages, "编辑消息");
        Add(Interfaces.AdminRights.DeleteMessages, "删除消息");
        Add(Interfaces.AdminRights.BanUsers, "封禁用户");
        Add(Interfaces.AdminRights.InviteUsers, "邀请用户");
        Add(Interfaces.AdminRights.PinMessages, "置顶消息");
        Add(Interfaces.AdminRights.ManageCall, "管理语音/直播");
        Add(Interfaces.AdminRights.AddAdmins, "添加管理员");
        Add(Interfaces.AdminRights.Anonymous, "匿名管理员");
        Add(Interfaces.AdminRights.ManageTopics, "管理话题");

        return parts.Count == 0 ? rights.ToString() : string.Join("、", parts);
    }

    #endregion
}
