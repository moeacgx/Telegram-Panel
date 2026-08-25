using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 批量任务仓储接口
/// </summary>
public interface IBatchTaskRepository : IRepository<BatchTask>
{
    Task<BatchTask?> GetFreshByIdAsync(int id);
    Task UpdateFreshAsync(BatchTask entity);
    Task<bool> TryStartAsync(int id, DateTime startedAt, CancellationToken cancellationToken = default);
    Task<bool> TryPauseAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> TryBeginPauseAsync(int id, CancellationToken cancellationToken = default) =>
        TryPauseAsync(id, cancellationToken);
    Task<bool> TryConfirmPausedAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
    Task<bool> TryResumeAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> TryCancelAsync(int id, DateTime completedAt, CancellationToken cancellationToken = default);
    Task<bool> TryCompleteAsync(int id, bool success, DateTime completedAt, CancellationToken cancellationToken = default);
    Task<bool> TryRequeueAsync(int id, CancellationToken cancellationToken = default);
    Task UpdateProgressColumnsAsync(int id, int completed, int failed, CancellationToken cancellationToken = default);
    Task UpdateRuntimeStateColumnsAsync(
        int id,
        string? phase,
        string? message,
        DateTime? heartbeatAtUtc,
        bool requiresAttention,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task UpdateConfigColumnAsync(int id, string? config, CancellationToken cancellationToken = default);
    Task UpdateDraftColumnsAsync(int id, int total, string? config, CancellationToken cancellationToken = default);
    Task<bool> TryUpdateEditableDraftAsync(int id, int total, string? config, CancellationToken cancellationToken = default);
    Task<bool> TryUpdateEditableDraftAsync(int id, int total, string? config, string? name, CancellationToken cancellationToken = default);
    Task<IEnumerable<BatchTask>> GetByStatusAsync(string status);
    Task<IEnumerable<BatchTask>> GetRunningTasksAsync();
    Task<IReadOnlyList<BatchTask>> GetActiveTasksAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20);
    Task<IReadOnlyList<BatchTask>> GetTaskCenterItemsAsync(int historyCount = 100, CancellationToken cancellationToken = default);
    Task<int> CountActiveTasksAsync(CancellationToken cancellationToken = default);
    Task<int> TrimHistoryTasksAsync(int keepCount, CancellationToken cancellationToken = default);
}
