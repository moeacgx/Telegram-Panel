using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Proxy;
using TL;
using WTelegram;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// 群组服务实现
/// </summary>
public class GroupService : IGroupService
{
    private readonly ITelegramClientPool _clientPool;
    private readonly AccountManagementService _accountManagement;
    private readonly IConfiguration _configuration;
    private readonly IAccountProxyResolver _proxyResolver;
    private readonly ILogger<GroupService> _logger;
    private readonly ISessionPathResolver _sessionPathResolver;

    public GroupService(
        ITelegramClientPool clientPool,
        AccountManagementService accountManagement,
        IConfiguration configuration,
        IAccountProxyResolver proxyResolver,
        ILogger<GroupService> logger,
        ISessionPathResolver sessionPathResolver)
    {
        _clientPool = clientPool;
        _accountManagement = accountManagement;
        _configuration = configuration;
        _proxyResolver = proxyResolver;
        _logger = logger;
        _sessionPathResolver = sessionPathResolver;
    }

    public async Task<List<GroupInfo>> GetOwnedGroupsAsync(int accountId)
    {
        var groups = await GetVisibleGroupsAsync(accountId);
        return groups.Where(x => x.IsCreator).ToList();
    }

    public async Task<GroupInfo> CreateGroupAsync(int accountId, string title, string about, bool isPublic = false, string? username = null)
    {
        if (isPublic)
        {
            username = username?.Trim().TrimStart('@');
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("公开群组需要设置用户名");
        }

        var group = await CreatePrivateGroupAsync(accountId, title, about);
        if (!isPublic || string.IsNullOrWhiteSpace(username))
            return group;

        if (!await SetGroupVisibilityAsync(accountId, group.TelegramId, true, username))
            throw new InvalidOperationException("群组已创建，但公开用户名未设置成功");
        return group with { Username = username };
    }

    public async Task<GroupInfo> CreatePrivateGroupAsync(int accountId, string title, string about)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);

        _logger.LogInformation("Creating group '{Title}' for account {AccountId}", title, accountId);

        UpdatesBase updates;
        try
        {
            updates = await client.Channels_CreateChannel(
                title: title,
                about: about,
                broadcast: false,
                megagroup: true
            );
        }
        catch (RpcException ex) when (ex.Code == 420 && string.Equals(ex.Message, "FROZEN_METHOD_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Telegram 返回 FROZEN_METHOD_INVALID：当前 ApiId/ApiHash 或账号被 Telegram 限制调用创建群组接口。" +
                "建议：在【系统设置】更换全局 ApiId/ApiHash（推荐使用你自己在 my.telegram.org 申请的），" +
                "并重新导入/重新登录生成 session 后再试。",
                ex);
        }

        var group = updates.Chats.Values.OfType<Channel>().FirstOrDefault(c => !c.IsChannel)
            ?? throw new InvalidOperationException("群组创建失败");

        return new GroupInfo
        {
            TelegramId = group.id,
            AccessHash = group.access_hash,
            Title = group.title,
            Username = group.MainUsername,
            MemberCount = 0,
            About = about,
            CreatorAccountId = accountId,
            IsCreator = true,
            IsAdmin = true,
            CreatedAt = group.date,
            SyncedAt = DateTime.UtcNow
        };
    }

    public async Task<List<GroupInfo>> GetVisibleGroupsAsync(int accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);

        var groups = new List<GroupInfo>();
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            cancellationToken,
            resetClientOnTimeout: true);

        foreach (var (_, chat) in dialogs.chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 基础群组（Chat 类型，非 Channel）
            if (chat is Chat basicChat && basicChat.IsActive)
            {
                var memberCount = (int)basicChat.participants_count;
                var about = ReadString(basicChat, null, "about", "About");
                var username = ReadString(basicChat, null, "username", "Username", "MainUsername");
                var isCreator = false;
                var isAdmin = false;

                try
                {
                    var fullChat = await ExecuteTelegramRequestAsync(
                        accountId,
                        $"拉取基础群详情({basicChat.id})",
                        () => client.Messages_GetFullChat(basicChat.id),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    if (fullChat.full_chat is ChatFull cf)
                    {
                        about = ReadString(cf, about, "about", "About");
                        memberCount = Math.Max(memberCount, ReadInt(cf, memberCount, "participants_count", "ParticipantsCount", "participantsCount"));

                        if (cf.participants is ChatParticipants cp)
                        {
                            memberCount = Math.Max(memberCount, cp.participants.Count());
                            var self = cp.participants.FirstOrDefault(p => GetChatParticipantUserId(p) == client.User!.id);
                            isCreator = self is ChatParticipantCreator;
                            isAdmin = isCreator || self is ChatParticipantAdmin;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (TryGetFloodWaitSeconds(ex.Message, out var seconds))
                    {
                        _logger.LogWarning("GetFullChat hit rate limit, retry after {Seconds}s (chatId={ChatId})", seconds, basicChat.id);
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to get full chat info for {ChatId}: {Error}", basicChat.id, ex.Message);
                        _logger.LogDebug(ex, "GetFullChat debug details (chatId={ChatId})", basicChat.id);
                    }
                }

                groups.Add(new GroupInfo
                {
                    TelegramId = basicChat.id,
                    Title = basicChat.title,
                    Username = username,
                    MemberCount = memberCount,
                    About = about,
                    CreatorAccountId = isCreator ? accountId : null,
                    IsCreator = isCreator,
                    IsAdmin = isAdmin,
                    SyncedAt = DateTime.UtcNow
                });

                continue;
            }

            // 超级群组（Channel 类型但 megagroup=true，即 !IsChannel）
            if (chat is not Channel channel || !channel.IsActive || channel.IsChannel)
                continue;

            try
            {
                var isCreator =
                    ReadBool(channel, "creator", "Creator", "is_creator", "IsCreator")
                    || ReadFlagsHas(channel, flagName: "creator", memberNames: new[] { "flags", "Flags" });
                var adminRights = ReadObject(channel, "admin_rights", "AdminRights", "adminRights");
                var isAdmin = isCreator || adminRights != null;

                var memberCount = ReadInt(channel, 0, "participants_count", "ParticipantsCount", "participantsCount", "memberCount", "MemberCount");
                string? about = null;
                try
                {
                    var fullChannel = await ExecuteTelegramRequestAsync(
                        accountId,
                        $"拉取超级群详情({channel.id})",
                        () => client.Channels_GetFullChannel(channel),
                        cancellationToken,
                        resetClientOnTimeout: false);
                    memberCount = fullChannel.full_chat.ParticipantsCount;
                    about = (fullChannel.full_chat as ChannelFull)?.about;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("CHANNEL_MONOFORUM_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping full info for group {GroupId}: {Error}", channel.id, ex.Message);
                    }
                    else if (TryGetFloodWaitSeconds(ex.Message, out var seconds))
                    {
                        _logger.LogWarning("GetFullGroup hit rate limit, retry after {Seconds}s (groupId={GroupId})", seconds, channel.id);
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to get full group info for {GroupId}: {Error}", channel.id, ex.Message);
                        _logger.LogDebug(ex, "GetFullGroup debug details (groupId={GroupId})", channel.id);
                    }
                }

                groups.Add(new GroupInfo
                {
                    TelegramId = channel.id,
                    AccessHash = channel.access_hash,
                    Title = channel.title,
                    Username = channel.MainUsername,
                    MemberCount = memberCount,
                    About = about,
                    CreatorAccountId = isCreator ? accountId : null,
                    IsCreator = isCreator,
                    IsAdmin = isAdmin,
                    CreatedAt = channel.date,
                    SyncedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get visible group info for {GroupId}", channel.id);
            }
        }

        _logger.LogInformation("Found {Count} visible groups for account {AccountId}", groups.Count, accountId);
        return groups;
    }

    private static bool TryGetFloodWaitSeconds(string message, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (!TelegramRpcErrorTranslator.TryGetRpcWaitSeconds(message, "FLOOD_WAIT_", out seconds))
            return false;

        if (seconds < 1) seconds = 1;
        if (seconds > 120) seconds = 120;
        return true;
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

            if (value is Enum enumValue)
            {
                var flagEnum = TryGetEnumFlag(enumValue.GetType(), flagName);
                if (flagEnum != null)
                    return enumValue.HasFlag(flagEnum);
                continue;
            }

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

    private static long ReadLong(object obj, long fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value))
            {
                if (value is long l) return l;
                if (value is int i) return i;
                if (value is short s) return s;
                if (value is byte by) return by;
            }
        }

        return fallback;
    }

    private static string? ReadString(object obj, string? fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadMember(obj, name, out var value) && value is string s)
                return s;
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

    private static long GetChatParticipantUserId(object participant)
    {
        return ReadLong(participant, 0, "user_id", "UserId", "userId");
    }

    public async Task<GroupInfo?> GetGroupInfoAsync(int accountId, long groupId)
    {
        var groups = await GetVisibleGroupsAsync(accountId);
        return groups.FirstOrDefault(g => g.TelegramId == groupId);
    }

    public async Task<bool> UpdateGroupInfoAsync(int accountId, long groupId, string title, string? about)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var group = await GetEditableMegaGroupAsync(accountId, client, groupId, CancellationToken.None);

        title = (title ?? string.Empty).Trim();
        about = string.IsNullOrWhiteSpace(about) ? null : about.Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("群组标题不能为空", nameof(title));

        await client.Channels_EditTitle(group, title);
        if (about != null)
            await client.Messages_EditChatAbout(group, about);

        return true;
    }

    public async Task<bool> SetGroupVisibilityAsync(int accountId, long groupId, bool isPublic, string? username = null)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var group = await GetEditableMegaGroupAsync(accountId, client, groupId, CancellationToken.None);

        if (isPublic)
        {
            username = (username ?? string.Empty).Trim().TrimStart('@');
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("公开群组需要设置用户名");

            var current = (group.MainUsername ?? string.Empty).Trim();
            if (string.Equals(current, username, StringComparison.OrdinalIgnoreCase))
                return true;

            var available = await client.Channels_CheckUsername(group, username);
            if (!available)
                throw new InvalidOperationException($"用户名 '{username}' 不可用");

            try
            {
                await client.Channels_UpdateUsername(group, username);
            }
            catch (RpcException ex) when (ex.Message.Contains("USERNAME_NOT_MODIFIED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(group.MainUsername))
                return true;

            await client.Channels_UpdateUsername(group, string.Empty);
        }

        return true;
    }

    public async Task<bool> SetGroupPhotoAsync(
        int accountId,
        long groupId,
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
        var group = await GetEditableMegaGroupAsync(accountId, client, groupId, cancellationToken);

        if (fileStream == null)
            throw new ArgumentException("头像文件为空", nameof(fileStream));

        fileName = (fileName ?? "group_photo.jpg").Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "group_photo.jpg";

        try
        {
            await using var encoded = await TelegramImageProcessor.PrepareAvatarJpegAsync(fileStream, cancellationToken);
            var uploaded = await client.UploadFileAsync(encoded, fileName);
            if (uploaded == null)
                throw new InvalidOperationException("群组头像上传失败：上传结果为空");

            await client.Channels_EditPhoto(group, new InputChatUploadedPhoto
            {
                flags = InputChatUploadedPhoto.Flags.has_file,
                file = uploaded
            });

            return true;
        }
        catch (SixLabors.ImageSharp.UnknownImageFormatException)
        {
            throw new InvalidOperationException("不支持的图片格式（建议使用 JPG/PNG）");
        }
    }

    private async Task<Channel> GetEditableMegaGroupAsync(int accountId, Client client, long groupId, CancellationToken cancellationToken)
    {
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            cancellationToken,
            resetClientOnTimeout: true);

        var normalizedId = groupId > 0 ? groupId : Math.Abs(groupId);
        var mega = dialogs.chats.Values.OfType<Channel>()
            .FirstOrDefault(x => x.IsActive && !x.IsChannel && x.id == normalizedId);
        if (mega != null)
            return mega;

        if (dialogs.chats.Values.OfType<Chat>().Any(x => x.IsActive && x.id == normalizedId))
            throw new InvalidOperationException("当前仅支持超级群组自动修改资料/公开状态");

        throw new InvalidOperationException($"群组 {groupId} not found");
    }

    private async Task<ChatBase> GetGroupChatAsync(int accountId, Client client, long groupId, CancellationToken cancellationToken)
    {
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            cancellationToken,
            resetClientOnTimeout: true);

        var normalizedId = NormalizeGroupId(groupId);
        var chat = dialogs.chats.Values.FirstOrDefault(x =>
            x switch
            {
                Chat basicChat => basicChat.IsActive && basicChat.id == normalizedId,
                Channel channel => channel.IsActive && !channel.IsChannel && channel.id == normalizedId,
                _ => false
            });

        return chat ?? throw new InvalidOperationException($"群组 {groupId} not found");
    }

    private static long NormalizeGroupId(long groupId)
    {
        if (groupId <= -1000000000000L)
            return (-groupId) - 1000000000000L;

        return groupId > 0 ? groupId : Math.Abs(groupId);
    }

    private static AdminRights SanitizeGroupAdminRights(AdminRights rights)
    {
        var sanitized = rights & ~AdminRights.PostMessages & ~AdminRights.EditMessages;
        return sanitized == AdminRights.None ? AdminRights.BasicAdmin & ~AdminRights.PostMessages & ~AdminRights.EditMessages : sanitized;
    }

    private static ChatBannedRights BuildKickRights(bool permanentBan)
    {
        var until = permanentBan ? DateTime.UtcNow.AddYears(100) : DateTime.UtcNow.AddSeconds(60);
        return new ChatBannedRights
        {
            flags = ChatBannedRights.Flags.view_messages,
            until_date = until
        };
    }

    private static ChatAdminRights ConvertAdminRights(AdminRights rights)
    {
        ChatAdminRights.Flags flags = 0;

        if (rights.HasFlag(AdminRights.ChangeInfo))
            flags |= ChatAdminRights.Flags.change_info;
        if (rights.HasFlag(AdminRights.PostMessages))
            flags |= ChatAdminRights.Flags.post_messages;
        if (rights.HasFlag(AdminRights.EditMessages))
            flags |= ChatAdminRights.Flags.edit_messages;
        if (rights.HasFlag(AdminRights.DeleteMessages))
            flags |= ChatAdminRights.Flags.delete_messages;
        if (rights.HasFlag(AdminRights.BanUsers))
            flags |= ChatAdminRights.Flags.ban_users;
        if (rights.HasFlag(AdminRights.InviteUsers))
            flags |= ChatAdminRights.Flags.invite_users;
        if (rights.HasFlag(AdminRights.PinMessages))
            flags |= ChatAdminRights.Flags.pin_messages;
        if (rights.HasFlag(AdminRights.ManageCall))
            flags |= ChatAdminRights.Flags.manage_call;
        if (rights.HasFlag(AdminRights.AddAdmins))
            flags |= ChatAdminRights.Flags.add_admins;
        if (rights.HasFlag(AdminRights.Anonymous))
            flags |= ChatAdminRights.Flags.anonymous;
        if (rights.HasFlag(AdminRights.ManageTopics))
            flags |= ChatAdminRights.Flags.manage_topics;

        return new ChatAdminRights { flags = flags };
    }

    public async Task<InviteResult> InviteUserAsync(int accountId, long groupId, string username)
    {
        username = (username ?? string.Empty).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
            return new InviteResult(username, false, "请输入要邀请的用户名");

        var client = await GetOrCreateConnectedClientAsync(accountId);
        try
        {
            var chat = await GetGroupChatAsync(accountId, client, groupId, CancellationToken.None);
            var resolved = await client.Contacts_ResolveUsername(username);
            var inputUser = new InputUser(resolved.User.id, resolved.User.access_hash);

            if (chat is Channel megaGroup)
                await client.Channels_InviteToChannel(megaGroup, inputUser);
            else if (chat is Chat basicGroup)
                await client.Messages_AddChatUser(basicGroup.id, inputUser, fwd_limit: 0);
            else
                throw new InvalidOperationException("群组类型无效");

            _logger.LogInformation("Successfully invited @{Username} to group {GroupId}", username, groupId);
            return new InviteResult(username, true);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Failed to invite @{Username} to group {GroupId}: {Error}", username, groupId, ex.Message);
            if (ex.Message.Contains("USER_ALREADY_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
                return new InviteResult(username, true);
            if (ex.Message.Contains("USER_BOT", StringComparison.OrdinalIgnoreCase))
                return new InviteResult(username, false, "目标是 Bot：请用“设置管理员”把 Bot 加为管理员（不能用邀请成员）");
            if (ex.Message.Contains("RIGHT_FORBIDDEN", StringComparison.OrdinalIgnoreCase))
                return new InviteResult(username, false, "RIGHT_FORBIDDEN：执行账号缺少邀请权限，或该群组限制邀请");

            return new InviteResult(username, false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error inviting @{Username} to group {GroupId}", username, groupId);
            return new InviteResult(username, false, ex.Message);
        }
    }

    public async Task<List<InviteResult>> BatchInviteUsersAsync(int accountId, long groupId, List<string> usernames, int delayMs = 2000)
    {
        var results = new List<InviteResult>();
        foreach (var username in usernames)
        {
            var result = await InviteUserAsync(accountId, groupId, username);
            results.Add(result);

            if (usernames.IndexOf(username) < usernames.Count - 1 && delayMs > 0)
                await Task.Delay(delayMs + Random.Shared.Next(500, 1500));
        }

        return results;
    }

    public async Task<bool> SetAdminAsync(int accountId, long groupId, string username, AdminRights rights, string title = "Admin")
    {
        username = (username ?? string.Empty).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("请输入管理员用户名", nameof(username));

        var client = await GetOrCreateConnectedClientAsync(accountId);
        var chat = await GetGroupChatAsync(accountId, client, groupId, CancellationToken.None);
        var resolved = await client.Contacts_ResolveUsername(username);

        if (chat is Chat basicGroup)
        {
            await client.Messages_EditChatAdmin(basicGroup.id, resolved.User, is_admin: true);
            return true;
        }

        if (chat is not Channel megaGroup)
            throw new InvalidOperationException("群组类型无效");

        var effectiveRights = SanitizeGroupAdminRights(rights);
        try
        {
            await client.Channels_EditAdmin(megaGroup, resolved.User, ConvertAdminRights(effectiveRights), string.IsNullOrWhiteSpace(title) ? "Admin" : title.Trim());
        }
        catch (RpcException ex) when (ex.Message.Contains("USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await client.Channels_InviteToChannel(megaGroup, new InputUser(resolved.User.id, resolved.User.access_hash));
            }
            catch (RpcException inviteEx) when (inviteEx.Message.Contains("USER_ALREADY_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
            {
                // ignore
            }
            catch (RpcException inviteEx) when (inviteEx.Message.Contains("USER_BOT", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(inviteEx, "Invite bot as member is not allowed for group {GroupId}, continue promoting", groupId);
            }
            catch (RpcException inviteEx) when (inviteEx.Message.Contains("RIGHT_FORBIDDEN", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("目标用户不在群组，且执行账号缺少“邀请用户”权限，无法先邀请再设置管理员。", inviteEx);
            }

            await client.Channels_EditAdmin(megaGroup, resolved.User, ConvertAdminRights(effectiveRights), string.IsNullOrWhiteSpace(title) ? "Admin" : title.Trim());
        }
        catch (RpcException ex) when (ex.Message.Contains("RIGHT_FORBIDDEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RIGHT_FORBIDDEN：执行账号无权设置管理员，或请求授予的权限超出可授予范围。", ex);
        }

        _logger.LogInformation("Set @{Username} as admin in group {GroupId}", username, groupId);
        return true;
    }

    public async Task<List<SetAdminResult>> BatchSetAdminsAsync(int accountId, long groupId, List<AdminRequest> requests)
    {
        var results = new List<SetAdminResult>();
        foreach (var request in requests)
        {
            try
            {
                var ok = await SetAdminAsync(accountId, groupId, request.Username, request.Rights, request.Title);
                results.Add(new SetAdminResult(request.Username, ok, ok ? null : "失败"));
            }
            catch (Exception ex)
            {
                results.Add(new SetAdminResult(request.Username, false, ex.Message));
            }
        }

        return results;
    }

    public async Task<bool> KickUserAsync(int accountId, long groupId, string username, bool permanentBan = false)
    {
        username = (username ?? string.Empty).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("请输入要踢出的用户名", nameof(username));

        var client = await GetOrCreateConnectedClientAsync(accountId);
        var chat = await GetGroupChatAsync(accountId, client, groupId, CancellationToken.None);
        var resolved = await client.Contacts_ResolveUsername(username);
        var inputUser = new InputUser(resolved.User.id, resolved.User.access_hash);

        if (chat is Channel megaGroup)
        {
            await client.Channels_EditBanned(megaGroup, new InputPeerUser(resolved.User.id, resolved.User.access_hash), BuildKickRights(permanentBan));
            return true;
        }

        if (chat is Chat basicGroup)
        {
            await client.Messages_DeleteChatUser(basicGroup.id, inputUser, revoke_history: permanentBan);
            return true;
        }

        throw new InvalidOperationException("群组类型无效");
    }

    public async Task<bool> KickUserByUserIdAsync(int accountId, long groupId, long userId, bool permanentBan = false)
    {
        if (userId <= 0)
            throw new ArgumentException("请输入要踢出的用户 ID", nameof(userId));

        var client = await GetOrCreateConnectedClientAsync(accountId);
        var chat = await GetGroupChatAsync(accountId, client, groupId, CancellationToken.None);

        try
        {
            if (chat is Channel megaGroup)
            {
                await client.Channels_EditBanned(megaGroup, new InputPeerUser(userId, 0), BuildKickRights(permanentBan));
                return true;
            }

            if (chat is Chat basicGroup)
            {
                await client.Messages_DeleteChatUser(basicGroup.id, new InputUser(userId, 0), revoke_history: permanentBan);
                return true;
            }
        }
        catch (RpcException ex) when (
            ex.Message.Contains("USER_ID_INVALID", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("该用户 ID 无法直接解析，请改用 @username 执行群组踢人。", ex);
        }

        throw new InvalidOperationException("群组类型无效");
    }

    public async Task<GroupAdminRemovalResult> RemoveAdminAndKickAsync(
        int accountId,
        long groupId,
        long targetUserId,
        CancellationToken cancellationToken = default)
    {
        if (targetUserId <= 0)
            throw new ArgumentException("管理员用户 ID 无效", nameof(targetUserId));

        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId, cancellationToken);
            var executorUserId = client.User?.id
                ?? throw new InvalidOperationException("执行账号未登录或 Session 已失效，请重新登录后再试");
            if (executorUserId == targetUserId)
                throw new InvalidOperationException("不能使用管理员本人将自己踢出群组");

            var chat = await GetGroupChatAsync(accountId, client, groupId, cancellationToken);
            return chat switch
            {
                Chat basicGroup => await RemoveBasicGroupAdminAndKickAsync(
                    accountId,
                    client,
                    basicGroup,
                    executorUserId,
                    targetUserId,
                    cancellationToken),
                Channel megaGroup => await RemoveMegaGroupAdminAndKickAsync(
                    accountId,
                    client,
                    megaGroup,
                    executorUserId,
                    targetUserId,
                    cancellationToken),
                _ => throw new InvalidOperationException("群组类型无效")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException(TranslateGroupAdminRemovalError(ex.Message), ex);
        }
    }

    private async Task<GroupAdminRemovalResult> RemoveBasicGroupAdminAndKickAsync(
        int accountId,
        Client client,
        Chat basicGroup,
        long executorUserId,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        var fullChat = await ExecuteTelegramRequestAsync(
            accountId,
            $"读取基础群管理员({basicGroup.id})",
            () => client.Messages_GetFullChat(basicGroup.id),
            cancellationToken,
            resetClientOnTimeout: true);
        if (fullChat.full_chat is not ChatFull chatFull || chatFull.participants is not ChatParticipants participants)
            throw new InvalidOperationException("无法读取基础群管理员列表，请刷新后重试");

        var target = participants.participants.FirstOrDefault(item => GetChatParticipantUserId(item) == targetUserId);
        if (target is ChatParticipantCreator)
            throw new InvalidOperationException("目标是群组创建者，不能移除或踢出");
        if (target is not ChatParticipantAdmin)
            throw new InvalidOperationException("目标已不是群组管理员，请刷新列表后重试");

        var executor = participants.participants.FirstOrDefault(item => GetChatParticipantUserId(item) == executorUserId);
        if (executor is not ChatParticipantCreator && executor is not ChatParticipantAdmin)
            throw new InvalidOperationException("执行账号不是该群组管理员，无法移除管理员");

        if (!fullChat.users.TryGetValue(targetUserId, out var targetUser) || targetUser.access_hash == 0)
            throw new InvalidOperationException("无法取得目标管理员的 Telegram 访问凭据，请刷新管理员列表后重试");

        var inputUser = new InputUser(targetUser.id, targetUser.access_hash);
        try
        {
            await ExecuteTelegramRequestAsync(
                accountId,
                $"撤销基础群管理员({basicGroup.id}/{targetUserId})",
                () => client.Messages_EditChatAdmin(basicGroup.id, inputUser, is_admin: false),
                cancellationToken,
                resetClientOnTimeout: true);
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException(TranslateGroupAdminRemovalError(ex.Message), ex);
        }

        return await KickDemotedBasicGroupMemberAsync(
            accountId,
            client,
            basicGroup,
            inputUser,
            targetUserId,
            cancellationToken);
    }

    private async Task<GroupAdminRemovalResult> RemoveMegaGroupAdminAndKickAsync(
        int accountId,
        Client client,
        Channel megaGroup,
        long executorUserId,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        var participants = await ExecuteTelegramRequestAsync(
            accountId,
            $"读取超级群管理员({megaGroup.id})",
            () => client.Channels_GetParticipants(megaGroup, new ChannelParticipantsAdmins()),
            cancellationToken,
            resetClientOnTimeout: true);

        var target = participants.participants.FirstOrDefault(item => item switch
        {
            ChannelParticipantAdmin admin => admin.user_id == targetUserId,
            ChannelParticipantCreator creator => creator.user_id == targetUserId,
            _ => false
        });
        if (target is ChannelParticipantCreator)
            throw new InvalidOperationException("目标是群组创建者，不能移除或踢出");
        if (target is not ChannelParticipantAdmin)
            throw new InvalidOperationException("目标已不是群组管理员，请刷新列表后重试");

        var executor = participants.participants.FirstOrDefault(item => item switch
        {
            ChannelParticipantAdmin admin => admin.user_id == executorUserId,
            ChannelParticipantCreator creator => creator.user_id == executorUserId,
            _ => false
        });
        if (executor is not ChannelParticipantCreator
            && (executor is not ChannelParticipantAdmin executorAdmin
                || executorAdmin.admin_rights == null
                || !executorAdmin.admin_rights.flags.HasFlag(ChatAdminRights.Flags.add_admins)
                || !executorAdmin.admin_rights.flags.HasFlag(ChatAdminRights.Flags.ban_users)))
        {
            throw new InvalidOperationException("执行账号需要同时拥有“添加管理员”和“封禁用户”权限，才能移除管理员并踢人");
        }

        if (!participants.users.TryGetValue(targetUserId, out var targetUser) || targetUser.access_hash == 0)
            throw new InvalidOperationException("无法取得目标管理员的 Telegram 访问凭据，请刷新管理员列表后重试");

        var inputUser = new InputUser(targetUser.id, targetUser.access_hash);
        var inputPeer = new InputPeerUser(targetUser.id, targetUser.access_hash);
        try
        {
            await ExecuteTelegramRequestAsync(
                accountId,
                $"撤销超级群管理员({megaGroup.id}/{targetUserId})",
                () => client.Channels_EditAdmin(
                    megaGroup,
                    inputUser,
                    new ChatAdminRights { flags = 0 },
                    string.Empty),
                cancellationToken,
                resetClientOnTimeout: true);
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException(TranslateGroupAdminRemovalError(ex.Message), ex);
        }

        return await KickDemotedMegaGroupMemberAsync(
            accountId,
            client,
            megaGroup,
            inputPeer,
            targetUserId,
            cancellationToken);
    }

    private async Task<GroupAdminRemovalResult> KickDemotedBasicGroupMemberAsync(
        int accountId,
        Client client,
        Chat basicGroup,
        InputUser inputUser,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteTelegramRequestAsync(
                accountId,
                $"踢出基础群成员({basicGroup.id}/{targetUserId})",
                () => client.Messages_DeleteChatUser(basicGroup.id, inputUser, revoke_history: false),
                cancellationToken,
                resetClientOnTimeout: true);
            return new GroupAdminRemovalResult(true, true, true, false, "已撤销管理员权限并踢出成员");
        }
        catch (RpcException ex) when (IsTargetAlreadyAbsent(ex.Message))
        {
            return new GroupAdminRemovalResult(true, true, false, true, "已撤销管理员权限；目标已不在群组中");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GroupAdminRemovalResult(
                false,
                true,
                false,
                false,
                $"已撤销管理员权限，但尚未踢出成员：{TranslateGroupAdminRemovalError(ex.Message)}");
        }
    }

    private async Task<GroupAdminRemovalResult> KickDemotedMegaGroupMemberAsync(
        int accountId,
        Client client,
        Channel megaGroup,
        InputPeerUser inputPeer,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteTelegramRequestAsync(
                accountId,
                $"踢出超级群成员({megaGroup.id}/{targetUserId})",
                () => client.Channels_EditBanned(megaGroup, inputPeer, BuildKickRights(permanentBan: false)),
                cancellationToken,
                resetClientOnTimeout: true);
            return new GroupAdminRemovalResult(true, true, true, false, "已撤销管理员权限并踢出成员");
        }
        catch (RpcException ex) when (IsTargetAlreadyAbsent(ex.Message))
        {
            return new GroupAdminRemovalResult(true, true, false, true, "已撤销管理员权限；目标已不在群组中");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GroupAdminRemovalResult(
                false,
                true,
                false,
                false,
                $"已撤销管理员权限，但尚未踢出成员：{TranslateGroupAdminRemovalError(ex.Message)}");
        }
    }

    private static bool IsTargetAlreadyAbsent(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && error.Contains("USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase);

    internal static string TranslateGroupAdminRemovalError(string? error)
    {
        var text = error?.Trim() ?? string.Empty;
        if (text.Contains("USER_CREATOR", StringComparison.OrdinalIgnoreCase))
            return "目标是群组创建者，不能移除或踢出";
        if (text.Contains("USER_ADMIN_INVALID", StringComparison.OrdinalIgnoreCase))
            return "目标管理员状态已变化，请刷新列表后重试";
        if (text.Contains("USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
            return "目标已不在群组中";
        if (text.Contains("CHAT_ADMIN_REQUIRED", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RIGHT_FORBIDDEN", StringComparison.OrdinalIgnoreCase))
            return "执行账号缺少取消管理员或封禁成员权限，请更换具备完整权限的群组管理员";
        if (text.Contains("USER_ID_INVALID", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase))
            return "无法解析目标管理员，请刷新管理员列表后重试";
        if (TelegramRpcErrorTranslator.TryGetRpcWaitSeconds(text, "FLOOD_WAIT_", out var seconds))
            return $"Telegram 风控限流，请等待约 {seconds} 秒后再试";
        if (text.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
            return "Telegram 风控限流，请稍后再试";

        return string.IsNullOrWhiteSpace(text) ? "Telegram 未返回具体失败原因" : text;
    }

    public async Task<bool> LeaveGroupAsync(int accountId, long groupId)
    {
        try
        {
            var client = await GetOrCreateConnectedClientAsync(accountId);
            var dialogs = await ExecuteTelegramRequestAsync(
                accountId,
                "拉取频道/群组对话列表",
                () => client.Messages_GetAllDialogs(),
                CancellationToken.None,
                resetClientOnTimeout: true);

            var normalizedId = groupId > 0 ? groupId : Math.Abs(groupId);
            var chat = dialogs.chats.Values.FirstOrDefault(x =>
                x switch
                {
                    Chat basicChat => basicChat.id == normalizedId,
                    Channel channel => !channel.IsChannel && channel.id == normalizedId,
                    _ => false
                });

            if (chat == null)
                throw new InvalidOperationException($"群组 {groupId} not found");

            await ExecuteTelegramRequestAsync(
                accountId,
                $"退出群组({normalizedId})",
                () => client.LeaveChat(chat.ToInputPeer()),
                CancellationToken.None,
                resetClientOnTimeout: false);

            return true;
        }
        catch (RpcException ex) when (ex.Code == 400 && string.Equals(ex.Message, "USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    public async Task<bool> DisbandGroupAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            CancellationToken.None,
            resetClientOnTimeout: true);

        var normalizedId = groupId > 0 ? groupId : Math.Abs(groupId);
        var chat = dialogs.chats.Values.FirstOrDefault(x =>
            x switch
            {
                Chat basicChat => basicChat.id == normalizedId,
                Channel channel => !channel.IsChannel && channel.id == normalizedId,
                _ => false
            });

        if (chat == null)
            throw new InvalidOperationException($"群组 {groupId} not found");

        if (chat is Chat)
            throw new InvalidOperationException("基础群暂不支持一键解散，请在 Telegram 客户端手动操作或先升级为超级群");

        if (chat is not Channel megaGroup)
            throw new InvalidOperationException("仅支持解散超级群");

        var input = new InputChannel(megaGroup.id, megaGroup.access_hash);
        await ExecuteTelegramRequestAsync(
            accountId,
            $"解散群组({normalizedId})",
            () => client.Channels_DeleteChannel(input),
            CancellationToken.None,
            resetClientOnTimeout: false);

        return true;
    }

    public async Task<bool> TransferOwnershipAsync(int accountId, long groupId, string targetUsername, string password)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await ExecuteTelegramRequestAsync(
            accountId,
            "拉取频道/群组对话列表",
            () => client.Messages_GetAllDialogs(),
            CancellationToken.None,
            resetClientOnTimeout: true);

        var normalizedId = groupId > 0 ? groupId : Math.Abs(groupId);
        var chat = dialogs.chats.Values.FirstOrDefault(x =>
            x switch
            {
                Chat basicChat => basicChat.IsActive && basicChat.id == normalizedId,
                Channel channel => channel.IsActive && !channel.IsChannel && channel.id == normalizedId,
                _ => false
            });

        if (chat == null)
            throw new InvalidOperationException($"群组 {groupId} not found");

        var user = await ResolveTransferTargetUserAsync(client, targetUsername);
        var srp = await BuildOwnershipTransferPasswordCheckAsync(client, password);

        try
        {
            await client.Messages_EditChatCreator(chat.ToInputPeer(), user, srp);
            _logger.LogInformation("Transferred group {GroupId} ownership to @{Username} by account {AccountId}", groupId, targetUsername.Trim().TrimStart('@'), accountId);
            return true;
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException(TranslateOwnershipTransferError(ex.Message), ex);
        }
    }

    public async Task<string> ExportJoinLinkAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await client.Messages_GetAllDialogs();

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

    private static async Task<InputUser> ResolveTransferTargetUserAsync(Client client, string targetUsername)
    {
        var username = (targetUsername ?? string.Empty).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("请填写新所有者用户名", nameof(targetUsername));
        if (long.TryParse(username, out _))
            throw new ArgumentException("转让所有权需要填写目标用户的 @username，不能只填数字用户 ID。", nameof(targetUsername));

        try
        {
            var resolved = await client.Contacts_ResolveUsername(username);
            var user = resolved.User;
            if (user == null || user.access_hash == 0)
                throw new InvalidOperationException("无法解析目标用户，请确认用户名正确，且执行账号能访问该用户。");

            return new InputUser(user.id, user.access_hash);
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException(TranslateOwnershipTransferError(ex.Message), ex);
        }
    }

    private static async Task<InputCheckPasswordSRP> BuildOwnershipTransferPasswordCheckAsync(Client client, string password)
    {
        password = (password ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("请输入当前创建者账号的二级密码", nameof(password));

        var accountPassword = await client.Account_GetPassword();
        if (accountPassword.current_algo == null)
            throw new InvalidOperationException("当前创建者账号未开启二级密码，Telegram 不允许直接转让所有权。请先为该账号开启二级密码后再操作。");

        try
        {
            return await WTelegram.Client.InputCheckPassword(accountPassword, password);
        }
        catch (RpcException ex)
        {
            throw new InvalidOperationException(TranslateOwnershipTransferError(ex.Message), ex);
        }
    }

    private static string TranslateOwnershipTransferError(string? message)
    {
        var text = message ?? string.Empty;
        if (text.Contains("PASSWORD_HASH_INVALID", StringComparison.OrdinalIgnoreCase))
            return "二级密码错误，请确认填写的是当前创建者账号的二级密码。";
        if (text.Contains("PASSWORD_MISSING", StringComparison.OrdinalIgnoreCase))
            return "当前账号未开启二级密码，Telegram 要求开启二级密码后才能转让所有权。";
        if (text.Contains("CHAT_ADMIN_REQUIRED", StringComparison.OrdinalIgnoreCase) || text.Contains("RIGHT_FORBIDDEN", StringComparison.OrdinalIgnoreCase))
            return "执行账号不是创建者，或没有转让所有权权限。请使用当前群组创建者账号执行。";
        if (text.Contains("USER_ID_INVALID", StringComparison.OrdinalIgnoreCase) || text.Contains("USERNAME_INVALID", StringComparison.OrdinalIgnoreCase) || text.Contains("USERNAME_NOT_OCCUPIED", StringComparison.OrdinalIgnoreCase))
            return "目标用户无效，请确认用户名正确。";
        if (text.Contains("USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase))
            return "目标用户不在该群组内，请先邀请目标用户并设为管理员后再转让。";
        if (text.Contains("USER_NOT_MUTUAL_CONTACT", StringComparison.OrdinalIgnoreCase))
            return "目标用户隐私限制导致无法转让，请先互加联系人或让目标用户加入群组。";
        var waitError = TelegramRpcErrorTranslator.TranslateOwnershipTransferWaitError(text);
        if (waitError != null)
            return waitError;

        return string.IsNullOrWhiteSpace(text) ? "所有权转让失败" : text;
    }

    public async Task<List<ChannelAdminInfo>> GetAdminsAsync(int accountId, long groupId)
    {
        var client = await GetOrCreateConnectedClientAsync(accountId);
        var dialogs = await client.Messages_GetAllDialogs();

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

    private async Task<WTelegram.Client> GetOrCreateConnectedClientAsync(int accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        var absoluteSessionPath = _sessionPathResolver.Resolve(account.SessionPath);
        // 先释放可能仍会保存旧 Session 的客户端，避免其在转换完成后覆盖新文件。
        await _clientPool.RemoveClientAsync(accountId);
        if (System.IO.File.Exists(absoluteSessionPath) && LooksLikeSqliteSession(absoluteSessionPath))
        {
            var proxyResolution = await _proxyResolver.ResolveAsync(accountId, cancellationToken);
            var proxy = proxyResolution.Proxy
                ?? (proxyResolution.UseGlobalProxy
                    ? throw new InvalidOperationException(
                        "全局代理路由未在 Session 转换前解析，已阻止降级为直连")
                    : null);

            var converted = await SessionDataConverter.TryConvertSqliteSessionFromJsonAsync(
                phone: account.Phone,
                apiId: apiId,
                apiHash: apiHash,
                sqliteSessionPath: absoluteSessionPath,
                logger: _logger,
                proxy: proxy,
                cancellationToken: cancellationToken
            );

            if (!converted.Ok)
            {
                throw new InvalidOperationException(
                    $"该账号的 Session 文件为 SQLite 格式：{account.SessionPath}，本项目无法直接复用。" +
                    "已尝试从本地 json（例如 sessions/<手机号>.json 或 session数据/<手机号>/*.json）读取 session_string 自动转换但失败；" +
                    $"原因：{converted.Reason}。请到【账号-手机号登录】重新登录生成新的 sessions/*.session 后再操作。");
            }
        }

        var client = await _clientPool.GetOrCreateClientAsync(accountId, apiId, apiHash, absoluteSessionPath, sessionKey, account.Phone, account.UserId, account.DeviceProfileKey);

        try
        {
            await ExecuteTelegramRequestAsync(
                accountId,
                "连接 Telegram",
                () => client.ConnectAsync(),
                cancellationToken,
                resetClientOnTimeout: true);

            if (client.User == null && (client.UserId != 0 || account.UserId != 0))
            {
                await ExecuteTelegramRequestAsync(
                    accountId,
                    "恢复 Telegram 登录状态",
                    () => client.LoginUserIfNeeded(reloginOnFailedResume: false),
                    cancellationToken,
                    resetClientOnTimeout: true);
            }
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

    private TimeSpan GetTelegramRequestTimeout()
    {
        var seconds = int.TryParse(_configuration["Telegram:RequestTimeoutSeconds"], out var parsedSeconds)
            ? parsedSeconds
            : 90;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 600));
    }

    private async Task ExecuteTelegramRequestAsync(
        int accountId,
        string operation,
        Func<Task> action,
        CancellationToken cancellationToken,
        bool resetClientOnTimeout)
    {
        var timeout = GetTelegramRequestTimeout();

        try
        {
            await action().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Telegram request timed out after {TimeoutSeconds}s for account {AccountId}: {Operation}",
                timeout.TotalSeconds,
                accountId,
                operation);

            if (resetClientOnTimeout)
                await _clientPool.RemoveClientAsync(accountId);

            throw new TimeoutException($"Telegram 请求超时：{operation} 超过 {timeout.TotalSeconds:0} 秒，可能是 Session 失效、账号受限、网络异常或代理异常");
        }
    }

    private async Task<T> ExecuteTelegramRequestAsync<T>(
        int accountId,
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        bool resetClientOnTimeout)
    {
        var timeout = GetTelegramRequestTimeout();

        try
        {
            return await action().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Telegram request timed out after {TimeoutSeconds}s for account {AccountId}: {Operation}",
                timeout.TotalSeconds,
                accountId,
                operation);

            if (resetClientOnTimeout)
                await _clientPool.RemoveClientAsync(accountId);

            throw new TimeoutException($"Telegram 请求超时：{operation} 超过 {timeout.TotalSeconds:0} 秒，可能是 Session 失效、账号受限、网络异常或代理异常");
        }
    }

    private int ResolveApiId(TelegramPanel.Data.Entities.Account account)
    {
        if (account.ApiId > 0)
            return account.ApiId;
        if (int.TryParse(_configuration["Telegram:ApiId"], out var globalApiId) && globalApiId > 0)
            return globalApiId;
        throw new InvalidOperationException("账号缺少 ApiId，且未配置全局 ApiId");
    }

    private string ResolveApiHash(TelegramPanel.Data.Entities.Account account)
    {
        if (!string.IsNullOrWhiteSpace(account.ApiHash))
            return account.ApiHash.Trim();
        var global = _configuration["Telegram:ApiHash"];
        if (!string.IsNullOrWhiteSpace(global))
            return global.Trim();
        throw new InvalidOperationException("账号缺少 ApiHash，且未配置全局 ApiHash");
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
