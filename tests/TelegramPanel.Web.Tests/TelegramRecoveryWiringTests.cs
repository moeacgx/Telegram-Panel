using System.Text.RegularExpressions;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class TelegramRecoveryWiringTests
{
    private static readonly string ServiceSource = File.ReadAllText(Path.Combine(
        TestRepositoryRoot.Find(),
        "src",
        "TelegramPanel.Core",
        "Services",
        "Telegram",
        "AccountTelegramToolsService.cs"));
    private static readonly string UserChatActiveHandlerSource = File.ReadAllText(Path.Combine(
        TestRepositoryRoot.Find(),
        "src",
        "TelegramPanel.Web",
        "Services",
        "UserChatActiveTaskHandler.cs"));

    [Fact]
    public void 状态刷新目标解析和任务入口均接入瞬时连接重试()
    {
        Assert.Matches(
            "RefreshAccountStatusAsync\\([\\s\\S]*?TelegramTransientConnectionRetry\\.ExecuteAsync[\\s\\S]*?private async Task TryPersistStatusAsync",
            ServiceSource);
        Assert.Matches(
            "ResolveChatTargetAsync\\([\\s\\S]*?TelegramTransientConnectionRetry\\.ExecuteAsync[\\s\\S]*?SendMessageToResolvedChatAsync",
            ServiceSource);
        Assert.Matches(
            "JoinChatOrChannelAsync\\([\\s\\S]*?TelegramTransientConnectionRetry\\.ExecuteAsync[\\s\\S]*?LeaveChatOrChannelAsync",
            ServiceSource);
        Assert.Matches(
            "StartExternalBotAsync\\([\\s\\S]*?TelegramTransientConnectionRetry\\.ExecuteAsync[\\s\\S]*?StopExternalBotAsync",
            ServiceSource);
        Assert.Matches(
            "StopExternalBotAsync\\([\\s\\S]*?TelegramTransientConnectionRetry\\.ExecuteAsync[\\s\\S]*?public sealed record ResolvedChatTarget",
            ServiceSource);
    }

    [Fact]
    public void 账号活跃目标解析先识别Bot私聊再回退群组频道()
    {
        Assert.Matches(
            "TryNormalizeTelegramBotTarget\\(raw[\\s\\S]*?TryResolveBotChatTargetAsync[\\s\\S]*?client\\.AnalyzeInviteLink",
            ServiceSource);
    }

    [Fact]
    public void Bot首次启动检查发生在文字和转发发送之前()
    {
        var startup = UserChatActiveHandlerSource.IndexOf(
            "botStartTracker.EnsureReadyAsync",
            StringComparison.Ordinal);
        var startupFailure = UserChatActiveHandlerSource.IndexOf(
            "if (!startup.Success)",
            startup,
            StringComparison.Ordinal);
        var forward = UserChatActiveHandlerSource.IndexOf(
            "accountTools.ForwardMessageToResolvedChatAsync",
            StringComparison.Ordinal);
        var image = UserChatActiveHandlerSource.IndexOf(
            "accountTools.SendPhotoToResolvedChatAsync",
            StringComparison.Ordinal);
        var text = UserChatActiveHandlerSource.IndexOf(
            "accountTools.SendMessageToResolvedChatAsync",
            StringComparison.Ordinal);

        Assert.True(startup >= 0);
        Assert.True(startupFailure > startup);
        Assert.True(forward > startupFailure);
        Assert.True(image > startupFailure);
        Assert.True(text > startupFailure);
        Assert.Contains("accountTools.StartExternalBotAsync", UserChatActiveHandlerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Bot启动的普通与深链请求在重试中复用同一随机标识()
    {
        var start = ServiceSource.IndexOf(
            "public async Task<(bool Success, string? Error, string? BotUsername)> StartExternalBotAsync",
            StringComparison.Ordinal);
        var nextMethod = ServiceSource.IndexOf(
            "public async Task<(bool Success, string? Error, string? BotUsername)> StopExternalBotAsync",
            start,
            StringComparison.Ordinal);
        var method = ServiceSource[start..nextMethod];
        var requestId = method.IndexOf("var randomId = Random.Shared.NextInt64()", StringComparison.Ordinal);
        var retry = method.IndexOf("TelegramTransientConnectionRetry.ExecuteAsync", StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(nextMethod > start);
        Assert.True(requestId >= 0);
        Assert.True(retry > requestId);
        Assert.Contains("client.Messages_SendMessage(", method, StringComparison.Ordinal);
        Assert.Contains("client.Messages_StartBot(", method, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(method, "random_id: randomId").Count);
    }

    [Fact]
    public void 目标解析的Telegram读取均有超时与取消边界()
    {
        Assert.Matches(
            "var chat = await ExecuteTelegramRequestAsync\\([\\s\\S]*?client\\.AnalyzeInviteLink",
            ServiceSource);
        Assert.Matches(
            "var dialogs = await ExecuteTelegramRequestAsync\\([\\s\\S]*?client\\.Messages_GetAllDialogs",
            ServiceSource);
    }

    [Fact]
    public void 目标解析在调用方取消时传播取消而不是普通失败()
    {
        Assert.Contains(
            "catch (Exception) when (cancellationToken.IsCancellationRequested)",
            ServiceSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "throw new OperationCanceledException(cancellationToken);",
            ServiceSource,
            StringComparison.Ordinal);
    }
}
