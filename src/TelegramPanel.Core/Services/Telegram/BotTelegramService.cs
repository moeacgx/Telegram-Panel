using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
/// - 兜底：手动对账（清理失效频道记录）
/// </summary>
public class BotTelegramService
{
    private readonly BotManagementService _botManagement;
    private readonly TelegramBotApiClient _api;
    private readonly BotUpdateHub _updateHub;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BotTelegramService> _logger;

    public BotTelegramService(
        BotManagementService botManagement,
        TelegramBotApiClient api,
        BotUpdateHub updateHub,
        IConfiguration configuration,
        ILogger<BotTelegramService> logger)
    {
        _botManagement = botManagement;
        _api = api;
        _updateHub = updateHub;
        _configuration = configuration;
        _logger = logger;
    }

    public sealed record BotChannelSyncResult(int AppliedUpdates, int RemovedStale);
    public sealed record ChannelStatusCheckResult(int SuccessCount, int FailedCount, int TotalCount, IReadOnlyDictionary<long, string> Failures);

    /// <summary>
    /// 手动同步（新增 + 清理）：
    /// - 新增/移除：尽力从 <see cref="BotUpdateHub"/> 拉取并应用 my_chat_member updates（回放/增量）
    /// - 清理：对本地已记录频道做一次权限核验，移除 Bot 已被撤权/踢出的频道记录
    ///
    /// 说明：Telegram Bot API 无法直接“枚举 Bot 当前所在的所有频道”，因此仍以更新队列为新增来源。
    /// </summary>
    public async Task<BotChannelSyncResult> SyncBotChannelsAsync(int botId, CancellationToken cancellationToken)
    {
        if (botId <= 0)
            throw new ArgumentException("botId 无效", nameof(botId));

        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        var applied = await DrainAndApplyMyChatMemberUpdatesAsync(botId, cancellationToken);

        var channels = (await _botManagement.GetChannelsAsync(botId)).ToList();
        if (channels.Count == 0)
        {
            bot.LastSyncAt = DateTime.UtcNow;
            await _botManagement.UpdateBotAsync(bot);
            return new BotChannelSyncResult(AppliedUpdates: applied, RemovedStale: 0);
        }

        var botUserId = await GetBotUserIdAsync(bot.Token, cancellationToken);

        var removed = 0;
        foreach (var ch in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var status = await GetBotMemberStatusAsync(bot.Token, ch.TelegramId, botUserId, cancellationToken);
                if (string.Equals(status, "creator", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "administrator", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await _botManagement.DeleteChannelByTelegramIdAsync(bot.Id, ch.TelegramId);
                removed++;
            }
            catch (InvalidOperationException ex) when (TryGetRetryAfterSeconds(ex.Message, out var seconds))
            {
                // 429：按提示退避（不重试当前 chat，避免长时间卡住页面）
                _logger.LogWarning("Bot manual sync hit rate limit, retry after {Seconds}s", seconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
            catch (InvalidOperationException ex) when (IsStaleChannelError(ex.Message))
            {
                // Bot 已被踢/频道不存在等：清理本地记录
                await _botManagement.DeleteChannelByTelegramIdAsync(bot.Id, ch.TelegramId);
                removed++;
            }
        }

        bot.LastSyncAt = DateTime.UtcNow;
        await _botManagement.UpdateBotAsync(bot);

        if (removed > 0)
            _logger.LogInformation("Bot manual sync completed: removed {Removed}/{Total} stale channels (botId={BotId})", removed, channels.Count, botId);
        else
            _logger.LogInformation("Bot manual sync completed: no stale channels removed (botId={BotId})", botId);

        if (applied > 0)
            _logger.LogInformation("Bot manual sync applied {Applied} my_chat_member updates (botId={BotId})", applied, botId);

        return new BotChannelSyncResult(AppliedUpdates: applied, RemovedStale: removed);
    }

    private async Task<int> DrainAndApplyMyChatMemberUpdatesAsync(int botId, CancellationToken cancellationToken)
    {
        // 通过 BotUpdateHub 共享单一 getUpdates 轮询，避免 409 Conflict
        await using var sub = await _updateHub.SubscribeAsync(botId, cancellationToken);

        var applied = 0;
        var batch = new List<JsonElement>(256);

        // poller 启动时会有短暂延迟 + getUpdates 长轮询，因此这里给足时间（避免“点同步就结束了但更新还没拉到”）。
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var drainedAny = false;
            while (sub.Reader.TryRead(out var update))
            {
                drainedAny = true;

                if (update.ValueKind == JsonValueKind.Object && update.TryGetProperty("my_chat_member", out _))
                {
                    batch.Add(update);
                    if (batch.Count >= 200)
                    {
                        applied += await ApplyMyChatMemberUpdatesAsync(botId, batch, cancellationToken);
                        batch.Clear();
                    }
                }
            }

            if (!drainedAny)
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        if (batch.Count > 0)
            applied += await ApplyMyChatMemberUpdatesAsync(botId, batch, cancellationToken);

        return applied;
    }

    private async Task<string?> GetBotMemberStatusAsync(string token, long chatId, long botUserId, CancellationToken cancellationToken)
    {
        var member = await _api.CallAsync(token, "getChatMember", new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["user_id"] = botUserId.ToString()
        }, cancellationToken);

        if (member.ValueKind != JsonValueKind.Object)
            return null;

        return member.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
    }

    private static bool IsStaleChannelError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var m = message.ToLowerInvariant();

        // 典型：bot 被踢/不在频道里/频道不存在
        if (m.Contains("bot was kicked"))
            return true;
        if (m.Contains("bot is not a member"))
            return true;
        if (m.Contains("chat not found"))
            return true;
        if (m.Contains("channel not found"))
            return true;

        return false;
    }

    private static bool TryGetRetryAfterSeconds(string message, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // Telegram: "Too Many Requests: retry after 5"
        var idx = message.IndexOf("retry after", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        var tail = message.Substring(idx + "retry after".Length).Trim();
        var num = new string(tail.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(num, out seconds))
            return false;

        if (seconds < 1) seconds = 1;
        if (seconds > 120) seconds = 120;
        return true;
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue)
    {
        var raw = (configuration[key] ?? "").Trim();
        return int.TryParse(raw, out var v) ? v : defaultValue;
    }

    /// <summary>
    /// 应用一批 my_chat_member updates，把 Bot 新加入/移除/升降权的频道写入数据库。
    /// </summary>
    public async Task<int> ApplyMyChatMemberUpdatesAsync(int botId, IReadOnlyList<JsonElement> updates, CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        // Telegram 会针对同一个 chat 连续产生多条 my_chat_member（例如 member -> administrator），
        // 这里按 chat_id 去重并只应用“最后一次状态”，避免“新增两个”的错觉/重复写库。
        var changesByChatId = new Dictionary<long, BotChatMemberChange>();

        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (update.ValueKind != JsonValueKind.Object)
                continue;

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
                await _botManagement.UpsertChannelAsync(bot.Id, new BotChannel
                {
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
            var canInviteUsers = ReadBool(el, "can_invite_users");
            var canPromoteMembers = ReadBool(el, "can_promote_members");
            var canRestrictMembers = ReadBool(el, "can_restrict_members");

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
                CustomTitle: string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim(),
                CanInviteUsers: canInviteUsers,
                CanPromoteMembers: canPromoteMembers,
                CanRestrictMembers: canRestrictMembers
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

        try
        {
            await _api.CallAsync(bot.Token, "setChatTitle", new Dictionary<string, string?>
            {
                ["chat_id"] = channelTelegramId.ToString(),
                ["title"] = title
            }, cancellationToken);
        }
        catch (Exception ex) when (IsBotApiNotModified(ex, "setChatTitle"))
        {
            // ignore: title not modified
        }

        try
        {
            await _api.CallAsync(bot.Token, "setChatDescription", new Dictionary<string, string?>
            {
                ["chat_id"] = channelTelegramId.ToString(),
                ["description"] = about ?? ""
            }, cancellationToken);
        }
        catch (Exception ex) when (IsBotApiNotModified(ex, "setChatDescription"))
        {
            // ignore: description not modified
        }

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

        await using var encoded = await TelegramImageProcessor.PrepareAvatarJpegAsync(fileStream, cancellationToken);

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

    private static bool IsBotApiNotModified(Exception ex, string method)
    {
        // Telegram Bot API 会在“内容未修改”时返回 400：
        // - setChatTitle: "Bad Request: chat title is not modified"
        // - setChatDescription: "Bad Request: chat description is not modified"
        // 这类情况不应该阻塞后续操作（例如继续设置头像）。
        if (ex is not InvalidOperationException)
            return false;

        var msg = ex.Message ?? string.Empty;
        if (!msg.Contains($"Bot API 调用失败：{method} (400)", StringComparison.OrdinalIgnoreCase))
            return false;

        return msg.Contains("is not modified", StringComparison.OrdinalIgnoreCase);
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

                var targetStatus = await GetChatMemberStatusAsync(bot.Token, chatId, userId, cancellationToken);
                if (string.Equals(targetStatus, "left", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetStatus, "kicked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("目标未加入该频道/群，无法设置管理员（请先把目标加入聊天后再设置管理员）");
                }

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

    /// <summary>
    /// 批量检测 Bot 在频道中的可操作状态：
    /// 通过 sendMessage 发送探测消息并立即 deleteMessage；发送失败则记为异常。
    /// </summary>
    public async Task<ChannelStatusCheckResult> CheckChannelsStatusAsync(
        int botId,
        IReadOnlyList<long> channelTelegramIds,
        CancellationToken cancellationToken)
    {
        var bot = await _botManagement.GetBotAsync(botId)
            ?? throw new InvalidOperationException($"机器人不存在：{botId}");

        if (!bot.IsActive)
            throw new InvalidOperationException("该机器人已停用");

        var ids = channelTelegramIds
            .Where(x => x != 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new ChannelStatusCheckResult(0, 0, 0, new Dictionary<long, string>());

        var failures = new Dictionary<long, string>();

        var delayMs = ReadInt(_configuration, "Telegram:DefaultDelayMs", 1000);
        if (delayMs < 0) delayMs = 0;
        if (delayMs > 60000) delayMs = 60000;

        var maxRetries = ReadInt(_configuration, "Telegram:MaxRetries", 1);
        if (maxRetries < 0) maxRetries = 0;
        if (maxRetries > 5) maxRetries = 5;

        var ok = 0;
        for (var index = 0; index < ids.Count; index++)
        {
            var chatId = ids[index];
            cancellationToken.ThrowIfCancellationRequested();

            var attempt = 0;
            while (true)
            {
                try
                {
                    await ProbeChannelBySendDeleteAsync(bot.Token, chatId, cancellationToken);
                    await _botManagement.UpdateChannelStatusAsync(bot.Id, chatId, ok: true, error: null, checkedAtUtc: DateTime.UtcNow);
                    ok++;
                    break;
                }
                catch (InvalidOperationException ex) when (TryGetRetryAfterSeconds(ex.Message, out var seconds))
                {
                    attempt++;
                    if (attempt > maxRetries)
                    {
                        var msg = string.IsNullOrWhiteSpace(ex.Message) ? "检测失败（限流重试超限）" : ex.Message.Trim();
                        failures[chatId] = msg;
                        await _botManagement.UpdateChannelStatusAsync(bot.Id, chatId, ok: false, error: msg, checkedAtUtc: DateTime.UtcNow);
                        _logger.LogWarning(
                            "CheckChannelsStatus retry exceeded: bot={BotId} chat={ChatId} retryAfter={RetryAfter}s",
                            botId,
                            chatId,
                            seconds);
                        break;
                    }

                    _logger.LogWarning(
                        "CheckChannelsStatus hit rate limit, retry after {Seconds}s (attempt {Attempt}/{Max}): bot={BotId} chat={ChatId}",
                        seconds,
                        attempt,
                        maxRetries,
                        botId,
                        chatId);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
                }
                catch (Exception ex)
                {
                    var msg = string.IsNullOrWhiteSpace(ex.Message) ? "检测失败" : ex.Message.Trim();
                    failures[chatId] = msg;
                    await _botManagement.UpdateChannelStatusAsync(bot.Id, chatId, ok: false, error: msg, checkedAtUtc: DateTime.UtcNow);
                    _logger.LogWarning(ex, "CheckChannelsStatus failed: bot={BotId} chat={ChatId}", botId, chatId);
                    break;
                }
            }

            if (delayMs > 0 && index < ids.Count - 1)
            {
                var jitter = Random.Shared.Next(0, Math.Min(300, delayMs + 1));
                await Task.Delay(delayMs + jitter, cancellationToken);
            }
        }

        return new ChannelStatusCheckResult(ok, failures.Count, ids.Count, failures);
    }

    private async Task ProbeChannelBySendDeleteAsync(string token, long chatId, CancellationToken cancellationToken)
    {
        var probeText = $"频道状态检测 {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
        var sendResult = await _api.CallAsync(token, "sendMessage", new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["text"] = probeText,
            ["disable_notification"] = "true"
        }, cancellationToken);

        if (sendResult.ValueKind != JsonValueKind.Object
            || !sendResult.TryGetProperty("message_id", out var messageIdEl)
            || !messageIdEl.TryGetInt64(out var messageId))
        {
            throw new InvalidOperationException("Bot API sendMessage 返回格式异常，无法完成频道检测");
        }

        try
        {
            await _api.CallAsync(token, "deleteMessage", new Dictionary<string, string?>
            {
                ["chat_id"] = chatId.ToString(),
                ["message_id"] = messageId.ToString()
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // 删除失败不判定为“频道异常”，但记录日志便于排查（例如 Bot 缺少删消息权限）。
            _logger.LogWarning(ex, "Probe deleteMessage failed: chat={ChatId}", chatId);
        }
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

    public sealed record BotChatAdminInfo(
        long UserId,
        string? Username,
        string? FirstName,
        string? LastName,
        string Status,
        string? CustomTitle,
        bool CanInviteUsers,
        bool CanPromoteMembers,
        bool CanRestrictMembers)
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
        bool ChangeInfo,
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

                var targetStatus = await GetChatMemberStatusAsync(bot.Token, chatId, userId, cancellationToken);
                if (string.Equals(targetStatus, "left", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(targetStatus, "kicked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("目标未加入该频道/群，无法设置管理员（请先把目标加入聊天后再设置管理员）");
                }

                var args = new Dictionary<string, string?>
                {
                    ["chat_id"] = chatId.ToString(),
                    ["user_id"] = userId.ToString(),
                    ["can_manage_chat"] = rights.ManageChat ? "true" : "false",
                    ["can_change_info"] = rights.ChangeInfo ? "true" : "false",
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
                if (IsChatAdminInviteRequiredError(msg))
                {
                    msg = "目标未加入该频道/群（或需要管理员邀请加入），无法设置管理员：请先把目标加入聊天后再设置管理员";
                }
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
        CancellationToken cancellationToken,
        Func<long, string?, int, int, Task>? perChatCallback = null)
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

        var delayMs = ReadInt(_configuration, "Telegram:DefaultDelayMs", 2000);
        if (delayMs < 0) delayMs = 0;
        if (delayMs > 60000) delayMs = 60000;

        var maxRetries = ReadInt(_configuration, "Telegram:MaxRetries", 0);
        if (maxRetries < 0) maxRetries = 0;
        if (maxRetries > 10) maxRetries = 10;

        var ok = 0;
        for (var index = 0; index < distinctIds.Count; index++)
        {
            var chatId = distinctIds[index];
            cancellationToken.ThrowIfCancellationRequested();

            var attempt = 0;
            while (true)
            {
                try
                {
                    var (canBan, reason) = await CanBotBanMembersAsync(bot.Token, chatId, botUserId, cancellationToken);
                    if (!canBan)
                        throw new InvalidOperationException(reason);

                    // 目标如果是管理员：Telegram 不允许直接 ban/kick 管理员，需先取消管理员再踢出
                    var targetStatus = await GetChatMemberStatusAsync(bot.Token, chatId, userId, cancellationToken);
                    if (string.Equals(targetStatus, "creator", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("目标是频道创建者，无法踢出/封禁");

                    if (string.Equals(targetStatus, "administrator", StringComparison.OrdinalIgnoreCase))
                    {
                        var (canPromote, promoteReason) = await CanBotPromoteMembersAsync(bot.Token, chatId, botUserId, cancellationToken);
                        if (!canPromote)
                            throw new InvalidOperationException($"目标是管理员，且无法取消管理员权限：{promoteReason}");

                        await DemoteChatMemberAsync(bot.Token, chatId, userId, cancellationToken);
                    }

                    try
                    {
                        await BanOrKickInternalAsync(bot.Token, chatId, userId, permanentBan, cancellationToken);
                    }
                    catch (InvalidOperationException ex) when (IsUserIsAdministratorError(ex.Message))
                    {
                        // 兜底：即使前面没识别到/状态变化，也尝试“先降权再踢”
                        var (canPromote, promoteReason) = await CanBotPromoteMembersAsync(bot.Token, chatId, botUserId, cancellationToken);
                        if (!canPromote)
                            throw new InvalidOperationException($"目标是管理员，且无法取消管理员权限：{promoteReason}");

                        await DemoteChatMemberAsync(bot.Token, chatId, userId, cancellationToken);
                        await BanOrKickInternalAsync(bot.Token, chatId, userId, permanentBan, cancellationToken);
                    }

                    ok++;
                    break;
                }
                catch (InvalidOperationException ex) when (TryGetRetryAfterSeconds(ex.Message, out var seconds))
                {
                    attempt++;
                    if (attempt > maxRetries)
                    {
                        failures[chatId] = ex.Message;
                        _logger.LogWarning(
                            "BanChatMember hit rate limit but retries exceeded: bot={BotId} chat={ChatId} user={UserId} retryAfter={Seconds}s",
                            botId,
                            chatId,
                            userId,
                            seconds);
                        break;
                    }

                    _logger.LogWarning(
                        "BanChatMember hit rate limit, retry after {Seconds}s (attempt {Attempt}/{Max}): bot={BotId} chat={ChatId} user={UserId}",
                        seconds,
                        attempt,
                        maxRetries,
                        botId,
                        chatId,
                        userId);

                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
                }
                catch (Exception ex)
                {
                    failures[chatId] = ex.Message;
                    _logger.LogWarning(
                        "BanChatMember failed: bot={BotId} chat={ChatId} user={UserId} permanent={Permanent} error={Error}",
                        botId,
                        chatId,
                        userId,
                        permanentBan,
                        ex.Message);
                    _logger.LogDebug(ex, "BanChatMember debug details: bot={BotId} chat={ChatId} user={UserId}", botId, chatId, userId);
                    break;
                }
            }

            if (perChatCallback != null)
            {
                failures.TryGetValue(chatId, out var err);
                await perChatCallback(chatId, err, ok, failures.Count);
            }

            // 降速：避免 Bot API 429
            if (delayMs > 0 && index < distinctIds.Count - 1)
            {
                var jitter = Random.Shared.Next(0, Math.Min(500, delayMs + 1));
                await Task.Delay(delayMs + jitter, cancellationToken);
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

    private static bool IsUserIsAdministratorError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // 典型：Bot API 调用失败：banChatMember (400) Bad Request: user is an administrator of the chat
        return message.IndexOf("administrator of the chat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsChatAdminInviteRequiredError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        // 典型：Bot API 调用失败：promoteChatMember (400) Bad Request: CHAT_ADMIN_INVITE_REQUIRED
        return message.IndexOf("CHAT_ADMIN_INVITE_REQUIRED", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task<string?> GetChatMemberStatusAsync(string token, long chatId, long userId, CancellationToken cancellationToken)
    {
        var member = await _api.CallAsync(token, "getChatMember", new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["user_id"] = userId.ToString()
        }, cancellationToken);

        if (member.ValueKind != JsonValueKind.Object)
            return null;

        return member.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
    }

    private async Task DemoteChatMemberAsync(string token, long chatId, long userId, CancellationToken cancellationToken)
    {
        // promoteChatMember：把所有管理员权限设为 false，即可取消管理员身份（demote）
        var args = new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["user_id"] = userId.ToString(),
            ["is_anonymous"] = "false",
            ["can_manage_chat"] = "false",
            ["can_change_info"] = "false",
            ["can_post_messages"] = "false",
            ["can_edit_messages"] = "false",
            ["can_delete_messages"] = "false",
            ["can_invite_users"] = "false",
            ["can_restrict_members"] = "false",
            ["can_pin_messages"] = "false",
            ["can_promote_members"] = "false",
            ["can_manage_video_chats"] = "false",
            ["can_manage_topics"] = "false",
            ["can_post_stories"] = "false",
            ["can_edit_stories"] = "false",
            ["can_delete_stories"] = "false"
        };

        await _api.CallAsync(token, "promoteChatMember", args, cancellationToken);
    }

    private async Task BanOrKickInternalAsync(string token, long chatId, long userId, bool permanentBan, CancellationToken cancellationToken)
    {
        if (permanentBan)
        {
            // 永久封禁：调用 banChatMember（默认永久封禁）
            var banArgs = new Dictionary<string, string?>
            {
                ["chat_id"] = chatId.ToString(),
                ["user_id"] = userId.ToString(),
                ["revoke_messages"] = "false" // 不撤回用户的历史消息
            };
            await _api.CallAsync(token, "banChatMember", banArgs, cancellationToken);
            return;
        }

        // 仅踢出：先封禁再立即解封
        var kickArgs = new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["user_id"] = userId.ToString(),
            ["revoke_messages"] = "false"
        };
        await _api.CallAsync(token, "banChatMember", kickArgs, cancellationToken);

        var unbanArgs = new Dictionary<string, string?>
        {
            ["chat_id"] = chatId.ToString(),
            ["user_id"] = userId.ToString()
        };
        await _api.CallAsync(token, "unbanChatMember", unbanArgs, cancellationToken);
    }
}
