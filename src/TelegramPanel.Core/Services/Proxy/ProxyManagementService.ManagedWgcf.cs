using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Proxy;

public sealed partial class ProxyManagementService
{
    public Task ValidateUnmanagedEndpointAsync(
        string host, int port, CancellationToken cancellationToken = default) =>
        ManagedWgcfProxyPolicy.EnsureUnmanagedEndpointAsync(_db, host, port, cancellationToken);

    public async Task<OutboundProxy> CreateManagedWgcfProxyAsync(
        string profile, string name, int port, string username, string password,
        CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(profile ?? string.Empty, "^[a-z0-9][a-z0-9_-]{0,47}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("受管 WireGuard 配置编号无效", nameof(profile));
        if (port is < 1024 or > 65535)
            throw new ArgumentException("受管 WireGuard 监听端口必须介于 1024 和 65535", nameof(port));
        foreach (var credential in new[] { username, password })
        {
            if (string.IsNullOrEmpty(credential) || Encoding.UTF8.GetByteCount(credential) > 255
                || credential.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                throw new ArgumentException("受管 WireGuard SOCKS 凭据无效");
        }
        name = NormalizeName(name, "受管 WireGuard");
        await using var lease = await AcquireMutationLeaseAsync(cancellationToken);
        var existing = await _db.OutboundProxies.SingleOrDefaultAsync(
            proxy => proxy.ManagedWgcfProfile == profile, cancellationToken);
        if (existing != null)
        {
            if (existing.Kind != "wireguard_warp" || existing.Protocol != "socks5" || existing.Host != "127.0.0.1"
                || existing.Port != port || existing.Username != username || existing.Password != password)
                throw new InvalidOperationException("受管 WireGuard 档案与已保存代理不一致，未覆盖连接材料");
            return existing;
        }
        var portPeers = await _db.OutboundProxies.Where(proxy => proxy.Port == port).ToListAsync(cancellationToken);
        if (portPeers.Any(proxy => ManagedWgcfProxyPolicy.IsLoopbackHost(proxy.Host)))
            throw new InvalidOperationException("本地监听端口已被代理记录占用");
        if (_configuration != null
            && GlobalTelegramProxyConfiguration.IsEnabled(_configuration)
            && GlobalTelegramProxyConfiguration.GetSourceMode(_configuration) == "manual")
        {
            var global = GlobalTelegramProxyConfiguration.BuildRequired(_configuration);
            if (global.Port == port && ManagedWgcfProxyPolicy.IsLoopbackHost(global.Host))
                throw new InvalidOperationException("本地监听端口已被全局代理配置占用");
        }
        var proxy = new OutboundProxy
        {
            ManagedWgcfProfile = profile, Name = name, Kind = "wireguard_warp", Protocol = "socks5",
            Host = "127.0.0.1", Port = port, Username = username, Password = password,
            IsEnabled = true, TestStatus = "unknown", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        _db.OutboundProxies.Add(proxy);
        await _db.SaveChangesAsync(cancellationToken);
        return proxy;
    }

    public async Task<OutboundProxy> SetManagedWgcfEnabledAsync(
        string profile, bool enabled, CancellationToken cancellationToken = default,
        string? claimToken = null)
    {
        await using var lease = await AcquireMutationLeaseAsync(cancellationToken);
        // 启停落库期间也独占档案，避免首次连接租约跨过占用检查窗口。
        using var profileClaim = _temporaryWarpClaims != null
            && !_temporaryWarpClaims.OwnsManagedProfile(profile, claimToken)
                ? _temporaryWarpClaims.ClaimManagedProfile(profile)
                : null;
        var proxy = await _db.OutboundProxies.SingleOrDefaultAsync(
            proxy => proxy.ManagedWgcfProfile == profile, cancellationToken)
            ?? throw new KeyNotFoundException("受管 WireGuard 代理不存在");
        EnsureManagedProfileClaimOwner(proxy, profileClaim?.Token ?? claimToken);
        if (!enabled && (IsEnabledGlobalProxy(proxy.Id)
                         || await _db.Accounts.AnyAsync(account => account.ProxyId == proxy.Id, cancellationToken)))
            throw new ProxyInUseException("受管 WireGuard 仍被账号或全局配置使用，请先明确切换账号出口后再停止");
        proxy.IsEnabled = enabled;
        ResetProbeState(proxy);
        proxy.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return proxy;
    }

    private static void RejectOrdinaryManagedWgcfMutation(OutboundProxy proxy)
    {
        if (proxy.ManagedWgcfProfile != null)
            throw new InvalidOperationException("受管 WireGuard 请使用专用创建和启停入口，普通编辑或删除不会变更运行器");
    }

    private void EnsureManagedProfileClaimOwner(OutboundProxy proxy, string? claimToken)
    {
        if (proxy.ManagedWgcfProfile is { } profile
            && _temporaryWarpClaims?.IsManagedProfileClaimed(profile) == true
            && !_temporaryWarpClaims.OwnsManagedProfile(profile, claimToken))
            throw new InvalidOperationException("轻量 WARP 正被首次连接或绑定流程占用，未修改出口");
    }
}
