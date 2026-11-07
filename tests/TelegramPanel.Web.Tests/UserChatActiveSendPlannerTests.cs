using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class UserChatActiveSendPlannerTests
{
    [Fact]
    public void 有限任务消息数超过可用账号时按可用账号封顶()
    {
        var plan = UserChatActiveSendPlanner.BuildFiniteRunPlan(
            eligibleAccountCount: 1,
            requestedMessageCount: 10,
            dictionaryCount: 10,
            accountMode: UserChatActiveTaskModes.Queue,
            messageMode: UserChatActiveTaskModes.Queue);

        var send = Assert.Single(plan);
        Assert.Equal(0, send.AccountIndex);
        Assert.Equal(0, send.MessageIndex);
    }

    [Fact]
    public void 有限任务总数跟随实际计划数封顶()
    {
        var plan = UserChatActiveSendPlanner.BuildFiniteRunPlan(
            eligibleAccountCount: 1,
            requestedMessageCount: 10,
            dictionaryCount: 10,
            accountMode: UserChatActiveTaskModes.Queue,
            messageMode: UserChatActiveTaskModes.Queue);

        var total = UserChatActiveSendPlanner.ResolveFiniteRunTotal(completedMessageCount: 0, plannedSendCount: plan.Count);

        Assert.Equal(1, total);
    }


    [Fact]
    public void 有限任务账号足够时每个账号最多分配一条消息()
    {
        var plan = UserChatActiveSendPlanner.BuildFiniteRunPlan(
            eligibleAccountCount: 10,
            requestedMessageCount: 10,
            dictionaryCount: 10,
            accountMode: UserChatActiveTaskModes.Queue,
            messageMode: UserChatActiveTaskModes.Queue);

        Assert.Equal(10, plan.Count);
        Assert.Equal(Enumerable.Range(0, 10), plan.Select(x => x.AccountIndex));
        Assert.Equal(Enumerable.Range(0, 10), plan.Select(x => x.MessageIndex));
        Assert.Equal(plan.Count, plan.Select(x => x.AccountIndex).Distinct().Count());
    }

    [Fact]
    public void 队列账号游标会让下一次有限运行接续账号()
    {
        var firstRun = UserChatActiveSendPlanner.BuildFiniteRunPlan(
            eligibleAccountCount: 2,
            requestedMessageCount: 1,
            dictionaryCount: 1,
            accountMode: UserChatActiveTaskModes.Queue,
            messageMode: UserChatActiveTaskModes.Queue,
            accountQueueCursor: 0);
        var nextCursor = UserChatActiveSendPlanner.AdvanceQueueCursor(0, 2);

        var secondRun = UserChatActiveSendPlanner.BuildFiniteRunPlan(
            eligibleAccountCount: 2,
            requestedMessageCount: 1,
            dictionaryCount: 1,
            accountMode: UserChatActiveTaskModes.Queue,
            messageMode: UserChatActiveTaskModes.Queue,
            accountQueueCursor: nextCursor);

        Assert.Equal(0, Assert.Single(firstRun).AccountIndex);
        Assert.Equal(1, Assert.Single(secondRun).AccountIndex);
    }

    [Fact]
    public void 消息规则保留多行段落并支持每条独立图片字典()
    {
        var config = new UserChatActiveTaskConfig
        {
            Dictionary = new List<string> { "旧词典" },
            ImageDictionaryToken = "{legacy_images}",
            MessageRules = new List<UserChatActiveMessageRule>
            {
                new() { Text = "  第一段\r\n第二段  ", ImageDictionaryToken = " {images_a} " },
                new() { Text = "纯文字规则", ImageDictionaryToken = null },
                new() { Text = "  ", ImageDictionaryToken = "  " }
            }
        };

        var rules = UserChatActiveMessageRuleNormalizer.Normalize(config);

        Assert.Equal(2, rules.Count);
        Assert.Equal("第一段\n第二段", rules[0].Text);
        Assert.Equal("{images_a}", rules[0].ImageDictionaryToken);
        Assert.Equal("纯文字规则", rules[1].Text);
        Assert.Null(rules[1].ImageDictionaryToken);
        Assert.Equal(new[] { "第一段\n第二段", "纯文字规则" }, config.Dictionary);
        Assert.Null(config.ImageDictionaryToken);
    }

    [Fact]
    public void 旧词典配置会迁移为等价消息规则()
    {
        var config = new UserChatActiveTaskConfig
        {
            Dictionary = new List<string> { "消息一", "消息二" },
            ImageDictionaryToken = "{legacy_images}"
        };

        var rules = UserChatActiveMessageRuleNormalizer.Normalize(config);

        Assert.Equal(2, rules.Count);
        Assert.All(rules, x => Assert.Equal("{legacy_images}", x.ImageDictionaryToken));
        Assert.Equal("{legacy_images}", config.ImageDictionaryToken);
    }

    [Fact]
    public void 仅图片字典配置会生成可循环的图片规则()
    {
        var config = new UserChatActiveTaskConfig
        {
            ImageDictionaryToken = "{images_only}"
        };

        var rule = Assert.Single(UserChatActiveMessageRuleNormalizer.Normalize(config));

        Assert.Equal(string.Empty, rule.Text);
        Assert.Equal("{images_only}", rule.ImageDictionaryToken);
    }

    [Fact]
    public async Task Bot无历史时先启动一次且后续发送不重复启动()
    {
        var tracker = new UserChatActiveBotStartTracker();
        var historyCalls = 0;
        var startCalls = 0;

        var first = await tracker.EnsureReadyAsync(
            isBotTarget: true,
            accountId: 7,
            botUserId: 99,
            readConversationHistory: () =>
            {
                historyCalls++;
                return Task.FromResult((Success: true, Error: (string?)null, HasConversation: false));
            },
            startBot: () =>
            {
                startCalls++;
                return Task.FromResult((Success: true, Error: (string?)null));
            });

        var second = await tracker.EnsureReadyAsync(
            isBotTarget: true,
            accountId: 7,
            botUserId: 99,
            readConversationHistory: () => throw new InvalidOperationException("已缓存的 Bot 不应再次读取历史"),
            startBot: () => throw new InvalidOperationException("已启动的 Bot 不应重复发送 /start"));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, historyCalls);
        Assert.Equal(1, startCalls);
    }

    [Fact]
    public async Task 已有Bot会话历史时不重复启动()
    {
        var tracker = new UserChatActiveBotStartTracker();
        var startCalls = 0;

        var result = await tracker.EnsureReadyAsync(
            isBotTarget: true,
            accountId: 7,
            botUserId: 99,
            readConversationHistory: () => Task.FromResult((Success: true, Error: (string?)null, HasConversation: true)),
            startBot: () =>
            {
                startCalls++;
                return Task.FromResult((Success: true, Error: (string?)null));
            });

        Assert.True(result.Success);
        Assert.Equal(0, startCalls);
    }

    [Fact]
    public async Task Bot启动失败时不缓存成功状态并保留错误()
    {
        var tracker = new UserChatActiveBotStartTracker();
        var startCalls = 0;

        var result = await tracker.EnsureReadyAsync(
            isBotTarget: true,
            accountId: 7,
            botUserId: 99,
            readConversationHistory: () => Task.FromResult((Success: true, Error: (string?)null, HasConversation: false)),
            startBot: () =>
            {
                startCalls++;
                return Task.FromResult((Success: false, Error: (string?)"USER_IS_BLOCKED"));
            });

        Assert.False(result.Success);
        Assert.Equal("USER_IS_BLOCKED", result.Error);

        var blockedRetry = await tracker.EnsureReadyAsync(
            isBotTarget: true,
            accountId: 7,
            botUserId: 99,
            readConversationHistory: () => throw new InvalidOperationException("失败后本轮不应再次读取历史"),
            startBot: () => throw new InvalidOperationException("失败后本轮不应重复发送 /start"));

        Assert.False(blockedRetry.Success);
        Assert.Equal("USER_IS_BLOCKED", blockedRetry.Error);
        Assert.Equal(1, startCalls);
    }

    [Fact]
    public async Task 私聊历史无法读取时不启动Bot并阻止本轮正文发送()
    {
        var tracker = new UserChatActiveBotStartTracker();
        var startCalls = 0;

        var result = await tracker.EnsureReadyAsync(
            true, 7, 99,
            () => Task.FromResult((false, (string?)"历史读取失败", false)),
            () =>
            {
                startCalls++;
                return Task.FromResult((true, (string?)null));
            });

        Assert.False(result.Success);
        Assert.Equal("历史读取失败", result.Error);
        Assert.Equal(0, startCalls);
    }

    [Fact]
    public async Task 新一轮执行在无历史时允许重新启动()
    {
        var previousRun = new UserChatActiveBotStartTracker();
        var startCalls = 0;

        var previousResult = await previousRun.EnsureReadyAsync(
            true, 7, 99,
            () => Task.FromResult((true, (string?)null, false)),
            () =>
            {
                startCalls++;
                return Task.FromResult((false, (string?)"YOU_BLOCKED_USER"));
            });

        var nextRun = new UserChatActiveBotStartTracker();

        var result = await nextRun.EnsureReadyAsync(
            true, 7, 99,
            () => Task.FromResult((true, (string?)null, false)),
            () =>
            {
                startCalls++;
                return Task.FromResult((true, (string?)null));
            });

        Assert.True(result.Success);
        Assert.False(previousResult.Success);
        Assert.Equal(2, startCalls);
    }

    [Fact]
    public async Task Bot被屏蔽错误会暂停本轮且新一轮按历史重新判断()
    {
        var currentRun = new UserChatActiveBotStartTracker();
        var ready = await currentRun.EnsureReadyAsync(
            true, 7, 99,
            () => Task.FromResult((true, (string?)null, true)),
            () => throw new InvalidOperationException("已有历史时不应发送 /start"));

        currentRun.PauseAfterBlockedError(7, 99, "YOU_BLOCKED_USER");
        var paused = await currentRun.EnsureReadyAsync(
            true, 7, 99,
            () => throw new InvalidOperationException("本轮暂停后不应重复读取历史"),
            () => throw new InvalidOperationException("本轮暂停后不应重复发送 /start"));

        var nextRun = new UserChatActiveBotStartTracker();
        var nextRunResult = await nextRun.EnsureReadyAsync(
            true, 7, 99,
            () => Task.FromResult((true, (string?)null, true)),
            () => throw new InvalidOperationException("已有历史时新一轮也不应发送 /start"));

        Assert.True(ready.Success);
        Assert.False(paused.Success);
        Assert.Equal("YOU_BLOCKED_USER", paused.Error);
        Assert.True(nextRunResult.Success);
    }

    [Theory]
    [InlineData("USER_IS_BLOCKED", true)]
    [InlineData("YOU_BLOCKED_USER", true)]
    [InlineData("BOT blocked by policy", false)]
    [InlineData("PEER_ID_INVALID", false)]
    public void 仅Telegram明确屏蔽错误触发本轮暂停(string error, bool expected)
    {
        Assert.Equal(expected, UserChatActiveBotStartTracker.IsTelegramBlockedError(error));
    }

    [Fact]
    public async Task 普通群组不读取Bot状态也不发送启动请求()
    {
        var tracker = new UserChatActiveBotStartTracker();
        var result = await tracker.EnsureReadyAsync(
            isBotTarget: false,
            accountId: 7,
            botUserId: 0,
            readConversationHistory: () => throw new InvalidOperationException("群组无需读取 Bot 历史"),
            startBot: () => throw new InvalidOperationException("群组无需发送 Bot /start"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Bot启动状态按账号与Bot隔离()
    {
        var tracker = new UserChatActiveBotStartTracker();

        await tracker.EnsureReadyAsync(
            true, 7, 99,
            () => Task.FromResult((true, (string?)null, false)),
            () => Task.FromResult((true, (string?)null)));

        var otherAccount = 0;
        var otherBot = 0;
        await tracker.EnsureReadyAsync(true, 8, 99,
            () => { otherAccount++; return Task.FromResult((true, (string?)null, false)); },
            () => Task.FromResult((true, (string?)null)));
        await tracker.EnsureReadyAsync(true, 7, 100,
            () => { otherBot++; return Task.FromResult((true, (string?)null, false)); },
            () => Task.FromResult((true, (string?)null)));

        Assert.Equal(1, otherAccount);
        Assert.Equal(1, otherBot);
    }
}
