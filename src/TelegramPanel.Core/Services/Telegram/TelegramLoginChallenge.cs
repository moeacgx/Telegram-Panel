namespace TelegramPanel.Core.Services.Telegram;

internal static class TelegramLoginChallenge
{
    internal const string Message = "Telegram 要求在官方客户端完成人机验证，面板无法代为完成。请在官方 Telegram 客户端中按提示完成验证或注册，再返回面板登录；请勿连续重发验证码。";

    internal static bool IsRequired(string? message) =>
        message?.Contains("RECAPTCHA_CHECK_", StringComparison.OrdinalIgnoreCase) == true;
}
