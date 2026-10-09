using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TelegramPanel.Core.Models;
using TelegramPanel.Data;

namespace TelegramPanel.Core.Services.Proxy;

/// <summary>
/// 解析账号全局代理。已有代理仅保存 ID，运行时始终从数据库读取最新连接参数。
/// </summary>
public sealed class GlobalProxyResolver
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public GlobalProxyResolver(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<ProxyConnectionOptions?> ResolveAsync(
        string stableAccountKey,
        CancellationToken cancellationToken = default)
    {
        if (!GlobalTelegramProxyConfiguration.IsEnabled(_configuration))
            return null;

        var sourceMode = GlobalTelegramProxyConfiguration.GetSourceMode(_configuration);
        if (sourceMode == GlobalTelegramProxyConfiguration.ManualSourceMode)
        {
            var manual = GlobalTelegramProxyConfiguration.Build(_configuration);
            if (manual != null)
                await ManagedWgcfProxyPolicy.EnsureUnmanagedEndpointAsync(_db, manual.Host, manual.Port, cancellationToken);
            return manual;
        }
        if (sourceMode != GlobalTelegramProxyConfiguration.ExistingSourceMode)
            throw new InvalidOperationException("Telegram 全局代理来源模式无效，已阻止降级为直连");

        var proxyId = GlobalTelegramProxyConfiguration.GetSelectedProxyId(
            _configuration,
            requireEnabled: false)
            ?? throw new InvalidOperationException("Telegram 全局代理未选择已有代理，已阻止降级为直连");
        var proxy = await _db.OutboundProxies
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == proxyId, cancellationToken);
        if (proxy is not { IsEnabled: true })
        {
            throw new InvalidOperationException(
                "Telegram 全局代理引用的已有代理不存在或已停用，已阻止降级为直连");
        }
        if (proxy.ManagedWgcfProfile != null)
            throw new InvalidOperationException("受管 WireGuard 不能作为全局代理，已阻止降级为直连");
        if (proxy.Kind == OutboundProxyKinds.WireGuardWarp
            && (proxy.TestStatus != "ok" || string.IsNullOrWhiteSpace(proxy.EgressIp)))
        {
            throw new InvalidOperationException(
                "Telegram 全局代理引用的外部 WireGuard WARP 端点尚未检测成功，已阻止降级为直连");
        }


        return AccountProxyResolver.BuildConnectionOptions(proxy, stableAccountKey);
    }

    public async Task<ProxyConnectionOptions> ResolveRequiredAsync(
        string stableAccountKey,
        CancellationToken cancellationToken = default) =>
        await ResolveAsync(stableAccountKey, cancellationToken)
        ?? throw new InvalidOperationException(
            "Telegram 全局代理尚未配置，已阻止降级为直连");

    /// <summary>
    /// 仅供仍传递旧式 UseGlobalProxy 标记的兼容调用方使用。
    /// 已有代理引用必须走异步数据库解析，禁止退回配置快照。
    /// </summary>
    public static ProxyConnectionOptions ResolveLegacyManualRequired(
        IConfiguration configuration)
    {
        if (GlobalTelegramProxyConfiguration.GetSourceMode(configuration)
            == GlobalTelegramProxyConfiguration.ExistingSourceMode)
        {
            throw new InvalidOperationException(
                "Telegram 全局代理引用已有代理但未通过数据库解析，已阻止降级为直连");
        }

        return GlobalTelegramProxyConfiguration.BuildRequired(configuration);
    }
}
