using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 批量任务管理服务
/// </summary>
public class BatchTaskManagementService
{
    private readonly IBatchTaskRepository _batchTaskRepository;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BatchTaskManagementService> _logger;

    public BatchTaskManagementService(
        IBatchTaskRepository batchTaskRepository,
        IConfiguration configuration,
        ILogger<BatchTaskManagementService> logger)
    {
        _batchTaskRepository = batchTaskRepository;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<BatchTask?> GetTaskAsync(int id)
    {
        return await _batchTaskRepository.GetFreshByIdAsync(id);
    }

    public async Task<IEnumerable<BatchTask>> GetAllTasksAsync()
    {
        return await _batchTaskRepository.GetAllAsync();
    }

    public async Task<IEnumerable<BatchTask>> GetTasksByStatusAsync(string status)
    {
        return await _batchTaskRepository.GetByStatusAsync(status);
    }

    public Task<IReadOnlyList<BatchTask>> GetEligiblePersistentTasksAsync(
        DateTime eligibleAtUtc,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.GetEligiblePersistentTasksAsync(eligibleAtUtc, cancellationToken);

    public async Task<IEnumerable<BatchTask>> GetRunningTasksAsync()
    {
        return await _batchTaskRepository.GetRunningTasksAsync();
    }

    public async Task<IReadOnlyList<BatchTask>> GetActiveTasksAsync(CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.GetActiveTasksAsync(cancellationToken);
    }

    public async Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20)
    {
        return await _batchTaskRepository.GetRecentTasksAsync(count);
    }

    public async Task<IReadOnlyList<BatchTask>> GetTaskCenterItemsAsync(int historyCount = 100, CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.GetTaskCenterItemsAsync(historyCount, cancellationToken);
    }

    public async Task<int> CountActiveTasksAsync(CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.CountActiveTasksAsync(cancellationToken);
    }

    public async Task<int> TrimHistoryTasksAsync(int keepCount, CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.TrimHistoryTasksAsync(keepCount, cancellationToken);
    }

    public Task<BatchTask> CreateTaskAsync(BatchTask task) =>
        CreateTaskAsync(task, "pending");

    public Task<BatchTask> CreateInitializingTaskAsync(BatchTask task) =>
        CreateTaskAsync(task, "initializing");

    private async Task<BatchTask> CreateTaskAsync(BatchTask task, string initialStatus)
    {
        task.OwnerModuleId = string.IsNullOrWhiteSpace(task.OwnerModuleId)
            ? "host.legacy"
            : task.OwnerModuleId.Trim();
        task.ExecutionKind = string.IsNullOrWhiteSpace(task.ExecutionKind)
            ? "batch"
            : task.ExecutionKind.Trim().ToLowerInvariant();
        if (task.ExecutionKind is not ("batch" or "persistent"))
            throw new InvalidOperationException($"不支持的任务执行通道：{task.ExecutionKind}");
        task.CreatedAt = DateTime.UtcNow;
        task.Status = initialStatus;
        return await _batchTaskRepository.AddAsync(task);
    }

    public async Task UpdateTaskProgressAsync(int taskId, int completed, int failed)
    {
        await _batchTaskRepository.UpdateProgressColumnsAsync(taskId, completed, failed);
    }

    public async Task UpdateTaskConfigAsync(int taskId, string? config)
    {
        await _batchTaskRepository.UpdateConfigColumnAsync(taskId, config);
    }

    public async Task UpdateTaskDraftAsync(int taskId, int total, string? config)
    {
        if (total < 0) total = 0;
        await _batchTaskRepository.UpdateDraftColumnsAsync(taskId, total, config);
    }

    public async Task<bool> TryUpdateEditableTaskDraftAsync(
        int taskId,
        int total,
        string? config,
        CancellationToken cancellationToken = default)
    {
        if (total < 0) total = 0;
        return await _batchTaskRepository.TryUpdateEditableDraftAsync(
            taskId,
            total,
            config,
            cancellationToken);
    }

    public async Task<bool> TryUpdateEditableTaskDraftAsync(
        int taskId,
        int total,
        string? config,
        string? name,
        CancellationToken cancellationToken = default)
    {
        if (total < 0) total = 0;
        return await _batchTaskRepository.TryUpdateEditableDraftAsync(
            taskId,
            total,
            config,
            name,
            cancellationToken);
    }

    public async Task StartTaskAsync(int taskId)
    {
        await TryStartTaskAsync(taskId);
    }

    public async Task<bool> TryStartTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.TryStartAsync(taskId, DateTime.UtcNow, cancellationToken);
    }

    public async Task PauseTaskAsync(int taskId)
    {
        await TryPauseTaskAsync(taskId);
    }

    public async Task<bool> TryPauseTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.TryPauseAsync(taskId, cancellationToken);
    }

    public Task UpdateTaskRuntimeStateAsync(
        int taskId,
        string? phase,
        string? message,
        DateTime? heartbeatAtUtc,
        bool requiresAttention,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.UpdateRuntimeStateColumnsAsync(
            taskId,
            NormalizeRuntimeValue(phase, 100),
            NormalizeRuntimeValue(message, 1000),
            heartbeatAtUtc,
            requiresAttention,
            cancellationToken);

    public Task<bool> TryBeginPauseTaskAsync(int taskId, CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryBeginPauseAsync(taskId, cancellationToken);

    public Task<bool> TryConfirmPausedTaskAsync(int taskId, CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryConfirmPausedAsync(taskId, cancellationToken);

    public async Task ResumeTaskAsync(int taskId)
    {
        await TryResumeTaskAsync(taskId);
    }

    public async Task<bool> TryResumeTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _batchTaskRepository.TryResumeAsync(taskId, cancellationToken);
    }

    public Task<bool> TryDeferTaskAsync(
        int taskId,
        DateTime nextEligibleAtUtc,
        string? reason,
        DateTime heartbeatAtUtc,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryDeferAsync(
            taskId,
            nextEligibleAtUtc,
            NormalizeRuntimeValue(reason, 1000),
            heartbeatAtUtc,
            cancellationToken);

    public Task<bool> TryActivateInitializedTaskAsync(
        int taskId,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryActivateInitializedAsync(taskId, cancellationToken);

    public Task<bool> TryBeginEditableTaskUpdateAsync(
        int taskId,
        int total,
        string? config,
        string? name,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryBeginEditableUpdateAsync(
            taskId,
            Math.Max(0, total),
            config,
            name,
            cancellationToken);

    public Task<bool> TryFinishEditableTaskUpdateAsync(
        int taskId,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryFinishEditableUpdateAsync(taskId, cancellationToken);

    public Task<bool> TryRollbackEditableTaskUpdateAsync(
        int taskId,
        int total,
        string? config,
        string? name,
        string? reason,
        DateTime heartbeatAtUtc,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryRollbackEditableUpdateAsync(
            taskId,
            Math.Max(0, total),
            config,
            name,
            NormalizeRuntimeValue(reason, 1000),
            heartbeatAtUtc,
            cancellationToken);

    public Task<bool> TryFailTaskTransitionAsync(
        int taskId,
        string expectedStatus,
        string? reason,
        DateTime failedAtUtc,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryFailTransitionAsync(
            taskId,
            expectedStatus,
            NormalizeRuntimeValue(reason, 1000),
            failedAtUtc,
            cancellationToken);

    public Task<bool> TryCompletePersistentTaskAsync(
        int taskId,
        int completed,
        int failed,
        string? message,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default) =>
        _batchTaskRepository.TryCompletePersistentAsync(
            taskId,
            Math.Max(0, completed),
            Math.Max(0, failed),
            NormalizeRuntimeValue(message, 1000),
            completedAtUtc,
            cancellationToken);

    public async Task<int> RequeueRunningTasksAsync(
        Func<BatchTask, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var runningTasks = (await _batchTaskRepository.GetByStatusAsync("running")).ToList();
        if (runningTasks.Count == 0)
            return 0;

        var requeued = 0;
        foreach (var task in runningTasks)
        {
            if (predicate != null && !predicate(task))
                continue;

            if (await _batchTaskRepository.TryRequeueAsync(task.Id, cancellationToken))
                requeued++;
        }

        return requeued;
    }

    public async Task<(int Requeued, int Paused)> RecoverInterruptedTasksAsync(
        CancellationToken cancellationToken = default)
    {
        var requeued = await RequeueRunningTasksAsync(cancellationToken: cancellationToken);
        var paused = 0;
        var pausingTasks = (await _batchTaskRepository.GetByStatusAsync("pausing")).ToList();
        foreach (var task in pausingTasks)
        {
            if (await _batchTaskRepository.TryConfirmPausedAsync(task.Id, cancellationToken))
                paused++;
        }

        return (requeued, paused);
    }

    public async Task CompleteTaskAsync(int taskId, bool success = true)
    {
        var transitioned = await _batchTaskRepository.TryCompleteAsync(
            taskId,
            success,
            DateTime.UtcNow);
        if (!transitioned)
            return;
    }

    public async Task CancelTaskAsync(int taskId)
    {
        await TryCancelTaskAsync(taskId);
    }

    public async Task<bool> TryCancelTaskAsync(
        int taskId,
        CancellationToken cancellationToken = default)
    {
        var transitioned = await _batchTaskRepository.TryCancelAsync(
            taskId,
            DateTime.UtcNow,
            cancellationToken);
        return transitioned;
    }

    public async Task DeleteTaskAsync(int id)
    {
        var task = await _batchTaskRepository.GetFreshByIdAsync(id);
        if (task != null)
        {
            await _batchTaskRepository.DeleteAsync(task);
        }
    }

    private static string? NormalizeRuntimeValue(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized?.Length > maxLength ? normalized[..maxLength] : normalized;
    }

}
