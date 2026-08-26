using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 批量任务仓储实现
/// </summary>
public class BatchTaskRepository : Repository<BatchTask>, IBatchTaskRepository
{
    public BatchTaskRepository(AppDbContext context) : base(context)
    {
    }

    public async Task<BatchTask?> GetFreshByIdAsync(int id)
    {
        DetachTrackedEntity(id);
        return await _dbSet.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task UpdateFreshAsync(BatchTask entity)
    {
        DetachTrackedEntity(entity.Id);
        _dbSet.Update(entity);
        await SaveChangesWithSqliteLockRetryAsync();
    }

    public Task<bool> TryStartAsync(int id, DateTime startedAt, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id
                && t.Status == "pending"
                && (t.NextEligibleAtUtc == null || t.NextEligibleAtUtc <= startedAt)),
            setters => setters
                .SetProperty(t => t.Status, "running")
                .SetProperty(t => t.StartedAt, startedAt)
                .SetProperty(t => t.CompletedAt, (DateTime?)null)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null)
                .SetProperty(t => t.RuntimePhase, "running")
                .SetProperty(t => t.RuntimeMessage, (string?)null)
                .SetProperty(t => t.HeartbeatAtUtc, startedAt)
                .SetProperty(t => t.RequiresAttention, false),
            cancellationToken);
    }

    public Task<bool> TryPauseAsync(int id, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && (t.Status == "pending" || t.Status == "running")),
            setters => setters
                .SetProperty(t => t.Status, "paused")
                .SetProperty(t => t.CompletedAt, (DateTime?)null)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null),
            cancellationToken);
    }

    public Task<bool> TryBeginPauseAsync(int id, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && (t.Status == "pending" || t.Status == "running")),
            setters => setters
                .SetProperty(t => t.Status, "pausing")
                .SetProperty(t => t.CompletedAt, (DateTime?)null)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null),
            cancellationToken);
    }

    public Task<bool> TryConfirmPausedAsync(int id, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "pausing"),
            setters => setters
                .SetProperty(t => t.Status, "paused")
                .SetProperty(t => t.CompletedAt, (DateTime?)null),
            cancellationToken);
    }

    public Task<bool> TryResumeAsync(int id, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "paused"),
            setters => setters
                .SetProperty(t => t.Status, "pending")
                .SetProperty(t => t.StartedAt, (DateTime?)null)
                .SetProperty(t => t.CompletedAt, (DateTime?)null)
                .SetProperty(t => t.RuntimePhase, (string?)null)
                .SetProperty(t => t.RuntimeMessage, (string?)null)
                .SetProperty(t => t.HeartbeatAtUtc, (DateTime?)null)
                .SetProperty(t => t.RequiresAttention, false)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null),
            cancellationToken);
    }

    public Task<bool> TryCancelAsync(int id, DateTime completedAt, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id
                && (t.Status == "pending" || t.Status == "running" || t.Status == "pausing" || t.Status == "paused")),
            setters => setters
                .SetProperty(t => t.Status, "canceled")
                .SetProperty(t => t.CompletedAt, completedAt)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null),
            cancellationToken);
    }

    public Task<bool> TryCompleteAsync(int id, bool success, DateTime completedAt, CancellationToken cancellationToken = default)
    {
        var status = success ? "completed" : "failed";
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "running"),
            setters => setters
                .SetProperty(t => t.Status, status)
                .SetProperty(t => t.CompletedAt, completedAt)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null),
            cancellationToken);
    }

    public Task<bool> TryRequeueAsync(int id, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "running"),
            setters => setters
                .SetProperty(t => t.Status, "pending")
                .SetProperty(t => t.StartedAt, (DateTime?)null)
                .SetProperty(t => t.CompletedAt, (DateTime?)null)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null),
            cancellationToken);
    }

    public Task<bool> TryCompletePersistentAsync(
        int id,
        int completed,
        int failed,
        string? runtimeMessage,
        DateTime completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "running"),
            setters => setters
                .SetProperty(t => t.Status, "completed")
                .SetProperty(t => t.Completed, completed)
                .SetProperty(t => t.Failed, failed)
                .SetProperty(t => t.CompletedAt, completedAtUtc)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null)
                .SetProperty(t => t.RuntimePhase, "completed")
                .SetProperty(t => t.RuntimeMessage, runtimeMessage)
                .SetProperty(t => t.HeartbeatAtUtc, completedAtUtc)
                .SetProperty(t => t.RequiresAttention, false),
            cancellationToken);
    }

    public Task<bool> TryDeferAsync(
        int id,
        DateTime nextEligibleAtUtc,
        string? runtimeMessage,
        DateTime heartbeatAtUtc,
        CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "running"),
            setters => setters
                .SetProperty(t => t.Status, "pending")
                .SetProperty(t => t.StartedAt, (DateTime?)null)
                .SetProperty(t => t.CompletedAt, (DateTime?)null)
                .SetProperty(t => t.NextEligibleAtUtc, nextEligibleAtUtc)
                .SetProperty(t => t.RuntimePhase, "deferred")
                .SetProperty(t => t.RuntimeMessage, runtimeMessage)
                .SetProperty(t => t.HeartbeatAtUtc, heartbeatAtUtc)
                .SetProperty(t => t.RequiresAttention, false),
            cancellationToken);
    }

    public Task<bool> TryActivateInitializedAsync(int id, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "initializing"),
            setters => setters
                .SetProperty(t => t.Status, "pending")
                .SetProperty(t => t.RuntimePhase, (string?)null)
                .SetProperty(t => t.RuntimeMessage, (string?)null)
                .SetProperty(t => t.HeartbeatAtUtc, (DateTime?)null)
                .SetProperty(t => t.RequiresAttention, false),
            cancellationToken);
    }

    public Task<bool> TryBeginEditableUpdateAsync(
        int id,
        int total,
        string? config,
        string? name,
        CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "paused"),
            setters => setters
                .SetProperty(t => t.Status, "updating")
                .SetProperty(t => t.Name, name)
                .SetProperty(t => t.Total, total)
                .SetProperty(t => t.Config, config)
                .SetProperty(t => t.RuntimePhase, "updating")
                .SetProperty(t => t.RuntimeMessage, "正在提交模块配置")
                .SetProperty(t => t.HeartbeatAtUtc, DateTime.UtcNow)
                .SetProperty(t => t.RequiresAttention, false),
            cancellationToken);
    }

    public Task<bool> TryFinishEditableUpdateAsync(int id, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "updating"),
            setters => setters
                .SetProperty(t => t.Status, "paused")
                .SetProperty(t => t.RuntimePhase, (string?)null)
                .SetProperty(t => t.RuntimeMessage, (string?)null)
                .SetProperty(t => t.HeartbeatAtUtc, (DateTime?)null)
                .SetProperty(t => t.RequiresAttention, false),
            cancellationToken);
    }

    public Task<bool> TryRollbackEditableUpdateAsync(
        int id,
        int total,
        string? config,
        string? name,
        string? runtimeMessage,
        DateTime heartbeatAtUtc,
        CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "updating"),
            setters => setters
                .SetProperty(t => t.Status, "paused")
                .SetProperty(t => t.Name, name)
                .SetProperty(t => t.Total, total)
                .SetProperty(t => t.Config, config)
                .SetProperty(t => t.RuntimePhase, "update_rolled_back")
                .SetProperty(t => t.RuntimeMessage, runtimeMessage)
                .SetProperty(t => t.HeartbeatAtUtc, heartbeatAtUtc)
                .SetProperty(t => t.RequiresAttention, true),
            cancellationToken);
    }

    public Task<bool> TryFailTransitionAsync(
        int id,
        string expectedStatus,
        string? runtimeMessage,
        DateTime failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == expectedStatus),
            setters => setters
                .SetProperty(t => t.Status, "failed")
                .SetProperty(t => t.CompletedAt, failedAtUtc)
                .SetProperty(t => t.NextEligibleAtUtc, (DateTime?)null)
                .SetProperty(t => t.RuntimePhase, expectedStatus + "_failed")
                .SetProperty(t => t.RuntimeMessage, runtimeMessage)
                .SetProperty(t => t.HeartbeatAtUtc, failedAtUtc)
                .SetProperty(t => t.RequiresAttention, true),
            cancellationToken);
    }

    public async Task UpdateProgressColumnsAsync(int id, int completed, int failed, CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateWithSqliteLockRetryAsync(
            token => _dbSet.Where(t => t.Id == id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Completed, completed)
                    .SetProperty(t => t.Failed, failed),
                token),
            cancellationToken);
    }

    public async Task UpdateRuntimeStateColumnsAsync(
        int id,
        string? phase,
        string? message,
        DateTime? heartbeatAtUtc,
        bool requiresAttention,
        CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateWithSqliteLockRetryAsync(
            token => _dbSet.Where(t => t.Id == id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.RuntimePhase, phase)
                    .SetProperty(t => t.RuntimeMessage, message)
                    .SetProperty(t => t.HeartbeatAtUtc, heartbeatAtUtc)
                    .SetProperty(t => t.RequiresAttention, requiresAttention),
                token),
            cancellationToken);
    }

    public async Task UpdateConfigColumnAsync(int id, string? config, CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateWithSqliteLockRetryAsync(
            token => _dbSet.Where(t => t.Id == id).ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.Config, config), token),
            cancellationToken);
    }

    public async Task UpdateDraftColumnsAsync(int id, int total, string? config, CancellationToken cancellationToken = default)
    {
        await ExecuteUpdateWithSqliteLockRetryAsync(
            token => _dbSet.Where(t => t.Id == id).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Total, total)
                    .SetProperty(t => t.Config, config),
                token),
            cancellationToken);
    }

    public Task<bool> TryUpdateEditableDraftAsync(int id, int total, string? config, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "paused"),
            setters => setters
                .SetProperty(t => t.Total, total)
                .SetProperty(t => t.Config, config),
            cancellationToken);
    }

    public Task<bool> TryUpdateEditableDraftAsync(int id, int total, string? config, string? name, CancellationToken cancellationToken = default)
    {
        return ExecuteConditionalUpdateAsync(
            _dbSet.Where(t => t.Id == id && t.Status == "paused"),
            setters => setters
                .SetProperty(t => t.Name, name)
                .SetProperty(t => t.Total, total)
                .SetProperty(t => t.Config, config),
            cancellationToken);
    }

    public async Task<IEnumerable<BatchTask>> GetByStatusAsync(string status)
    {
        return await _dbSet
            .Where(t => t.Status == status)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<BatchTask>> GetEligiblePersistentTasksAsync(
        DateTime eligibleAtUtc,
        CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(t => t.ExecutionKind == "persistent"
                && t.Status == "pending"
                && (t.NextEligibleAtUtc == null || t.NextEligibleAtUtc <= eligibleAtUtc))
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => ToListItem(t))
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<BatchTask>> GetRunningTasksAsync()
    {
        return await _dbSet
            .Where(t => t.Status == "running")
            .OrderBy(t => t.StartedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<BatchTask>> GetActiveTasksAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(t => t.Status == "initializing" || t.Status == "updating"
                || t.Status == "pending" || t.Status == "running"
                || t.Status == "pausing" || t.Status == "paused")
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => ToListItem(t))
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(t => t.Status == "completed" || t.Status == "failed" || t.Status == "canceled")
            .OrderByDescending(t => t.CreatedAt)
            .Take(count)
            .Select(t => ToListItem(t))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<BatchTask>> GetTaskCenterItemsAsync(int historyCount = 100, CancellationToken cancellationToken = default)
    {
        if (historyCount <= 0)
            historyCount = 100;
        if (historyCount > 500)
            historyCount = 500;

        var activeTasks = await _dbSet
            .AsNoTracking()
            .Where(t => t.Status == "initializing" || t.Status == "updating"
                || t.Status == "pending" || t.Status == "running"
                || t.Status == "pausing" || t.Status == "paused")
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => ToListItem(t))
            .ToListAsync(cancellationToken);

        var historyTasks = await _dbSet
            .AsNoTracking()
            .Where(t => t.Status == "completed" || t.Status == "failed" || t.Status == "canceled")
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Take(historyCount)
            .Select(t => ToListItem(t))
            .ToListAsync(cancellationToken);

        return activeTasks
            .Concat(historyTasks)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .OrderByDescending(t => t.CreatedAt)
            .ToList();
    }

    private static BatchTask ToListItem(BatchTask task) => new()
    {
        Id = task.Id,
        Name = task.Name,
        TaskType = task.TaskType,
        OwnerModuleId = task.OwnerModuleId,
        ExecutionKind = task.ExecutionKind,
        Status = task.Status,
        Total = task.Total,
        Completed = task.Completed,
        Failed = task.Failed,
        Config = null,
        RuntimePhase = task.RuntimePhase,
        RuntimeMessage = task.RuntimeMessage,
        HeartbeatAtUtc = task.HeartbeatAtUtc,
        RequiresAttention = task.RequiresAttention,
        NextEligibleAtUtc = task.NextEligibleAtUtc,
        CreatedAt = task.CreatedAt,
        StartedAt = task.StartedAt,
        CompletedAt = task.CompletedAt
    };

    public async Task<int> CountActiveTasksAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .AsNoTracking()
            .CountAsync(t => t.Status == "initializing" || t.Status == "updating"
                || t.Status == "pending" || t.Status == "running"
                || t.Status == "pausing" || t.Status == "paused", cancellationToken);
    }

    public async Task<int> TrimHistoryTasksAsync(int keepCount, CancellationToken cancellationToken = default)
    {
        if (keepCount <= 0)
            return 0;

        var staleTasks = await _dbSet
            .Where(t => t.Status == "completed" || t.Status == "failed" || t.Status == "canceled")
            .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(keepCount)
            .ToListAsync(cancellationToken);

        if (staleTasks.Count == 0)
            return 0;

        _dbSet.RemoveRange(staleTasks);
        await SaveChangesWithSqliteLockRetryAsync(cancellationToken);
        return staleTasks.Count;
    }

    private void DetachTrackedEntity(int id)
    {
        foreach (var entry in _context.ChangeTracker.Entries<BatchTask>())
        {
            if (entry.Entity.Id == id)
                entry.State = EntityState.Detached;
        }
    }

    private async Task<bool> ExecuteConditionalUpdateAsync(
        IQueryable<BatchTask> query,
        System.Linq.Expressions.Expression<Func<Microsoft.EntityFrameworkCore.Query.SetPropertyCalls<BatchTask>, Microsoft.EntityFrameworkCore.Query.SetPropertyCalls<BatchTask>>> setters,
        CancellationToken cancellationToken)
    {
        var affected = await ExecuteUpdateWithSqliteLockRetryAsync(
            token => query.ExecuteUpdateAsync(setters, token),
            cancellationToken);
        return affected == 1;
    }

    private static async Task<int> ExecuteUpdateWithSqliteLockRetryAsync(
        Func<CancellationToken, Task<int>> update,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        var delayMs = 200;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await update(cancellationToken);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsSqliteLock(ex))
            {
                await Task.Delay(delayMs, cancellationToken);
                delayMs = Math.Min(delayMs * 2, 2000);
            }
        }
    }

    private static bool IsSqliteLock(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqliteException sqliteException
                && (sqliteException.SqliteErrorCode == 5 || sqliteException.SqliteErrorCode == 6))
                return true;
        }

        return exception.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("database is busy", StringComparison.OrdinalIgnoreCase);
    }
}
