using TelegramPanel.Core.Models;
using TelegramPanel.Core.Services.Proxy;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 定期刷新普通代理和 Resin 代理的出口及地理元数据。
/// 受管轻量 WARP 由共享运行器监督进程负责恢复。
/// </summary>
public sealed class ProxyEgressMaintenanceBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProxyEgressMaintenanceBackgroundService> _logger;

    public ProxyEgressMaintenanceBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ProxyEgressMaintenanceBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = ProxyEgressMaintenanceOptions.From(_configuration);
        if (!options.Enabled)
        {
            _logger.LogInformation("Proxy egress maintenance is disabled");
            return;
        }

        if (options.InitialDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(options.InitialDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            options = ProxyEgressMaintenanceOptions.From(_configuration);
            if (!options.Enabled)
                return;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ProxyManagementService>();
                var result = await service.RefreshAllNonWarpProxyEgressAsync(stoppingToken);
                if (result.Failed > 0)
                {
                    _logger.LogWarning(
                        "Proxy egress maintenance completed with {Failed}/{Checked} failures",
                        result.Failed,
                        result.Checked);
                }
                else if (result.Checked > 0)
                {
                    _logger.LogInformation(
                        "Proxy egress maintenance refreshed {Succeeded}/{Checked} proxies",
                        result.Succeeded,
                        result.Checked);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proxy egress maintenance sweep failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(options.IntervalMinutes), stoppingToken);
        }
    }
}
