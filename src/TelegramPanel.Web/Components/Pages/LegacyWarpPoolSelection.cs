using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;
using TelegramPanel.Web.Services;

namespace TelegramPanel.Web.Components.Pages;

/// <summary>为旧 Razor 兼容页面复用轻量 WARP 的运行态领取资格。</summary>
internal static class LegacyWarpPoolSelection
{
    public static int Count(
        IReadOnlyCollection<OutboundProxy> proxies,
        WgcfEnvironmentDto? environment,
        int? globallySelectedProxyId)
    {
        if (environment is not { Available: true })
            return 0;

        var eligibleProfiles = environment.Profiles
            .Where(x => x.PoolEligible && x.ProxyId is > 0)
            .ToDictionary(x => x.Profile, x => x.ProxyId!.Value, StringComparer.Ordinal);

        return proxies.Count(proxy =>
            proxy.IsEnabled
            && proxy.Kind == OutboundProxyKinds.WireGuardWarp
            && proxy.ManagedWgcfProfile is { Length: > 0 } profile
            && eligibleProfiles.TryGetValue(profile, out var eligibleProxyId)
            && eligibleProxyId == proxy.Id
            && proxy.Id != globallySelectedProxyId
            && proxy.Accounts.Count == 0
            && proxy.TestStatus == "ok"
            && !string.IsNullOrWhiteSpace(proxy.EgressIp));
    }
}
