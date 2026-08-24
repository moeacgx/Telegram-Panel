using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Core.Interfaces;
using TelegramPanel.Core.Services;
using TelegramPanel.Core.Services.Telegram;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 数据同步服务（页面层复用：Home/Settings/Channels）
/// </summary>
public class DataSyncService
{
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static readonly SemaphoreSlim SyncTaskCreationGate = new(1, 1);
    private static readonly JsonSerializerOptions SyncTaskConfigJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly AccountManagementService _accountManagement;
    private readonly ChannelManagementService _channelManagement;
    private readonly GroupManagementService _groupManagement;
    private readonly IChannelService _channelService;
    private readonly IGroupService _groupService;
    private readonly AccountTelegramToolsService _telegramTools;
    private readonly BatchTaskManagementService _taskManagement;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataSyncService> _logger;

    public DataSyncService(
        AccountManagementService accountManagement,
        ChannelManagementService channelManagement,
        GroupManagementService groupManagement,
        IChannelService channelService,
        IGroupService groupService,
        AccountTelegramToolsService telegramTools,
        BatchTaskManagementService taskManagement,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DataSyncService> logger)
    {
        _accountManagement = accountManagement;
        _channelManagement = channelManagement;
        _groupManagement = groupManagement;
        _channelService = channelService;
        _groupService = groupService;
        _telegramTools = telegramTools;
        _taskManagement = taskManagement;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<TrackedSyncResult> RunAllActiveAccountsTrackedAsync(string trigger, CancellationToken cancellationToken)
    {
        var task = await CreateTrackedTaskIfNoActiveAsync(trigger, throwIfActive: true, cancellationToken);
        return await ExecuteTrackedSyncAsync(task.Id, trigger, cancellationToken);
    }

    public async Task<int> StartAllActiveAccountsTrackedInBackgroundAsync(string trigger, CancellationToken cancellationToken = default)
    {
        var task = await CreateTrackedTaskIfNoActiveAsync(trigger, throwIfActive: false, cancellationToken);
        var taskId = task.Id;
        if (task.Status != "pending")
            return taskId;

        var scopeFactory = _scopeFactory;
        var logger = _logger;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dataSync = scope.ServiceProvider.GetRequiredService<DataSyncService>();
                await dataSync.ExecuteTrackedSyncAsync(taskId, trigger, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Manual account sync background execution failed unexpectedly for task {TaskId}", taskId);
            }
        }, CancellationToken.None);

        return taskId;
    }

    private async Task<BatchTask> CreateTrackedTaskIfNoActiveAsync(string trigger, bool throwIfActive, CancellationToken cancellationToken)
    {
        await SyncTaskCreationGate.WaitAsync(cancellationToken);
        try
        {
            var activeTask = await FindActiveAccountSyncTaskAsync();
            if (activeTask != null)
            {
                if (throwIfActive)
                    throw new InvalidOperationException($"账号数据同步已在运行，请到任务中心查看当前进度 #{activeTask.Id}");

                return activeTask;
            }

            return await CreateTrackedTaskAsync(trigger, cancellationToken);
        }
        finally
        {
            SyncTaskCreationGate.Release();
        }
    }

    private async Task<BatchTask?> FindActiveAccountSyncTaskAsync()
    {
        var activeTasks = (await _taskManagement.GetTasksByStatusAsync("running"))
            .Concat(await _taskManagement.GetTasksByStatusAsync("pending"))
            .Concat(await _taskManagement.GetTasksByStatusAsync("paused"))
            .Where(t => string.Equals(t.TaskType, BatchTaskTypes.AccountAutoSync, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Status == "running" ? 0 : t.Status == "pending" ? 1 : 2)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        return activeTasks.FirstOrDefault();
    }

    private async Task<BatchTask> CreateTrackedTaskAsync(string trigger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (accounts, skippedAccounts) = SplitSyncEligibleAccounts(await GetDistinctActiveAccountsAsync());

        return await _taskManagement.CreateTaskAsync(new BatchTask
        {
            TaskType = BatchTaskTypes.AccountAutoSync,
            Total = accounts.Count,
            Config = BuildSyncTaskConfig(
                trigger: trigger,
                totalAccounts: accounts.Count,
                processedAccounts: 0,
                failedAccounts: 0,
                totalChannelsSynced: 0,
                totalGroupsSynced: 0,
                failures: Array.Empty<SyncFailureItem>(),
                skippedAccounts: skippedAccounts.Select(ToSkippedItem).ToList(),
                error: null)
        });
    }

    public async Task<TrackedSyncResult> ExecuteTrackedSyncAsync(int taskId, string trigger, CancellationToken cancellationToken)
    {
        var gateEntered = false;

        try
        {
            gateEntered = await SyncGate.WaitAsync(0, cancellationToken);
            if (!gateEntered)
                throw new InvalidOperationException("账号数据同步已在运行，请到任务中心查看当前进度");

            var (accounts, skippedAccounts) = SplitSyncEligibleAccounts(await GetDistinctActiveAccountsAsync());
            await _taskManagement.UpdateTaskDraftAsync(
                taskId,
                accounts.Count,
                BuildSyncTaskConfig(
                    trigger: trigger,
                    totalAccounts: accounts.Count,
                    processedAccounts: 0,
                    failedAccounts: 0,
                    totalChannelsSynced: 0,
                    totalGroupsSynced: 0,
                    failures: Array.Empty<SyncFailureItem>(),
                    skippedAccounts: skippedAccounts.Select(ToSkippedItem).ToList(),
                    error: null));

            await _taskManagement.StartTaskAsync(taskId);

            var summary = await SyncAccountsAsync(
                accounts,
                cancellationToken,
                progressCallback: progress => _taskManagement.UpdateTaskProgressAsync(taskId, progress.ProcessedAccounts, progress.FailedAccounts));

            await _taskManagement.UpdateTaskProgressAsync(taskId, summary.ProcessedAccounts, summary.FailedAccountsCount);
            await _taskManagement.UpdateTaskConfigAsync(
                taskId,
                BuildSyncTaskConfig(
                    trigger: trigger,
                    totalAccounts: summary.TotalAccounts,
                    processedAccounts: summary.ProcessedAccounts,
                    failedAccounts: summary.FailedAccountsCount,
                    totalChannelsSynced: summary.TotalChannelsSynced,
                    totalGroupsSynced: summary.TotalGroupsSynced,
                    failures: summary.AccountFailures.Select(ToFailureItem).ToList(),
                    skippedAccounts: summary.SkippedAccounts.Select(ToSkippedItem).ToList(),
                    error: null));
            await _taskManagement.CompleteTaskAsync(taskId, success: true);

            return new TrackedSyncResult(taskId, summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主停机时保留 running 状态，交给 BatchTaskBackgroundService 下次启动恢复为 pending。
            // 否则容器重启会把正常中断的账号同步任务误标为失败。
            throw;
        }
        catch (Exception ex)
        {
            var snapshot = await _taskManagement.GetTaskAsync(taskId);
            await _taskManagement.UpdateTaskConfigAsync(
                taskId,
                BuildSyncTaskConfig(
                    trigger: trigger,
                    totalAccounts: snapshot?.Total ?? 0,
                    processedAccounts: snapshot?.Completed ?? 0,
                    failedAccounts: snapshot?.Failed ?? 0,
                    totalChannelsSynced: 0,
                    totalGroupsSynced: 0,
                    failures: Array.Empty<SyncFailureItem>(),
                    skippedAccounts: Array.Empty<SyncSkippedItem>(),
                    error: ex.Message));
            await _taskManagement.CompleteTaskAsync(taskId, success: false);
            throw;
        }
        finally
        {
            if (gateEntered)
                SyncGate.Release();
        }
    }

    private async Task<List<Account>> GetDistinctActiveAccountsAsync()
    {
        return (await _accountManagement.GetActiveAccountsAsync())
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
    }

    private static (List<Account> Eligible, List<Account> Skipped) SplitSyncEligibleAccounts(IEnumerable<Account> accounts)
    {
        var eligible = new List<Account>();
        var skipped = new List<Account>();

        foreach (var account in accounts.Where(x => x != null)
                     .GroupBy(x => x.Id)
                     .Select(x => x.First()))
        {
            if (ShouldSkipAccountDataSync(account))
                skipped.Add(account);
            else
                eligible.Add(account);
        }

        return (eligible, skipped);
    }

    private static bool ShouldSkipAccountDataSync(Account account)
    {
        var statusText = $"{account.TelegramStatusSummary} {account.TelegramStatusDetails}".Trim();
        if (statusText.Length == 0)
            return false;

        var compact = statusText
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("　", string.Empty, StringComparison.Ordinal);

        return statusText.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase)
               || statusText.Contains("SESSION_REVOKED", StringComparison.OrdinalIgnoreCase)
               || statusText.Contains("AUTH_KEY_DUPLICATED", StringComparison.OrdinalIgnoreCase)
               || statusText.Contains("Can't read session block", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("Session失效", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("session已失效", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("Session已被撤销", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("Session无法读取", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("Session冲突", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("账号未登录", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SyncSummary> SyncAllActiveAccountsAsync(
        CancellationToken cancellationToken,
        Func<SyncProgress, Task>? progressCallback = null)
    {
        var accounts = await _accountManagement.GetActiveAccountsAsync();
        return await SyncAccountsAsync(accounts, cancellationToken, progressCallback);
    }

    public async Task<SyncSummary> SyncAccountAsync(
        int accountId,
        CancellationToken cancellationToken,
        Func<SyncProgress, Task>? progressCallback = null)
    {
        var account = await _accountManagement.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"账号不存在：{accountId}");

        return await SyncAccountsAsync(new[] { account }, cancellationToken, progressCallback);
    }

    public async Task<SyncSummary> SyncAccountsAsync(
        IEnumerable<Account> accounts,
        CancellationToken cancellationToken,
        Func<SyncProgress, Task>? progressCallback = null)
    {
        var summary = new SyncSummary();

        var accountList = accounts
            .Where(x => x != null)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();

        summary.TotalAccounts = accountList.Count;

        var delayMs = _configuration.GetValue("Telegram:DefaultDelayMs", 2000);
        if (delayMs < 0) delayMs = 0;
        if (delayMs > 60000) delayMs = 60000;

        for (var index = 0; index < accountList.Count; index++)
        {
            var account = accountList[index];
            var accountFailed = false;
            cancellationToken.ThrowIfCancellationRequested();

            if (ShouldSkipAccountDataSync(account))
            {
                summary.SkippedAccounts.Add((account.Id, account.Phone, account.TelegramStatusSummary ?? "Session 不可用"));
                summary.ProcessedAccounts++;
                if (progressCallback != null)
                {
                    await progressCallback(new SyncProgress(
                        TotalAccounts: summary.TotalAccounts,
                        ProcessedAccounts: summary.ProcessedAccounts,
                        FailedAccounts: summary.FailedAccountsCount));
                }

                _logger.LogInformation(
                    "Skipping account data sync because session is not recoverable: {AccountId} {Phone} {Reason}",
                    account.Id,
                    account.Phone,
                    account.TelegramStatusSummary);
                continue;
            }

            _logger.LogInformation(
                "Syncing account {Index}/{Total}: {AccountId} {Phone}",
                index + 1,
                accountList.Count,
                account.Id,
                account.Phone);

            try
            {
                // 同步频道：拉取账号当前可见的全部频道，并记录该账号在频道中的角色关系。
                var channelInfos = await _channelService.GetVisibleChannelsAsync(account.Id, cancellationToken);
                var keepChannelIds = new List<int>(capacity: channelInfos.Count);

                foreach (var channelInfo in channelInfos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var channel = new Channel
                    {
                        TelegramId = channelInfo.TelegramId,
                        AccessHash = channelInfo.AccessHash,
                        Title = channelInfo.Title,
                        Username = channelInfo.Username,
                        IsBroadcast = channelInfo.IsBroadcast,
                        MemberCount = channelInfo.MemberCount,
                        About = channelInfo.About,
                        CreatorAccountId = channelInfo.IsCreator ? account.Id : null,
                        CreatedAt = channelInfo.CreatedAt
                    };

                    var saved = await _channelManagement.CreateOrUpdateChannelAsync(channel);
                    keepChannelIds.Add(saved.Id);

                    await _channelManagement.UpsertAccountChannelAsync(
                        accountId: account.Id,
                        channelId: saved.Id,
                        isCreator: channelInfo.IsCreator,
                        isAdmin: channelInfo.IsAdmin,
                        syncedAtUtc: DateTime.UtcNow);

                    summary.TotalChannelsSynced++;
                }

                await _channelManagement.DeleteStaleAccountChannelsAsync(account.Id, keepChannelIds);

                // 同步群组：拉取账号当前可见的全部群组，并记录该账号在群组中的角色关系。
                var groupInfos = await _groupService.GetVisibleGroupsAsync(account.Id, cancellationToken);
                var keepGroupIds = new List<int>(capacity: groupInfos.Count);

                foreach (var groupInfo in groupInfos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var group = new Group
                    {
                        TelegramId = groupInfo.TelegramId,
                        AccessHash = groupInfo.AccessHash,
                        Title = groupInfo.Title,
                        Username = groupInfo.Username,
                        MemberCount = groupInfo.MemberCount,
                        About = groupInfo.About,
                        CreatorAccountId = groupInfo.IsCreator ? account.Id : null,
                        CreatedAt = groupInfo.CreatedAt
                    };

                    var saved = await _groupManagement.CreateOrUpdateGroupAsync(group);
                    keepGroupIds.Add(saved.Id);

                    await _groupManagement.UpsertAccountGroupAsync(
                        accountId: account.Id,
                        groupId: saved.Id,
                        isCreator: groupInfo.IsCreator,
                        isAdmin: groupInfo.IsAdmin,
                        syncedAtUtc: DateTime.UtcNow);

                    summary.TotalGroupsSynced++;
                }

                await _groupManagement.DeleteStaleAccountGroupsAsync(account.Id, keepGroupIds);

                await _telegramTools.EnsureEstimatedRegistrationAsync(account.Id, cancellationToken);

                await MarkAccountSyncSucceededAsync(account);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;

                accountFailed = true;
                _logger.LogDebug(ex, "Account sync failed (debug details): {AccountId}", account.Id);
                summary.AccountFailures.Add((account.Id, account.Phone, ex.Message));

                // 单个 Telegram 请求被取消通常只是本次网络/代理请求中断，不能据此把账号标记为失效。
                // 只有整个同步任务的取消令牌已触发时，才由上层任务执行器负责中断和恢复。
                if (IsTransientRequestCancellation(ex, cancellationToken))
                {
                    _logger.LogWarning(
                        ex,
                        "Account sync request was canceled transiently; preserving Telegram status: {AccountId} {Phone}",
                        account.Id,
                        account.Phone);
                }
                else
                {
                    // 其他同步失败仍更新账号的 Telegram 状态
                    try
                    {
                        var (statusSummary, statusDetails) = AccountTelegramToolsService.MapTelegramException(ex);
                        _logger.LogWarning("Account sync failed: {Phone} {Summary}", account.Phone, statusSummary);

                        // FLOOD_WAIT 之类属于“限流/临时状态”，不代表账号异常：避免把正常账号标红。
                        // 同理：某些群组接口不支持也不应影响账号状态。
                        var shouldPersistStatus = true;
                        if (statusSummary.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase)
                            || statusSummary.Contains("CHANNEL_MONOFORUM_UNSUPPORTED", StringComparison.OrdinalIgnoreCase))
                        {
                            shouldPersistStatus = false;
                        }

                        var updatedByProbe = false;
                        if (string.Equals(statusSummary, "连接失败", StringComparison.OrdinalIgnoreCase))
                        {
                            // 同步操作里遇到的“连接失败”有可能是误判：这里做一次轻量探测（等同于“刷新账号状态”不勾深度探测），避免把存活账号标成掉线。
                            try
                            {
                                var probe = await _telegramTools.RefreshAccountStatusAsync(account.Id, probeCreateChannel: false, cancellationToken: cancellationToken);
                                if (!string.Equals(probe.Summary, "连接失败", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(probe.Summary, "已取消", StringComparison.OrdinalIgnoreCase))
                                {
                                    // RefreshAccountStatusAsync 内部已持久化账号状态，这里避免覆盖回“连接失败”。
                                    updatedByProbe = true;
                                }
                            }
                            catch (Exception probeEx)
                            {
                                _logger.LogWarning(probeEx, "Account sync fallback status probe failed: {AccountId}", account.Id);
                            }
                        }

                        if (!updatedByProbe && shouldPersistStatus)
                        {
                            account.TelegramStatusOk = false;
                            account.TelegramStatusSummary = statusSummary;
                            account.TelegramStatusDetails = statusDetails;
                            account.TelegramStatusCheckedAtUtc = DateTime.UtcNow;
                            await _accountManagement.UpdateAccountAsync(account);
                        }
                    }
                    catch (Exception statusEx)
                    {
                        _logger.LogWarning(statusEx, "Failed to update Telegram status for account {AccountId}", account.Id);
                    }
                }
            }
            finally
            {
                summary.ProcessedAccounts++;
                if (!accountFailed)
                    summary.SucceededAccounts++;

                if (progressCallback != null)
                {
                    await progressCallback(new SyncProgress(
                        TotalAccounts: summary.TotalAccounts,
                        ProcessedAccounts: summary.ProcessedAccounts,
                        FailedAccounts: summary.FailedAccountsCount));
                }
            }

            // 降速：同步多个账号时插入延迟，降低触发 FLOOD_WAIT 的概率
            if (delayMs > 0 && index < accountList.Count - 1)
            {
                var jitter = Random.Shared.Next(0, Math.Min(500, delayMs + 1));
                await Task.Delay(delayMs + jitter, cancellationToken);
            }
        }

        return summary;
    }

    private static SyncFailureItem ToFailureItem((int AccountId, string Phone, string Error) failure)
    {
        return new SyncFailureItem(failure.AccountId, failure.Phone, failure.Error);
    }

    private static SyncSkippedItem ToSkippedItem((int AccountId, string Phone, string Reason) skipped)
    {
        return new SyncSkippedItem(skipped.AccountId, skipped.Phone, skipped.Reason);
    }

    private static SyncSkippedItem ToSkippedItem(Account account)
    {
        return new SyncSkippedItem(account.Id, account.Phone, account.TelegramStatusSummary ?? "Session 不可用");
    }

    internal static bool IsTransientRequestCancellation(Exception exception, CancellationToken syncCancellationToken)
    {
        return !syncCancellationToken.IsCancellationRequested
               && exception is OperationCanceledException;
    }

    internal static string BuildSyncTaskConfig(
        string trigger,
        int totalAccounts,
        int processedAccounts,
        int failedAccounts,
        int totalChannelsSynced,
        int totalGroupsSynced,
        IReadOnlyCollection<SyncFailureItem> failures,
        IReadOnlyCollection<SyncSkippedItem> skippedAccounts,
        string? error)
    {
        var payload = new
        {
            trigger = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim(),
            scope = "all_active_accounts",
            includes = new[]
            {
                "visible_channels_sync",
                "visible_groups_sync",
                "lightweight_telegram_status_refresh_on_sync_error",
                "successful_sync_clears_transient_telegram_status"
            },
            excludes = new[]
            {
                "deep_telegram_status_probe",
                "verification_code_collection"
            },
            progress = new
            {
                totalAccounts,
                processedAccounts,
                failedAccounts
            },
            result = new
            {
                totalChannelsSynced,
                totalGroupsSynced
            },
            failures = failures.Take(50).ToList(),
            skippedAccounts = skippedAccounts.Take(100).ToList(),
            error
        };

        return JsonSerializer.Serialize(payload, SyncTaskConfigJsonOptions);
    }

    private async Task MarkAccountSyncSucceededAsync(Account account)
    {
        if (ShouldMarkTelegramStatusOkAfterSuccessfulSync(account))
        {
            var now = DateTime.UtcNow;
            account.LastSyncAt = now;
            account.TelegramStatusOk = true;
            account.TelegramStatusSummary = "正常";
            account.TelegramStatusDetails = "后台自动同步成功：频道/群组同步已完成，账号可连接 Telegram；未执行深度探测。";
            account.TelegramStatusCheckedAtUtc = now;
            await _accountManagement.UpdateAccountAsync(account);
            return;
        }

        await _accountManagement.UpdateLastSyncTimeAsync(account.Id);
    }

    private static bool ShouldMarkTelegramStatusOkAfterSuccessfulSync(Account account)
    {
        var summary = (account.TelegramStatusSummary ?? string.Empty).Trim();
        if (summary.Length == 0)
            return true;

        if (string.Equals(summary, "正常", StringComparison.OrdinalIgnoreCase))
            return true;

        return summary.Contains("连接失败", StringComparison.OrdinalIgnoreCase)
               || summary.Contains("请求超时", StringComparison.OrdinalIgnoreCase)
               || summary.Contains("刷新失败", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record SyncFailureItem(
        [property: JsonPropertyName("accountId")] int AccountId,
        [property: JsonPropertyName("phone")] string Phone,
        [property: JsonPropertyName("error")] string Error);
    internal sealed record SyncSkippedItem(int AccountId, string Phone, string Reason);

    public sealed class SyncSummary
    {
        public int TotalAccounts { get; set; }
        public int ProcessedAccounts { get; set; }
        public int SucceededAccounts { get; set; }
        public int FailedAccountsCount => AccountFailures.Count;
        public int TotalChannelsSynced { get; set; }
        public int TotalGroupsSynced { get; set; }
        public List<(int AccountId, string Phone, string Error)> AccountFailures { get; } = new();
        public List<(int AccountId, string Phone, string Reason)> SkippedAccounts { get; } = new();
    }

    public readonly record struct SyncProgress(int TotalAccounts, int ProcessedAccounts, int FailedAccounts);

    public readonly record struct TrackedSyncResult(int TaskId, SyncSummary Summary);
}
