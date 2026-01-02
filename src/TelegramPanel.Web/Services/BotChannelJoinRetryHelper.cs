using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

public static class BotChannelJoinRetryHelper
{
    public static bool LooksLikeChannelNotFound(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Channel", StringComparison.OrdinalIgnoreCase)
               && message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> TryJoinChannelAsync(
        BotTelegramService botTelegram,
        AccountTelegramToolsService accountTools,
        int botId,
        int accountId,
        BotChannel channel,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            var link = await botTelegram.ExportInviteLinkAsync(botId, channel.TelegramId, cancellationToken);
            var (success, error, _) = await accountTools.JoinChatOrChannelAsync(accountId, link, cancellationToken);
            if (!success)
            {
                failures.Add($"{channel.Title}：执行账号尝试加入频道失败：{error}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failures.Add($"{channel.Title}：执行账号尝试加入频道失败：{ex.Message}");
            return false;
        }
    }
}

