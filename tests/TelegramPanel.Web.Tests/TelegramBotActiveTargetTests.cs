using TelegramPanel.Core.Services.Telegram;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramBotActiveTargetTests
{
    [Theory]
    [InlineData("@examplebot", "examplebot")]
    [InlineData("examplebot", "examplebot")]
    [InlineData("https://t.me/examplebot?start=abc", "examplebot")]
    [InlineData("https://t.me/SpamBot", "SpamBot")]
    [InlineData("t.me/examplebot?start=abc", "examplebot")]
    [InlineData("tg://resolve?domain=examplebot&start=abc", "examplebot")]
    public void Bot活跃目标识别支持常见Bot链接(string input, string expected)
    {
        Assert.True(AccountTelegramToolsService.TryNormalizeTelegramBotUsername(input, out var username));
        Assert.Equal(expected, username);
    }

    [Theory]
    [InlineData("@regularuser")]
    [InlineData("https://t.me/+inviteHash")]
    [InlineData("-1001234567890")]
    [InlineData("https://t.me/channelname")]
    public void 非Bot目标不会被抢先按Bot私聊解析(string input)
    {
        Assert.False(AccountTelegramToolsService.TryNormalizeTelegramBotUsername(input, out _));
    }
}
