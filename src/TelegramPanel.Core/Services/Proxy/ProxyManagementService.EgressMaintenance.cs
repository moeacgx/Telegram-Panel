using Microsoft.EntityFrameworkCore;
using TelegramPanel.Core.Models;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Proxy;

public sealed partial class ProxyManagementService
{
    /// <summary>
    /// 轻量巡检所有启用的普通代理和 Resin 代理连通性。
    /// 受管轻量 WARP 由专用检测处理，避免普通 HTTP 健康探针覆盖 warp=on 验证结果。
    /// 巡检不拉取出口 IP/地理元数据，避免每 5 分钟调用 Trace 元数据端点。
    /// </summary>
    public async Task<ProxyEgressMaintenanceBatchResult> RefreshAllNonWarpProxyEgressAsync(
        CancellationToken cancellationToken = default)
    {
        var proxies = await _db.OutboundProxies
            .AsNoTracking()
            .Where(x => x.IsEnabled
                && x.Kind != OutboundProxyKinds.Warp
                && x.ManagedWgcfProfile == null
                && x.Protocol != OutboundProxyProtocols.MtProto)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Name })
            .ToListAsync(cancellationToken);

        var items = new List<ProxyEgressMaintenanceItem>(proxies.Count);
        foreach (var candidate in proxies)
        {
            try
            {
                var proxy = await TestHealthAsync(candidate.Id, cancellationToken);
                items.Add(new ProxyEgressMaintenanceItem(
                    proxy.Id,
                    proxy.Name,
                    proxy.TestStatus == "ok",
                    proxy.EgressIp,
                    proxy.LastError));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                items.Add(new ProxyEgressMaintenanceItem(
                    candidate.Id,
                    candidate.Name,
                    false,
                    null,
                    SafeError(ex)));
            }
        }

        return new ProxyEgressMaintenanceBatchResult(
            items.Count,
            items.Count(x => x.Success),
            items.Count(x => !x.Success),
            items);
    }
    private async Task<OutboundProxy> TestHealthAsync(
        int id,
        CancellationToken cancellationToken)
    {
        await MutationLock.WaitAsync(cancellationToken);
        try
        {
            var proxy = await _db.OutboundProxies
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("代理不存在");
            var probeKey = $"telegram_panel_probe_{proxy.Id}";
            try
            {
                EgressProbeResult result;
                if (proxy.Kind == OutboundProxyKinds.Resin)
                {
                    var controlError = await ValidateResinControlPlaneAsync(proxy, cancellationToken);
                    result = controlError == null
                        ? await _probeService.ProbeProxyHealthAsync(
                            proxy,
                            probeKey,
                            cancellationToken)
                        : new EgressProbeResult(
                            false,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            DateTime.UtcNow,
                            controlError);
                }
                else
                {
                    result = await _probeService.ProbeProxyHealthAsync(
                        proxy,
                        probeKey,
                        cancellationToken);
                }

                proxy.TestStatus = result.Success ? "ok" : "fail";
                proxy.LastError = result.Error;
                proxy.LastLatencyMs = result.Success ? result.LatencyMs : null;
                proxy.LastTestedAtUtc = result.CheckedAtUtc;
                proxy.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                return proxy;
            }
            finally
            {
                if (proxy.Kind == OutboundProxyKinds.Resin)
                {
                    await ReleaseResinLeaseBestEffortAsync(
                        proxy,
                        probeKey,
                        $"巡检身份 {probeKey}",
                        CancellationToken.None);
                }
            }
        }
        finally
        {
            MutationLock.Release();
        }
    }
}
