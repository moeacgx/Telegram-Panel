using System.Text.Json;
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
}
