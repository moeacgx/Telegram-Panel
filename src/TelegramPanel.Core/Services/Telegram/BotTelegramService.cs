using System.Text.Json;
using Microsoft.Extensions.Logging;
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
}
