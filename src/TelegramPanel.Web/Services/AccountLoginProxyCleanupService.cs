using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 清理用户关闭页面后遗留的临时登录客户端、WARP 和 Resin 登录身份。
/// </summary>
public sealed class AccountLoginProxyCleanupService : BackgroundService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly AccountLoginProxyStateStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountLoginProxyCleanupService> _logger;

    public AccountLoginProxyCleanupService(
        AccountLoginProxyStateStore store,
        IServiceScopeFactory scopeFactory,
        ILogger<AccountLoginProxyCleanupService> logger)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            await SweepBestEffortAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SweepBestEffortAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止。
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (_store.TryTakeAny(out var state) && state != null)
            {
                if (await CleanupClaimedStateBestEffortAsync(state, cleanupTimeout.Token))
                    continue;
                break;
            }
        }
    }

    private async Task SweepBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 清理失败不能终止宿主；保留数据库恢复入口，下个周期继续重试。
            _logger.LogError(ex, "Login proxy cleanup sweep failed and will be retried");
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - SessionLifetime;
        while (_store.TryTakeExpired(cutoff, out var state) && state != null)
        {
            if (await CleanupClaimedStateBestEffortAsync(state, cancellationToken))
                continue;
            break;
        }
    }

    private async Task<bool> CleanupClaimedStateBestEffortAsync(
        AccountLoginProxyState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<AccountLoginProxyCoordinator>();
            await coordinator.ReleaseClaimedStateAsync(state, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to strictly stop login {LoginId}; frozen proxy state will be retried",
                state.LoginId);
            return false;
        }
    }

}
