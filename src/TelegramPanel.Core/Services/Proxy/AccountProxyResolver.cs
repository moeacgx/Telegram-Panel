using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Core.Services.Proxy;

/// <summary>
/// 解析账号当前代理，并为 Resin 生成稳定的账号身份。
/// </summary>
public sealed class AccountProxyResolver : IAccountProxyResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public AccountProxyResolver(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
    }

    public async Task<AccountProxyResolution> ResolveAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(accountId), "必须提供已入库账号 ID，禁止为未知账号推断出口");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts
            .AsNoTracking()
            .Include(x => x.Proxy)
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);

        if (account == null)
            throw new KeyNotFoundException($"账号 {accountId} 不存在，已阻止推断代理出口");
        if (!account.ProxyId.HasValue)
        {
            return account.UseGlobalProxy
                ? await ResolveGlobalProxyAsync(db, accountId, cancellationToken)
                : new AccountProxyResolution(null, false);
        }
        if (account.Proxy is not { IsEnabled: true } proxy)
            throw new InvalidOperationException($"账号 {accountId} 绑定的代理不可用，已阻止降级为直连");
        if (proxy.ManagedWgcfProfile != null
            && await db.Accounts.AsNoTracking().AnyAsync(
                other => other.ProxyId == proxy.Id && other.Id != accountId, cancellationToken))
            throw new InvalidOperationException("受管 WireGuard 出口存在多个账号引用，已阻止连接或降级为直连");

        return new AccountProxyResolution(
            BuildConnectionOptions(proxy, $"tg_account_{accountId}"),
            false);
    }

    private async Task<AccountProxyResolution> ResolveGlobalProxyAsync(
        AppDbContext db,
        int accountId,
        CancellationToken cancellationToken) =>
        new(
            await new GlobalProxyResolver(db, _configuration).ResolveRequiredAsync(
                $"tg_account_{accountId}",
                cancellationToken),
            false);

    public static ProxyConnectionOptions BuildConnectionOptions(
        OutboundProxy proxy,
        string stableAccountKey)
    {
        RejectRetiredWarp(proxy);
        var username = proxy.Username;
        var password = proxy.Password;

        if (proxy.Kind == OutboundProxyKinds.Resin)
        {
            var platform = string.IsNullOrWhiteSpace(proxy.ResinPlatform)
                ? "Default"
                : proxy.ResinPlatform.Trim();
            username = $"{platform}.{NormalizeResinAccount(stableAccountKey)}";
        }

        return new ProxyConnectionOptions(
            proxy.Id,
            proxy.Name,
            proxy.Kind,
            proxy.Protocol,
            proxy.Host,
            proxy.Port,
            username,
            password,
            proxy.Secret);
    }

    private static string NormalizeResinAccount(string value)
    {
        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_')
            .ToArray();
        var normalized = new string(chars);
        return string.IsNullOrWhiteSpace(normalized) ? "telegram_panel" : normalized;
    }

    public static void RejectRetiredWarp(OutboundProxy proxy)
    {
        if (proxy.Kind == OutboundProxyKinds.Warp)
            throw new InvalidOperationException("旧版容器 WARP 已退役，请先迁移至轻量 WARP；已阻止连接或降级为直连");
    }
}
