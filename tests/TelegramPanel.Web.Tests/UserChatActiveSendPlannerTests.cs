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
}
