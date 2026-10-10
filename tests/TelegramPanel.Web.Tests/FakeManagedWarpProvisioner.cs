using Microsoft.EntityFrameworkCore;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Tests;

internal sealed class FakeManagedWarpProvisioner(AppDbContext db, TemporaryWarpClaimStore claims)
    : IManagedWarpProvisioner
{
    public bool Ready { get; set; } = true;
    public HashSet<int> UnavailableProxyIds { get; } = new();
    public int ProvisionCalls { get; private set; }
    public int StopCalls { get; private set; }
    public Func<OutboundProxy, Task>? OnProvision { get; set; }
    public Func<OutboundProxy, Task>? OnStop { get; set; }

    public Task<ManagedWarpAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManagedWarpAvailability(true, null));

    public async Task<ManagedWarpProxyLease> ProvisionAsync(string name, string requestId, bool acceptTerms,
        CancellationToken cancellationToken = default)
    {
        if (!acceptTerms) throw new ArgumentException("尚未确认 WARP 条款");
        ProvisionCalls++;
        var profile = "test-" + requestId.Replace("-", "");
        var proxy = await db.OutboundProxies.SingleOrDefaultAsync(x => x.ManagedWgcfProfile == profile, cancellationToken);
        if (proxy == null)
        {
            proxy = new OutboundProxy
            {
                Name = name, Kind = OutboundProxyKinds.WireGuardWarp, ManagedWgcfProfile = profile,
                Protocol = OutboundProxyProtocols.Socks5, Host = "127.0.0.1", Port = 21000 + ProvisionCalls,
                Username = "test", Password = "secret", IsEnabled = true,
                TestStatus = Ready ? "ok" : "unknown", EgressIp = Ready ? "2606:4700:100::90" : null
            };
            db.OutboundProxies.Add(proxy);
            await db.SaveChangesAsync(cancellationToken);
        }
        var claim = claims.ClaimManagedProfile(profile);
        try
        {
            if (OnProvision != null) await OnProvision(proxy);
            return new ManagedWarpProxyLease(proxy, claim.Token, claim);
        }
        catch { claim.Dispose(); throw; }
    }

    public async Task<ManagedWarpProxyLease> AcquireAsync(int proxyId, CancellationToken cancellationToken = default)
    {
        if (UnavailableProxyIds.Contains(proxyId))
            throw new InvalidOperationException("运行器档案尚未就绪");
        var proxy = await db.OutboundProxies.SingleAsync(x => x.Id == proxyId, cancellationToken);
        if (await db.Accounts.AnyAsync(x => x.ProxyId == proxyId, cancellationToken))
            throw new InvalidOperationException("出口已有账号绑定");
        var claim = claims.ClaimManagedProfile(proxy.ManagedWgcfProfile!);
        return new ManagedWarpProxyLease(proxy, claim.Token, claim);
    }

    public async Task StopUnboundAsync(int proxyId, string? claimToken = null,
        CancellationToken cancellationToken = default)
    {
        var proxy = await db.OutboundProxies.SingleAsync(x => x.Id == proxyId, cancellationToken);
        if (claims.IsManagedProfileClaimed(proxy.ManagedWgcfProfile!)
            && !claims.OwnsManagedProfile(proxy.ManagedWgcfProfile!, claimToken))
            throw new InvalidOperationException("出口占用凭据不匹配");
        if (await db.Accounts.AnyAsync(x => x.ProxyId == proxyId, cancellationToken))
            throw new ProxyInUseException("出口仍被账号使用");
        if (OnStop != null) await OnStop(proxy);
        StopCalls++;
        proxy.IsEnabled = false;
        proxy.TestStatus = "unknown";
        await db.SaveChangesAsync(cancellationToken);
    }
}
