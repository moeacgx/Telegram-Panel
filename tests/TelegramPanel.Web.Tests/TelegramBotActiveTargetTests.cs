using TelegramPanel.Core.Services.Telegram;
using TL;
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

    [Theory]
    [InlineData("https://t.me/channelname/123", "channelname", 123)]
    [InlineData("t.me/channelname/456?single", "channelname", 456)]
    [InlineData("https://t.me/c/1234567890/789", "-1001234567890", 789)]
    [InlineData("https://t.me/c/1234567890/11/12", "-1001234567890", 12)]
    public void 消息链接解析支持公开和私有消息链接(string input, string expectedSource, int expectedMessageId)
    {
        Assert.True(AccountTelegramToolsService.TryParseTelegramMessageReference(input, out var reference, out var error), error);
        Assert.NotNull(reference);
        Assert.Equal(expectedSource, reference!.SourceTarget);
        Assert.Equal(expectedMessageId, reference.MessageId);
    }

    [Theory]
    [InlineData("https://t.me/channelname")]
    [InlineData("https://example.com/channel/123")]
    public void 消息链接解析拒绝缺少消息Id或非Telegram链接(string input)
    {
        Assert.False(AccountTelegramToolsService.TryParseTelegramMessageReference(input, out var reference, out var error));
        Assert.Null(reference);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void 最新消息作者识别优先使用出站标记()
    {
        var message = new Message
        {
            flags = Message.Flags.out_,
            from_id = new PeerUser { user_id = 999 }
        };

        Assert.True(AccountTelegramToolsService.IsMessageFromCurrentAccount(message, accountUserId: 123));
    }

    [Fact]
    public void 最新消息作者识别支持当前账号UserId()
    {
        var message = new Message
        {
            from_id = new PeerUser { user_id = 123 }
        };

        Assert.True(AccountTelegramToolsService.IsMessageFromCurrentAccount(message, accountUserId: 123));
    }

    [Fact]
    public void 最新消息作者识别会拒绝其他账号消息()
    {
        var message = new Message
        {
            from_id = new PeerUser { user_id = 456 }
        };

        Assert.False(AccountTelegramToolsService.IsMessageFromCurrentAccount(message, accountUserId: 123));
    }
}
