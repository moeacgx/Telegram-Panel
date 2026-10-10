using System.Reflection;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Components.Pages;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class LegacyWarpPoolSelectionTests
{
    [Theory]
    [InlineData(typeof(AccountLogin), "ready", 1)]
    [InlineData(typeof(AccountImport), "ready", 1)]
    [InlineData(typeof(AccountLogin), "ineligible", 0)]
    [InlineData(typeof(AccountImport), "ineligible", 0)]
    [InlineData(typeof(AccountLogin), "unavailable", 0)]
    [InlineData(typeof(AccountImport), "unavailable", 0)]
    [InlineData(typeof(AccountLogin), "missing", 0)]
    [InlineData(typeof(AccountImport), "missing", 0)]
    [InlineData(typeof(AccountLogin), "global", 0)]
    [InlineData(typeof(AccountImport), "global", 0)]
    [InlineData(typeof(AccountLogin), "legacy", 0)]
    [InlineData(typeof(AccountImport), "legacy", 0)]
    [InlineData(typeof(AccountLogin), "bound", 0)]
    [InlineData(typeof(AccountImport), "bound", 0)]
    [InlineData(typeof(AccountLogin), "mismatch", 0)]
    [InlineData(typeof(AccountImport), "mismatch", 0)]
    public async Task 兼容页根据运行器快照决定空闲池选择(Type pageType, string scenario, int expectedCount)
    {
        var page = Activator.CreateInstance(pageType)!;
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:Proxy:Enabled"] = (scenario == "global").ToString(),
                ["Telegram:Proxy:SourceMode"] = "existing",
                ["Telegram:Proxy:ProxyId"] = "17"
            }).Build();
            pageType.GetProperty("Configuration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(page, configuration);
            SetField(page, "proxyStrategy", "warp_pool");
            SetField(page, "proxyOptions", new List<OutboundProxy>
            {
                new()
                {
                    Id = scenario == "mismatch" ? 18 : 17, IsEnabled = true, TestStatus = "ok", EgressIp = "1.2.3.4",
                    Kind = scenario == "legacy" ? OutboundProxyKinds.Warp : OutboundProxyKinds.WireGuardWarp,
                    ManagedWgcfProfile = scenario == "legacy" ? null : "web-ready",
                    WarpProfile = scenario == "legacy" ? new WarpProfile { DesiredEnabled = true, Status = "active" } : null,
                    Accounts = scenario == "bound" ? new List<Account> { new() } : new List<Account>()
                }
            });
            var profiles = scenario is "legacy" or "missing"
                ? Array.Empty<WgcfProfileDto>()
                : new[]
                {
                    new WgcfProfileDto("web-ready", "空闲出口", "ready", true, true, true,
                        "listening", 17, 0, "ok", "1.2.3.4", null)
                    {
                        PoolEligible = scenario != "ineligible"
                    }
                };
            SetField(page, "warpEnvironment", new WgcfEnvironmentDto(scenario != "unavailable", null, profiles));

            Assert.Equal(expectedCount, GetProperty<int>(page, "WarpPoolCount"));
            Assert.Equal(expectedCount == 0, GetProperty<bool>(page, "ProxySelectionInvalid"));
        }
        finally
        {
            if (page is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (page is IDisposable disposable) disposable.Dispose();
        }
    }

    [Theory]
    [InlineData(typeof(AccountLogin), "manual", true)]
    [InlineData(typeof(AccountImport), "manual", true)]
    [InlineData(typeof(AccountLogin), "warp", false)]
    [InlineData(typeof(AccountImport), "warp", false)]
    [InlineData(typeof(AccountLogin), "managed", false)]
    [InlineData(typeof(AccountImport), "managed", false)]
    public async Task 普通代理选项和提交校验拒绝旧版与受管出口(Type pageType, string kind, bool expected)
    {
        var page = Activator.CreateInstance(pageType)!;
        try
        {
            SetField(page, "proxyStrategy", "existing");
            SetField(page, "selectedProxyId", (int?)17);
            SetField(page, "proxyOptions", new List<OutboundProxy>
            {
                new()
                {
                    Id = 17, IsEnabled = true,
                    Kind = kind == "managed" ? OutboundProxyKinds.WireGuardWarp : kind,
                    ManagedWgcfProfile = kind == "managed" ? "web-ready" : null
                }
            });

            Assert.Equal(expected, GetProperty<IEnumerable<OutboundProxy>>(page, "ExistingProxyOptions").Any());
            Assert.Equal(!expected, GetProperty<bool>(page, "ProxySelectionInvalid"));
        }
        finally
        {
            if (page is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (page is IDisposable disposable) disposable.Dispose();
        }
    }

    private static void SetField(object page, string name, object value) =>
        page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static T GetProperty<T>(object page, string name) =>
        (T)page.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
}
