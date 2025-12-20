using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.ExternalApi;

public static class KickApi
{
    public static void MapKickApi(this WebApplication app)
    {
        app.MapPost("/api/kick", HandleAsync)
            .DisableAntiforgery()
            .AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        KickRequest request,
        IConfiguration configuration,
        BotManagementService botManagement,
        BotTelegramService botTelegram,
        CancellationToken cancellationToken)
    {
        var providedKey = http.Request.Headers["X-API-Key"].ToString();

        var apis = configuration.GetSection("ExternalApi:Apis").Get<List<ExternalApiDefinition>>() ?? new List<ExternalApiDefinition>();
        var kickApis = apis.Where(a => string.Equals(a.Type, ExternalApiTypes.Kick, StringComparison.OrdinalIgnoreCase)).ToList();
        var matched = kickApis.FirstOrDefault(a => a.Enabled && FixedTimeEquals(a.ApiKey, providedKey));
        if (matched == null)
        {
            // 未配置任何启用的 kick API：隐藏端点
            if (kickApis.All(a => !a.Enabled))
                return Results.NotFound();
            return Results.Unauthorized();
        }

        if (request.UserId <= 0)
            return Results.BadRequest(new KickResponse(false, "user_id 无效", new KickSummary(0, 0, 0), Array.Empty<KickResultItem>()));

        var permanentBan = request.PermanentBan ?? matched.Kick.PermanentBanDefault;
        var configuredBotId = matched.Kick.BotId;
        var useAllChats = configuredBotId == 0 ? true : matched.Kick.UseAllChats;
        var configuredChatSet = new HashSet<long>(matched.Kick.ChatIds.Where(x => x != 0));

        var targets = await ResolveTargetsAsync(botManagement, configuredBotId, useAllChats, configuredChatSet, cancellationToken);
        if (targets.TotalChats == 0)
        {
            return Results.BadRequest(new KickResponse(
                false,
                "未配置任何可操作的频道/群组（请先在页面选择机器人与频道/群组）",
                new KickSummary(0, 0, 0),
                Array.Empty<KickResultItem>()));
        }

        var results = new List<KickResultItem>(targets.TotalChats);

        foreach (var group in targets.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var banResult = await botTelegram.BanChatMemberAsync(
                botId: group.Bot.Id,
                channelTelegramIds: group.Chats.Select(x => x.TelegramId).ToList(),
                userId: request.UserId,
                permanentBan: permanentBan,
                cancellationToken: cancellationToken);

            var failures = banResult.Failures;
            foreach (var chat in group.Chats)
            {
                failures.TryGetValue(chat.TelegramId, out var err);
                results.Add(new KickResultItem(
                    ChatId: chat.TelegramId.ToString(),
                    Title: chat.Title,
                    Success: err == null,
                    Error: err));
            }
        }

        var okCount = results.Count(x => x.Success);
        var total = results.Count;
        var failed = total - okCount;
        var actionText = permanentBan ? "Banned" : "Kicked";
        var message = $"{actionText} user {request.UserId} from {okCount}/{total} chats";

        return Results.Ok(new KickResponse(
            Success: true,
            Message: message,
            Summary: new KickSummary(total, okCount, failed),
            Results: results));
    }

    private static async Task<TargetResolution> ResolveTargetsAsync(
        BotManagementService botManagement,
        int configuredBotId,
        bool useAllChats,
        HashSet<long> configuredChatIds,
        CancellationToken cancellationToken)
    {
        var bots = (await botManagement.GetAllBotsAsync())
            .Where(b => b.IsActive)
            .OrderBy(b => b.Id)
            .ToList();

        if (configuredBotId > 0)
            bots = bots.Where(b => b.Id == configuredBotId).ToList();

        var groups = new List<TargetGroup>();
        var totalChats = 0;

        foreach (var bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chats = (await botManagement.GetChatsAsync(bot.Id)).ToList();
            if (!useAllChats && configuredChatIds.Count > 0)
                chats = chats.Where(c => configuredChatIds.Contains(c.TelegramId)).ToList();

            if (chats.Count == 0)
                continue;

            groups.Add(new TargetGroup(bot, chats));
            totalChats += chats.Count;
        }

        return new TargetResolution(groups, totalChats);
    }

    private static bool FixedTimeEquals(string expected, string provided)
    {
        expected = (expected ?? string.Empty).Trim();
        provided = (provided ?? string.Empty).Trim();

        if (expected.Length == 0 || provided.Length == 0)
            return false;

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        if (a.Length != b.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public sealed record KickRequest(
        [property: JsonPropertyName("user_id")] long UserId,
        [property: JsonPropertyName("permanent_ban")] bool? PermanentBan = null);

    public sealed record KickResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("summary")] KickSummary Summary,
        [property: JsonPropertyName("results")] IReadOnlyList<KickResultItem> Results);

    public sealed record KickSummary(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("success")] int Success,
        [property: JsonPropertyName("failed")] int Failed);

    public sealed record KickResultItem(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record TargetGroup(Bot Bot, List<BotChannel> Chats);
    private sealed record TargetResolution(List<TargetGroup> Groups, int TotalChats);
}
