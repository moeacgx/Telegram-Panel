using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class WgcfWireproxyResourcesTests
{
    [Fact]
    public void 关闭覆盖保留父环境并不添加资源参数()
    {
        var resources = new WgcfWireproxyResources(new ConfigurationBuilder().Build());
        var start = new ProcessStartInfo("python3");
        start.Environment["GOMAXPROCS"] = "3";
        start.Environment["GOMEMLIMIT"] = "96MiB";
        resources.Apply(start);
        Assert.Null(resources.Error);
        Assert.Empty(start.ArgumentList);
        Assert.Equal("3", start.Environment["GOMAXPROCS"]);
        Assert.Equal("96MiB", start.Environment["GOMEMLIMIT"]);
    }

    [Fact]
    public void 显式覆盖使用统一工具参数且不改监督器的Go环境()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WgcfWarp:Wireproxy:GoMaxProcs"] = "2",
            ["WgcfWarp:Wireproxy:GoMemLimitMiB"] = "64",
            ["WgcfWarp:Wireproxy:NoFile"] = "256"
        }).Build();
        var start = new ProcessStartInfo("python3");
        start.Environment["GOMAXPROCS"] = "7";
        new WgcfWireproxyResources(config).Apply(start);
        Assert.Equal(new[] { "--wireproxy-gomaxprocs=2", "--wireproxy-gomemlimit-mib=64", "--wireproxy-nofile=256" },
            start.ArgumentList);
        Assert.Equal("7", start.Environment["GOMAXPROCS"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData(" 1")]
    [InlineData("2147483648")]
    [InlineData("SECRET_SENTINEL")]
    public void 非法配置明确失败且不输出原值(string value)
    {
        foreach (var key in new[] { "GoMaxProcs", "GoMemLimitMiB", "NoFile" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"WgcfWarp:Wireproxy:{key}"] = value
            }).Build();
            var resources = new WgcfWireproxyResources(config);
            Assert.Contains($"WgcfWarp:Wireproxy:{key}", resources.Error);
            Assert.DoesNotContain("SECRET_SENTINEL", resources.Error);
            Assert.Throws<InvalidOperationException>(() => resources.Apply(new ProcessStartInfo("python3")));
        }
    }
}
