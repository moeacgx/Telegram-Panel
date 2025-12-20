using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 批量任务后台执行器：从数据库拉取 pending 任务并在后台静默运行。
/// </summary>
public sealed class BatchTaskBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BatchTaskBackgroundService> _logger;
    private readonly IConfiguration _configuration;

    public BatchTaskBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<BatchTaskBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue("BatchTasks:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("Batch task runner disabled (BatchTasks:Enabled=false)");
            return;
        }

        var seconds = _configuration.GetValue("BatchTasks:PollIntervalSeconds", 2);
        if (seconds < 1) seconds = 1;
        if (seconds > 30) seconds = 30;
        var interval = TimeSpan.FromSeconds(seconds);

        _logger.LogInformation("Batch task runner started, interval {IntervalSeconds} seconds", seconds);

        // 延迟一点，避免与启动时 DB 迁移抢资源
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryRunOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch task runner loop failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task TryRunOneAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskManagement = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var channelManagement = scope.ServiceProvider.GetRequiredService<ChannelManagementService>();
        var channelService = scope.ServiceProvider.GetRequiredService<IChannelService>();
        var accountTelegramTools = scope.ServiceProvider.GetRequiredService<AccountTelegramToolsService>();

        var pending = (await taskManagement.GetTasksByStatusAsync("pending"))
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefault();

        if (pending == null)
            return;

        await taskManagement.StartTaskAsync(pending.Id);
        _logger.LogInformation("Batch task started: {TaskId} {TaskType}", pending.Id, pending.TaskType);

        var completed = pending.Completed;
        var failed = pending.Failed;

        try
        {
            switch (pending.TaskType)
            {
                case BatchTaskTypes.Invite:
                    (completed, failed) = await RunInviteAsync(pending, taskManagement, channelManagement, channelService, completed, failed, cancellationToken);
                    break;
                case BatchTaskTypes.SetAdmin:
                    (completed, failed) = await RunSetAdminAsync(pending, taskManagement, channelManagement, channelService, completed, failed, cancellationToken);
                    break;
                case BatchTaskTypes.UserJoinSubscribe:
                    (completed, failed) = await RunUserJoinSubscribeAsync(pending, taskManagement, accountTelegramTools, completed, failed, cancellationToken);
                    break;
                default:
                    failed = pending.Total == 0 ? 1 : pending.Total;
                    await taskManagement.UpdateTaskProgressAsync(pending.Id, completed, failed);
                    await taskManagement.CompleteTaskAsync(pending.Id, success: false);
                    _logger.LogWarning("Unsupported batch task type: {TaskType} (task {TaskId})", pending.TaskType, pending.Id);
                    return;
            }

            // 如果任务被用户取消（当前实现：Cancel 会把状态写成 failed），则不覆盖它
            var latest = await taskManagement.GetTaskAsync(pending.Id);
            if (latest != null && latest.Status != "running")
                return;

            await taskManagement.CompleteTaskAsync(pending.Id, success: failed == 0);
            _logger.LogInformation("Batch task completed: {TaskId} {TaskType} (ok={Ok}, completed={Completed}, failed={Failed})",
                pending.Id, pending.TaskType, failed == 0, completed, failed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch task failed: {TaskId} {TaskType}", pending.Id, pending.TaskType);
            try
            {
                await taskManagement.UpdateTaskProgressAsync(pending.Id, completed, failed == 0 ? 1 : failed);
                await taskManagement.CompleteTaskAsync(pending.Id, success: false);
            }
            catch
            {
                // ignore secondary failures
            }
        }
    }

    private static async Task<(int completed, int failed)> RunInviteAsync(
        BatchTask task,
        BatchTaskManagementService taskManagement,
        ChannelManagementService channelManagement,
        IChannelService channelService,
        int completed,
        int failed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.Config))
            throw new InvalidOperationException("任务配置为空");

        var cfg = JsonSerializer.Deserialize<InviteTaskConfig>(task.Config);
        if (cfg == null || cfg.ChannelTelegramIds.Count == 0 || cfg.Usernames.Count == 0)
            throw new InvalidOperationException("任务配置缺少 ChannelTelegramIds 或 Usernames");

        foreach (var channelTelegramId in cfg.ChannelTelegramIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latest = await taskManagement.GetTaskAsync(task.Id);
            if (latest != null && latest.Status != "running")
                return (completed, failed);

            var channel = await channelManagement.GetChannelByTelegramIdAsync(channelTelegramId);
            if (channel == null)
            {
                failed += cfg.Usernames.Count;
                await taskManagement.UpdateTaskProgressAsync(task.Id, completed, failed);
                continue;
            }

            var executeAccountId = await channelManagement.ResolveExecuteAccountIdAsync(channel, cfg.PreferredExecuteAccountId > 0 ? cfg.PreferredExecuteAccountId : null);
            if (executeAccountId is not > 0)
            {
                failed += cfg.Usernames.Count;
                await taskManagement.UpdateTaskProgressAsync(task.Id, completed, failed);
                continue;
            }

            List<InviteResult> results;
            try
            {
                results = await channelService.BatchInviteUsersAsync(executeAccountId.Value, channelTelegramId, cfg.Usernames, cfg.DelayMs);
            }
            catch (Exception ex)
            {
                results = cfg.Usernames.Select(u => new InviteResult(u, false, ex.Message)).ToList();
            }

            completed += results.Count(r => r.Success);
            failed += results.Count(r => !r.Success);
            await taskManagement.UpdateTaskProgressAsync(task.Id, completed, failed);
        }

        return (completed, failed);
    }

    private static async Task<(int completed, int failed)> RunSetAdminAsync(
        BatchTask task,
        BatchTaskManagementService taskManagement,
        ChannelManagementService channelManagement,
        IChannelService channelService,
        int completed,
        int failed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.Config))
            throw new InvalidOperationException("任务配置为空");

        var cfg = JsonSerializer.Deserialize<SetAdminTaskConfig>(task.Config);
        if (cfg == null || cfg.ChannelTelegramIds.Count == 0 || cfg.Usernames.Count == 0)
            throw new InvalidOperationException("任务配置缺少 ChannelTelegramIds 或 Usernames");

        var rights = (AdminRights)cfg.Rights;
        var adminRequests = cfg.Usernames.Select(u => new AdminRequest(u, rights, cfg.Title)).ToList();

        foreach (var channelTelegramId in cfg.ChannelTelegramIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latest = await taskManagement.GetTaskAsync(task.Id);
            if (latest != null && latest.Status != "running")
                return (completed, failed);

            var channel = await channelManagement.GetChannelByTelegramIdAsync(channelTelegramId);
            if (channel == null)
            {
                failed += cfg.Usernames.Count;
                await taskManagement.UpdateTaskProgressAsync(task.Id, completed, failed);
                continue;
            }

            var executeAccountId = await channelManagement.ResolveExecuteAccountIdAsync(channel, cfg.PreferredExecuteAccountId > 0 ? cfg.PreferredExecuteAccountId : null);
            if (executeAccountId is not > 0)
            {
                failed += cfg.Usernames.Count;
                await taskManagement.UpdateTaskProgressAsync(task.Id, completed, failed);
                continue;
            }

            List<SetAdminResult> results;
            try
            {
                results = await channelService.BatchSetAdminsAsync(executeAccountId.Value, channelTelegramId, adminRequests);
            }
            catch (Exception ex)
            {
                results = cfg.Usernames.Select(u => new SetAdminResult(u, false, ex.Message)).ToList();
            }

            completed += results.Count(r => r.Success);
            failed += results.Count(r => !r.Success);
            await taskManagement.UpdateTaskProgressAsync(task.Id, completed, failed);
        }

        return (completed, failed);
    }

    private static async Task<(int completed, int failed)> RunUserJoinSubscribeAsync(
        BatchTask task,
        BatchTaskManagementService taskManagement,
        AccountTelegramToolsService accountTelegramTools,
        int completed,
        int failed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.Config))
            throw new InvalidOperationException("任务配置为空");

        var cfg = JsonSerializer.Deserialize<UserJoinSubscribeTaskConfig>(task.Config);
        if (cfg == null || cfg.AccountIds.Count == 0 || cfg.Links.Count == 0)
            throw new InvalidOperationException("任务配置缺少 AccountIds 或 Links");

        foreach (var accountId in cfg.AccountIds)
        {
            foreach (var link in cfg.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var latest = await taskManagement.GetTaskAsync(task.Id);
                if (latest != null && latest.Status != "running")
                    return (completed, failed);

                var (ok, _, _) = await accountTelegramTools.JoinChatOrChannelAsync(accountId, link, cancellationToken);
                if (ok) completed++; else failed++;

                await taskManagement.UpdateTaskProgressAsync(task.Id, completed, failed);

                if (cfg.DelayMs > 0)
                    await Task.Delay(cfg.DelayMs + Random.Shared.Next(300, 900), cancellationToken);
            }
        }

        return (completed, failed);
    }

    private sealed record InviteTaskConfig(
        List<long> ChannelTelegramIds,
        List<string> Usernames,
        int DelayMs,
        int PreferredExecuteAccountId
    );

    private sealed record SetAdminTaskConfig(
        List<long> ChannelTelegramIds,
        List<string> Usernames,
        int Rights,
        string Title,
        int PreferredExecuteAccountId
    );

    private sealed record UserJoinSubscribeTaskConfig(
        List<int> AccountIds,
        List<string> Links,
        int DelayMs
    );
}
