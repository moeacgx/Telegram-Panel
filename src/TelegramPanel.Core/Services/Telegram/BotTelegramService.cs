using System.Text.Json;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Bot 相关能力（双轨）：
/// - 主轨：Telegram Bot API（不依赖 ApiId/ApiHash），用于“秒级新增频道/导出邀请链接”
/// - 兜底：保留手动同步入口（本实现同样走 Bot API pull updates）
/// </summary>
public class BotTelegramService
{
    private readonly BotManagementService _botManagement;
    private readonly TelegramBotApiClient _api;
    private readonly ILogger<BotTelegramService> _logger;

    public BotTelegramService(
        BotManagementService botManagement,
        TelegramBotApiClient api,
        ILogger<BotTelegramService> logger)
    {
        _botManagement = botManagement;
        _api = api;
        _logger = logger;
    }

    /// <summary>
    /// 拉取 Bot API updates 并把 bot 新加入的频道写入数据库（同时确认 offset）。
    /// </summary>
    public async Task<int> SyncBotChannelsAsync(int botId, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        var token = bot.Token;
        var offset = bot.LastUpdateId.HasValue ? bot.LastUpdateId.Value + 1 : (long?)null;

        var allowedUpdates = JsonSerializer.Serialize(new[] { "my_chat_member" });
        var result = await _api.CallAsync(token, "getUpdates", new Dictionary<string, string?>
        {
            ["offset"] = offset?.ToString(),
            ["timeout"] = "0",
            ["limit"] = "100",
            ["allowed_updates"] = allowedUpdates
        }, cancellationToken);

        if (result.ValueKind != JsonValueKind.Array)
            return 0;

        // Telegram 会针对同一个 chat 连续产生多条 my_chat_member（例如 member -> administrator），
        // 这里按 chat_id 去重并只应用“最后一次状态”，避免“新增两个”的错觉/重复写库。
        var changesByChatId = new Dictionary<long, BotChatMemberChange>();
        long? maxUpdateId = null;

        foreach (var update in result.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (update.ValueKind != JsonValueKind.Object)
                continue;

            if (update.TryGetProperty("update_id", out var updateIdEl) && updateIdEl.TryGetInt64(out var updateId))
            {
                maxUpdateId = maxUpdateId.HasValue ? Math.Max(maxUpdateId.Value, updateId) : updateId;
            }

            // 只处理 my_chat_member：Bot 被加入/移除/升降权
            if (!update.TryGetProperty("my_chat_member", out var myChatMember))
                continue;

            if (!TryParseChatMemberUpdate(myChatMember, out var chat, out var status))
                continue;

            changesByChatId[chat.Id] = new BotChatMemberChange(chat, status);
        }

        var affected = 0;
        foreach (var kv in changesByChatId)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chatId = kv.Key;
            var change = kv.Value;

            // 本系统 Bot 只管理频道（channel）；如果收到群组/超级群组更新，则仅用于“清理旧数据”，避免列表统计被污染。
            if (change.Chat.Type is not "channel")
            {
                await _botManagement.DeleteChannelByTelegramIdAsync(bot.Id, chatId);
                affected++;
                continue;
            }

            // 只把“具备管理员/创建者权限”的频道纳入 Bot 列表；
            // left/kicked 或降权为 member 等，直接从列表移除（否则导出邀请会失败且列表不准）。
            if (change.Status is "administrator" or "creator")
            {
                await _botManagement.UpsertChannelAsync(new BotChannel
                {
                    BotId = bot.Id,
                    TelegramId = chatId,
                    Title = string.IsNullOrWhiteSpace(change.Chat.Title) ? $"频道 {chatId}" : change.Chat.Title,
                    Username = string.IsNullOrWhiteSpace(change.Chat.Username) ? null : change.Chat.Username.Trim().TrimStart('@'),
                    IsBroadcast = true,
                    MemberCount = 0,
                    About = null,
                    AccessHash = null,
                    CreatedAt = null
                });
            }
            else
            {
                await _botManagement.DeleteChannelByTelegramIdAsync(bot.Id, chatId);
            }

            affected++;
        }

        if (maxUpdateId.HasValue)
            bot.LastUpdateId = maxUpdateId.Value;
        bot.LastSyncAt = DateTime.UtcNow;
        await _botManagement.UpdateBotAsync(bot);

        if (affected > 0)
        {
            var ids = string.Join(", ", changesByChatId.Keys.Take(5));
            _logger.LogInformation("Bot {BotId} updates applied: affected {Affected} chats (first: {ChatIds})", botId, affected, ids);
        }

        return affected;
    }

    /// <summary>
    /// 导出加入链接：公开频道返回 t.me 链接；否则创建邀请链接。
    /// </summary>
    public async Task<string> ExportInviteLinkAsync(int botId, long channelTelegramId, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        // 优先用公开用户名（无需管理员权限）
        var channels = await _botManagement.GetChannelsAsync(botId);
        var found = channels.FirstOrDefault(x => x.TelegramId == channelTelegramId);
        if (found != null && !string.IsNullOrWhiteSpace(found.Username))
            return $"https://t.me/{found.Username.Trim().TrimStart('@')}";

        // 私密/无用户名：需要管理员权限创建邀请链接
        var result = await _api.CallAsync(bot.Token, "createChatInviteLink", new Dictionary<string, string?>
        {
            ["chat_id"] = channelTelegramId.ToString()
        }, cancellationToken);

        // result 是 ChatInviteLink 对象
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("invite_link", out var linkEl))
        {
            var link = linkEl.GetString();
            if (!string.IsNullOrWhiteSpace(link))
                return link;
        }

        throw new InvalidOperationException("无法获取邀请链接（可能无权限/被限制）");
    }

    public async Task<BotChatInfo> GetChatInfoAsync(int botId, long channelTelegramId, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        var chat = await _api.CallAsync(bot.Token, "getChat", new Dictionary<string, string?>
        {
            ["chat_id"] = channelTelegramId.ToString()
        }, cancellationToken);

        if (chat.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("无法获取频道信息（返回格式异常）");

        var type = chat.TryGetProperty("type", out var typeEl) ? (typeEl.GetString() ?? "") : "";
        var title = chat.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var username = chat.TryGetProperty("username", out var usernameEl) ? usernameEl.GetString() : null;
        var description = chat.TryGetProperty("description", out var descEl) ? descEl.GetString() : null;

        int? memberCount = null;
        try
        {
            var countEl = await _api.CallAsync(bot.Token, "getChatMemberCount", new Dictionary<string, string?>
            {
                ["chat_id"] = channelTelegramId.ToString()
            }, cancellationToken);
            if (countEl.ValueKind == JsonValueKind.Number && countEl.TryGetInt32(out var c))
                memberCount = c;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "getChatMemberCount failed for bot {BotId} chat {ChatId}", botId, channelTelegramId);
        }

        return new BotChatInfo(
            TelegramId: channelTelegramId,
            Type: type,
            Title: title,
            Username: string.IsNullOrWhiteSpace(username) ? null : username.Trim().TrimStart('@'),
            Description: string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            MemberCount: memberCount
        );
    }

    public async Task<List<BotChatAdminInfo>> GetChatAdminsAsync(int botId, long channelTelegramId, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        var result = await _api.CallAsync(bot.Token, "getChatAdministrators", new Dictionary<string, string?>
        {
            ["chat_id"] = channelTelegramId.ToString()
        }, cancellationToken);

        if (result.ValueKind != JsonValueKind.Array)
            return new List<BotChatAdminInfo>();

        var list = new List<BotChatAdminInfo>();
        foreach (var el in result.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;

            var status = el.TryGetProperty("status", out var statusEl) ? (statusEl.GetString() ?? "") : "";
            var customTitle = el.TryGetProperty("custom_title", out var ctEl) ? ctEl.GetString() : null;

            if (!el.TryGetProperty("user", out var userEl) || userEl.ValueKind != JsonValueKind.Object)
                continue;

            if (!userEl.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var userId))
                continue;

            var username = userEl.TryGetProperty("username", out var uEl) ? uEl.GetString() : null;
            var firstName = userEl.TryGetProperty("first_name", out var fnEl) ? fnEl.GetString() : null;
            var lastName = userEl.TryGetProperty("last_name", out var lnEl) ? lnEl.GetString() : null;

            list.Add(new BotChatAdminInfo(
                UserId: userId,
                Username: string.IsNullOrWhiteSpace(username) ? null : username.Trim().TrimStart('@'),
                FirstName: firstName,
                LastName: lastName,
                Status: status,
                CustomTitle: string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim()
            ));
        }

        return list
            .OrderByDescending(x => x.IsCreator)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<bool> UpdateChannelInfoAsync(int botId, long channelTelegramId, string title, string? about, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        title = (title ?? string.Empty).Trim();
        about = string.IsNullOrWhiteSpace(about) ? null : about.Trim();

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("频道标题不能为空", nameof(title));

        await _api.CallAsync(bot.Token, "setChatTitle", new Dictionary<string, string?>
        {
            ["chat_id"] = channelTelegramId.ToString(),
            ["title"] = title
        }, cancellationToken);

        await _api.CallAsync(bot.Token, "setChatDescription", new Dictionary<string, string?>
        {
            ["chat_id"] = channelTelegramId.ToString(),
            ["description"] = about ?? ""
        }, cancellationToken);

        return true;
    }

    public async Task<bool> SetChannelPhotoAsync(
        int botId,
        long channelTelegramId,
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        if (fileStream == null)
            throw new ArgumentException("头像文件为空", nameof(fileStream));

        fileName = (fileName ?? "photo.jpg").Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "photo.jpg";

        // Bot API setChatPhoto 需要 multipart 上传 InputFile。
        // 为了提高成功率：做“居中裁剪为正方形 + 缩放到 512x512 + JPEG 压缩”，避免原图过大/比例异常导致失败。
        await using var raw = new MemoryStream();
        if (fileStream.CanSeek)
            fileStream.Position = 0;
        await fileStream.CopyToAsync(raw, cancellationToken);
        raw.Position = 0;

        await using var encoded = new MemoryStream();
        try
        {
            using var image = await Image.LoadAsync(raw, cancellationToken);
            image.Mutate(x => x.AutoOrient());

            const int targetSize = 512;
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Crop,
                Size = new Size(targetSize, targetSize)
            }));

            await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 85 }, cancellationToken);
            encoded.Position = 0;
        }
        catch (UnknownImageFormatException)
        {
            throw new InvalidOperationException("不支持的图片格式（建议使用 JPG/PNG）");
        }

        _ = await _api.CallWithFileAsync(
            token: bot.Token,
            method: "setChatPhoto",
            parameters: new Dictionary<string, string?>
            {
                ["chat_id"] = channelTelegramId.ToString()
            },
            fileParameterName: "photo",
            fileStream: encoded,
            fileName: fileName,
            cancellationToken: cancellationToken);

        return true;
    }

    public async Task<int> PromoteChatMemberAsync(int botId, IReadOnlyList<long> channelTelegramIds, long userId, BotAdminRights rights, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        if (userId <= 0)
            throw new ArgumentException("userId 无效", nameof(userId));

        var botUserId = await GetBotUserIdAsync(bot.Token, cancellationToken);

        var ok = 0;
        foreach (var chatId in channelTelegramIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var (canPromote, reason) = await CanBotPromoteMembersAsync(bot.Token, chatId, botUserId, cancellationToken);
                if (!canPromote)
                    throw new InvalidOperationException(reason);

                var args = new Dictionary<string, string?>
                {
                    ["chat_id"] = chatId.ToString(),
                    ["user_id"] = userId.ToString(),
                    ["can_manage_chat"] = rights.ManageChat ? "true" : "false",
                    ["can_post_messages"] = rights.PostMessages ? "true" : "false",
                    ["can_edit_messages"] = rights.EditMessages ? "true" : "false",
                    ["can_delete_messages"] = rights.DeleteMessages ? "true" : "false",
                    ["can_invite_users"] = rights.InviteUsers ? "true" : "false",
                    ["can_restrict_members"] = rights.RestrictMembers ? "true" : "false",
                    ["can_pin_messages"] = rights.PinMessages ? "true" : "false",
                    ["can_promote_members"] = rights.PromoteMembers ? "true" : "false"
                };

                await _api.CallAsync(bot.Token, "promoteChatMember", args, cancellationToken);
                ok++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PromoteChatMember failed for bot {BotId} chat {ChatId} user {UserId}", botId, chatId, userId);
            }
        }

        return ok;
    }

    public async Task<IReadOnlyDictionary<long, string>> ExportInviteLinksAsync(int botId, IReadOnlyList<long> channelTelegramIds, CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, string>();
        foreach (var id in channelTelegramIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                map[id] = await ExportInviteLinkAsync(botId, id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ExportInviteLink failed for bot {BotId} chat {ChatId}", botId, id);
                map[id] = "(无法生成/不可见/无权限)";
            }
        }
        return map;
    }

    private static bool TryParseChatMemberUpdate(JsonElement myChatMember, out BotApiChat chat, out string status)
    {
        chat = default;
        status = string.Empty;

        if (myChatMember.ValueKind != JsonValueKind.Object)
            return false;

        if (!myChatMember.TryGetProperty("chat", out var chatEl) || chatEl.ValueKind != JsonValueKind.Object)
            return false;

        if (!chatEl.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var chatId))
            return false;

        var type = chatEl.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        var title = chatEl.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var username = chatEl.TryGetProperty("username", out var userEl) ? userEl.GetString() : null;

        if (!myChatMember.TryGetProperty("new_chat_member", out var newMember) || newMember.ValueKind != JsonValueKind.Object)
            return false;

        if (!newMember.TryGetProperty("status", out var statusEl))
            return false;

        status = statusEl.GetString() ?? string.Empty;
        chat = new BotApiChat(chatId, type ?? string.Empty, title, username);
        return true;
    }

    private readonly record struct BotChatMemberChange(BotApiChat Chat, string Status);
    private readonly record struct BotApiChat(long Id, string Type, string? Title, string? Username);

    public sealed record BotChatInfo(long TelegramId, string Type, string? Title, string? Username, string? Description, int? MemberCount);

    public sealed record BotChatAdminInfo(long UserId, string? Username, string? FirstName, string? LastName, string Status, string? CustomTitle)
    {
        public bool IsCreator => string.Equals(Status, "creator", StringComparison.OrdinalIgnoreCase);
        public string DisplayName
        {
            get
            {
                var name = $"{FirstName} {LastName}".Trim();
                return string.IsNullOrWhiteSpace(name) ? (Username ?? UserId.ToString()) : name;
            }
        }
    }

    public sealed record BotAdminRights(
        bool ManageChat,
        bool PostMessages,
        bool EditMessages,
        bool DeleteMessages,
        bool InviteUsers,
        bool RestrictMembers,
        bool PinMessages,
        bool PromoteMembers
    );

    public async Task<PromoteAdminsResult> PromoteChatMemberWithResultAsync(
        int botId,
        IReadOnlyList<long> channelTelegramIds,
        long userId,
        BotAdminRights rights,
        CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        if (userId <= 0)
            throw new ArgumentException("userId 无效", nameof(userId));

        var distinctIds = channelTelegramIds.Distinct().ToList();
        var failures = new Dictionary<long, string>();

        var botUserId = await GetBotUserIdAsync(bot.Token, cancellationToken);

        var ok = 0;
        foreach (var chatId in distinctIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var (canPromote, reason) = await CanBotPromoteMembersAsync(bot.Token, chatId, botUserId, cancellationToken);
                if (!canPromote)
                    throw new InvalidOperationException(reason);

                var args = new Dictionary<string, string?>
                {
                    ["chat_id"] = chatId.ToString(),
                    ["user_id"] = userId.ToString(),
                    ["can_manage_chat"] = rights.ManageChat ? "true" : "false",
                    ["can_post_messages"] = rights.PostMessages ? "true" : "false",
                    ["can_edit_messages"] = rights.EditMessages ? "true" : "false",
                    ["can_delete_messages"] = rights.DeleteMessages ? "true" : "false",
                    ["can_invite_users"] = rights.InviteUsers ? "true" : "false",
                    ["can_restrict_members"] = rights.RestrictMembers ? "true" : "false",
                    ["can_pin_messages"] = rights.PinMessages ? "true" : "false",
                    ["can_promote_members"] = rights.PromoteMembers ? "true" : "false"
                };

                await _api.CallAsync(bot.Token, "promoteChatMember", args, cancellationToken);
                ok++;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                failures[chatId] = msg;
                _logger.LogWarning(ex, "PromoteChatMember failed for bot {BotId} chat {ChatId} user {UserId}", botId, chatId, userId);
            }
        }

        return new PromoteAdminsResult(ok, distinctIds.Count, failures);
    }

    public sealed record PromoteAdminsResult(int SuccessCount, int TotalCount, IReadOnlyDictionary<long, string> Failures);

    private async Task<long> GetBotUserIdAsync(string token, CancellationToken cancellationToken)
    {
        var me = await _api.CallAsync(token, "getMe", new Dictionary<string, string?>(), cancellationToken);
        if (me.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Bot API getMe 返回格式异常");
        if (!me.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
            throw new InvalidOperationException("Bot API getMe 缺少 id");
        return id;
    }

    private async Task<(bool CanPromote, string Reason)> CanBotPromoteMembersAsync(string token, long chatId, long botUserId, CancellationToken cancellationToken)
    {
        var member = await _api.CallAsync(token, "getChatMember", new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["user_id"] = botUserId.ToString()
        }, cancellationToken);

        if (member.ValueKind != JsonValueKind.Object)
            return (false, "无法检测机器人权限（getChatMember 返回格式异常）");

        var status = member.TryGetProperty("status", out var statusEl) ? (statusEl.GetString() ?? "") : "";
        if (string.Equals(status, "creator", StringComparison.OrdinalIgnoreCase))
            return (true, "");

        if (!string.Equals(status, "administrator", StringComparison.OrdinalIgnoreCase))
            return (false, "机器人不是管理员（请先把机器人设为管理员）");

        var canPromote = ReadBool(member, "can_promote_members");
        if (!canPromote)
            return (false, "机器人缺少“添加管理员”权限（请在频道管理员设置里给机器人开启“添加管理员/添加新管理员”）");

        return (true, "");
    }

    private static bool ReadBool(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var el)
            && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            && el.GetBoolean();
    }

    /// <summary>
    /// 批量踢出/封禁频道成员
    /// </summary>
    /// <param name="permanentBan">true=永久封禁（无法再加入），false=仅踢出（可通过邀请链接重新加入）</param>
    public async Task<BanMembersResult> BanChatMemberAsync(
        int botId,
        IReadOnlyList<long> channelTelegramIds,
        long userId,
        bool permanentBan,
        CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        if (userId <= 0)
            throw new ArgumentException("userId 无效", nameof(userId));

        var distinctIds = channelTelegramIds.Distinct().ToList();
        var failures = new Dictionary<long, string>();

        var botUserId = await GetBotUserIdAsync(bot.Token, cancellationToken);

        var ok = 0;
        foreach (var chatId in distinctIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var (canBan, reason) = await CanBotBanMembersAsync(bot.Token, chatId, botUserId, cancellationToken);
                if (!canBan)
                    throw new InvalidOperationException(reason);

                if (permanentBan)
                {
                    // 永久封禁：调用 banChatMember（默认永久封禁）
                    var banArgs = new Dictionary<string, string?>
                    {
                        ["chat_id"] = chatId.ToString(),
                        ["user_id"] = userId.ToString(),
                        ["revoke_messages"] = "false" // 不撤回用户的历史消息
                    };
                    await _api.CallAsync(bot.Token, "banChatMember", banArgs, cancellationToken);
                }
                else
                {
                    // 仅踢出：先封禁再立即解封
                    var banArgs = new Dictionary<string, string?>
                    {
                        ["chat_id"] = chatId.ToString(),
                        ["user_id"] = userId.ToString(),
                        ["revoke_messages"] = "false"
                    };
                    await _api.CallAsync(bot.Token, "banChatMember", banArgs, cancellationToken);

                    var unbanArgs = new Dictionary<string, string?>
                    {
                        ["chat_id"] = chatId.ToString(),
                        ["user_id"] = userId.ToString()
                    };
                    await _api.CallAsync(bot.Token, "unbanChatMember", unbanArgs, cancellationToken);
                }

                ok++;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                failures[chatId] = msg;
                _logger.LogWarning(ex, "BanChatMember failed for bot {BotId} chat {ChatId} user {UserId} permanent {Permanent}", botId, chatId, userId, permanentBan);
            }
        }

        return new BanMembersResult(ok, distinctIds.Count, failures);
    }

    public sealed record BanMembersResult(int SuccessCount, int TotalCount, IReadOnlyDictionary<long, string> Failures);

    private async Task<(bool CanBan, string Reason)> CanBotBanMembersAsync(string token, long chatId, long botUserId, CancellationToken cancellationToken)
    {
        var member = await _api.CallAsync(token, "getChatMember", new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["user_id"] = botUserId.ToString()
        }, cancellationToken);

        if (member.ValueKind != JsonValueKind.Object)
            return (false, "无法检测机器人权限（getChatMember 返回格式异常）");

        var status = member.TryGetProperty("status", out var statusEl) ? (statusEl.GetString() ?? "") : "";
        if (string.Equals(status, "creator", StringComparison.OrdinalIgnoreCase))
            return (true, "");

        if (!string.Equals(status, "administrator", StringComparison.OrdinalIgnoreCase))
            return (false, "机器人不是管理员（请先把机器人设为管理员）");

        var canBan = ReadBool(member, "can_restrict_members");
        if (!canBan)
            return (false, "机器人缺少封禁用户权限（请在频道管理员设置里给机器人开启封禁用户权限）");

        return (true, "");
    }
}
