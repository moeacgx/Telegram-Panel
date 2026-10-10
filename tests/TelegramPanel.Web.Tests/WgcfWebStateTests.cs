using System.Text.Json;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class WgcfWebStateTests
{
    [Fact]
    public void 请求标识规范化后重试对应同一档案()
    {
        var id = Guid.NewGuid();
        Assert.Equal(WgcfWarpService.ProfileFor(id.ToString("D")), WgcfWarpService.ProfileFor(id.ToString("N")));
        Assert.Throws<ArgumentException>(() => WgcfWarpService.ProfileFor("../private"));
    }

    [Fact]
    public void 注册前拒绝空名称和控制字符()
    {
        Assert.Equal("测试出口", WgcfWarpService.ValidateName(" 测试出口 "));
        Assert.Throws<ArgumentException>(() => WgcfWarpService.ValidateName(" "));
        Assert.Throws<ArgumentException>(() => WgcfWarpService.ValidateName("出口\n秘密"));
    }

    [Fact]
    public void 陈旧运行回执和不同修订号不能表示正在监听()
    {
        using var meta = JsonDocument.Parse("{\"revision\":\"new\",\"desired\":true}");
        using var stale = JsonDocument.Parse("{\"state\":\"listening\",\"revision\":\"new\",\"at\":1}");
        using var wrong = JsonDocument.Parse($"{{\"state\":\"listening\",\"revision\":\"old\",\"at\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}");
        Assert.Equal("unknown", WgcfWarpService.RuntimeState(meta.RootElement, stale.RootElement));
        Assert.Equal("starting", WgcfWarpService.RuntimeState(meta.RootElement, wrong.RootElement));
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("temporary", false)]
    [InlineData("stopRequested", false)]
    [InlineData("claimed", false)]
    [InlineData("creating", false)]
    [InlineData("undesired", false)]
    [InlineData("stopped", false)]
    [InlineData("missing", false)]
    [InlineData("disabled", false)]
    [InlineData("untested", false)]
    [InlineData("noEgress", false)]
    [InlineData("bound", false)]
    [InlineData("global", false)]
    public void 空闲池资格排除临时占用与未就绪出口(string scenario, bool expected)
    {
        var proxy = new OutboundProxy
        {
            IsEnabled = scenario != "disabled",
            TestStatus = scenario == "untested" ? "unknown" : "ok",
            EgressIp = scenario == "noEgress" ? null : "1.2.3.4"
        };
        if (scenario == "bound") proxy.Accounts.Add(new Account());

        var eligible = WgcfWarpService.IsPoolEligible(
            temporary: scenario == "temporary",
            stopRequested: scenario == "stopRequested",
            claimed: scenario == "claimed",
            phase: scenario == "creating" ? "creating" : "ready",
            desired: scenario != "undesired",
            runtime: scenario == "stopped" ? "stopped" : "listening",
            proxy: scenario == "missing" ? null : proxy,
            globallySelected: scenario == "global");

        Assert.Equal(expected, eligible);
    }
}
