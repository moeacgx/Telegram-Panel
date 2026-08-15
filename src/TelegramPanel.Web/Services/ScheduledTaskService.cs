using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 计划任务业务服务。
/// </summary>
public sealed class ScheduledTaskService
{
    private readonly IScheduledTaskRepository _scheduledTaskRepository;
    private readonly CronExpressionService _cronExpressionService;
    private readonly BatchTaskManagementService _batchTaskManagement;
    private readonly PanelTimeZoneService _timeZone;
    private readonly ImageAssetStorageService _assetStorage;

    public ScheduledTaskService(
        IScheduledTaskRepository scheduledTaskRepository,
        CronExpressionService cronExpressionService,
        BatchTaskManagementService batchTaskManagement,
        PanelTimeZoneService timeZone,
        ImageAssetStorageService assetStorage)
    {
        _scheduledTaskRepository = scheduledTaskRepository;
        _cronExpressionService = cronExpressionService;
        _batchTaskManagement = batchTaskManagement;
        _timeZone = timeZone;
        _assetStorage = assetStorage;
    }

    public async Task<IReadOnlyList<ScheduledTask>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _scheduledTaskRepository.GetAllOrderedAsync(cancellationToken);
    }

    public async Task<ScheduledTask?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _scheduledTaskRepository.GetByIdAsync(id);
    }

    public async Task<ScheduledTask> CreateAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        Normalize(task);
        task.CreatedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = GetNextRunAtUtc(task.CronExpression, DateTime.UtcNow);
        return await _scheduledTaskRepository.AddAsync(task);
    }

    public async Task<ScheduledTask> UpdateAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        Normalize(task);
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = GetNextRunAtUtc(task.CronExpression, DateTime.UtcNow);
        await _scheduledTaskRepository.UpdateAsync(task);
        return task;
    }

    public async Task PauseAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.Status = ScheduledTaskStatuses.Paused;
        task.UpdatedAt = DateTime.UtcNow;
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public async Task ResumeAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.Status = ScheduledTaskStatuses.Enabled;
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = GetNextRunAtUtc(task.CronExpression, DateTime.UtcNow);
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        if (!string.IsNullOrWhiteSpace(task.OwnedAssetScopeId))
            await _assetStorage.DeleteScopeAsync(task.OwnedAssetScopeId, cancellationToken);

        await _scheduledTaskRepository.DeleteAsync(task);
    }

    public async Task<IReadOnlyList<ScheduledTask>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        return await _scheduledTaskRepository.GetEnabledAsync(cancellationToken);
    }

    public async Task<int> CountEnabledAsync(CancellationToken cancellationToken = default)
    {
        return await _scheduledTaskRepository.CountEnabledAsync(cancellationToken);
    }

    public async Task MarkTriggeredAsync(int id, DateTime triggeredAtUtc, int batchTaskId, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.LastRunAtUtc = triggeredAtUtc;
        task.LastBatchTaskId = batchTaskId;
        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = GetNextRunAtUtc(task.CronExpression, triggeredAtUtc);
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public async Task AdvanceNextRunAsync(int id, DateTime fromUtc, CancellationToken cancellationToken = default)
    {
        var task = await _scheduledTaskRepository.GetByIdAsync(id);
        if (task == null)
            return;

        task.UpdatedAt = DateTime.UtcNow;
        task.NextRunAtUtc = GetNextRunAtUtc(task.CronExpression, fromUtc);
        await _scheduledTaskRepository.UpdateAsync(task);
    }

    public async Task<int> RecalculateNextRunsAsync(DateTime fromUtc, CancellationToken cancellationToken = default)
    {
        var tasks = await _scheduledTaskRepository.GetAllOrderedAsync(cancellationToken);
        var updated = 0;
        foreach (var task in tasks.Where(x => x.Status == ScheduledTaskStatuses.Enabled))
        {
            task.UpdatedAt = DateTime.UtcNow;
            task.NextRunAtUtc = GetNextRunAtUtc(task.CronExpression, fromUtc);
            await _scheduledTaskRepository.UpdateAsync(task);
            updated++;
        }

        return updated;
    }

    public async Task<BatchTask?> RunNowAsync(int id, CancellationToken cancellationToken = default)
    {
        var scheduledTask = await _scheduledTaskRepository.GetByIdAsync(id);
        if (scheduledTask == null)
            return null;

        if (scheduledTask.LastBatchTaskId.HasValue)
        {
            var lastBatchTask = await _batchTaskManagement.GetTaskAsync(scheduledTask.LastBatchTaskId.Value);
            if (lastBatchTask?.Status is "pending" or "running" or "paused")
                throw new InvalidOperationException("上次执行任务尚未结束，请等待完成或取消后再手动执行");
        }

        var created = await _batchTaskManagement.CreateTaskAsync(new BatchTask
        {
            Name = NormalizeBatchTaskName(scheduledTask.Name),
            TaskType = scheduledTask.TaskType,
            Total = Math.Max(0, scheduledTask.Total),
            Completed = 0,
            Failed = 0,
            Config = TaskAssetScopeHelper.RemoveAssetScopeId(scheduledTask.ConfigJson)
        });

        var nowUtc = DateTime.UtcNow;
        scheduledTask.LastRunAtUtc = nowUtc;
        scheduledTask.LastBatchTaskId = created.Id;
        scheduledTask.UpdatedAt = nowUtc;
        scheduledTask.NextRunAtUtc = GetNextRunAtUtc(scheduledTask.CronExpression, nowUtc);
        await _scheduledTaskRepository.UpdateAsync(scheduledTask);

        return created;
    }

    private static string? NormalizeBatchTaskName(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Length == 0 ? null : value;
    }

    public string ValidateCronOrThrow(string expression)
    {
        if (!_cronExpressionService.TryValidate(expression, out var error))
            throw new InvalidOperationException(error ?? "Cron 表达式无效");
        return expression.Trim();
    }

    private void Normalize(ScheduledTask task)
    {
        task.TaskType = (task.TaskType ?? string.Empty).Trim();
        task.Name = string.IsNullOrWhiteSpace(task.Name)
            ? task.TaskType
            : task.Name.Trim();
        if (task.Name.Length > 100)
            throw new InvalidOperationException("计划任务名称不能超过 100 个字符");
        task.Status = string.Equals((task.Status ?? string.Empty).Trim(), ScheduledTaskStatuses.Paused, StringComparison.OrdinalIgnoreCase)
            ? ScheduledTaskStatuses.Paused
            : ScheduledTaskStatuses.Enabled;
        task.CronExpression = ValidateCronOrThrow(task.CronExpression);
        task.ConfigJson = string.IsNullOrWhiteSpace(task.ConfigJson) ? null : task.ConfigJson.Trim();
        task.OwnedAssetScopeId = NormalizeNullable(task.OwnedAssetScopeId);
        if (task.Total < 0)
            task.Total = 0;
    }

    private DateTime? GetNextRunAtUtc(string expression, DateTime fromUtc)
    {
        return _cronExpressionService.GetNextOccurrenceUtc(expression, fromUtc, _timeZone.Current);
    }

    private static string? NormalizeNullable(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }
}
