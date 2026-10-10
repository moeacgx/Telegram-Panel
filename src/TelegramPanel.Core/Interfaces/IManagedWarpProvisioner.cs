using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Interfaces;

public sealed record ManagedWarpAvailability(bool Available, string? Reason);

/// <summary>首次连接期间独占轻量出口，正式绑定完成或严格断开后才能释放。</summary>
public sealed class ManagedWarpProxyLease : IDisposable
{
    private IDisposable? _reservation;
    public OutboundProxy Proxy { get; }
    public string ClaimToken { get; }

    public ManagedWarpProxyLease(OutboundProxy proxy, string claimToken, IDisposable reservation)
    {
        Proxy = proxy;
        ClaimToken = claimToken;
        _reservation = reservation;
    }

    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
}

/// <summary>由宿主实现轻量运行器，Core 不依赖 Web 或 Docker 创建逻辑。</summary>
public interface IManagedWarpProvisioner
{
    Task<ManagedWarpAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<ManagedWarpProxyLease> ProvisionAsync(string name, string requestId, bool acceptTerms,
        CancellationToken cancellationToken = default);
    Task<ManagedWarpProxyLease> AcquireAsync(int proxyId, CancellationToken cancellationToken = default);
    Task StopUnboundAsync(int proxyId, string? claimToken = null,
        CancellationToken cancellationToken = default);
}
