using System.Net;
using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data;

namespace TelegramPanel.Core.Services.Proxy;

public static class ManagedWgcfProxyPolicy
{
    public static bool IsLoopbackHost(string? host)
    {
        var normalized = host?.Trim().Trim('[', ']').TrimEnd('.');
        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(normalized, out var address)
               && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    public static async Task EnsureUnmanagedEndpointAsync(
        AppDbContext db, string host, int port, CancellationToken cancellationToken = default)
    {
        if (IsLoopbackHost(host) && await db.OutboundProxies.AsNoTracking().AnyAsync(
                proxy => proxy.ManagedWgcfProfile != null && proxy.Port == port,
                cancellationToken))
        {
            throw new InvalidOperationException("该本地端口属于受管 WireGuard 出口，不能创建普通代理或全局代理影子记录");
        }
    }
}
