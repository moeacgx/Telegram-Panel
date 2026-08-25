using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Core.Services;

namespace TelegramPanel.Web.Services;

public sealed class BatchTaskExecutionControlService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModuleTaskLifecycleService? _lifecycle;
    private readonly TimeSpan _stopTimeout;
    private readonly ConcurrentDictionary<int, Gate> _gates = new();
    private readonly ConcurrentDictionary<int, BatchTaskExecutionLease> _executions = new();
    private readonly AsyncLocal<BatchTaskExecutionLease?> _current = new();
    private long _generation;

    public BatchTaskExecutionControlService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ModuleTaskLifecycleService? lifecycle = null)
    {
        _scopeFactory = scopeFactory;
        _lifecycle = lifecycle;
        var seconds = configuration.GetValue("BatchTasks:ExecutionStopTimeoutSeconds", 15);
        _stopTimeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
    }

    internal async Task<BatchTaskExecutionLease?> TryStartExecutionAsync(int taskId, CancellationToken stoppingToken)
    {
        var gate = AcquireGate(taskId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(stoppingToken);
            entered = true;
            if (TryGetActive(taskId, out _))
                return null;

            var lease = new BatchTaskExecutionLease(taskId, Interlocked.Increment(ref _generation), stoppingToken);
            if (!_executions.TryAdd(taskId, lease))
            {
                lease.DisposeWithoutExecution();
                return null;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
                if (await tasks.TryStartTaskAsync(taskId, stoppingToken))
                    return lease;

                RemoveExecution(lease);
                lease.DisposeWithoutExecution();
                return null;
            }
            catch
            {
                RemoveExecution(lease);
                lease.DisposeWithoutExecution();
                throw;
            }
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseGate(taskId, gate);
        }
    }

    public async Task PauseTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var gate = AcquireGate(taskId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            var current = await tasks.GetTaskAsync(taskId)
                ?? throw new InvalidOperationException("任务不存在或已被删除");

            if (current.Status is "pending" or "running")
            {
                if (!await tasks.TryBeginPauseTaskAsync(taskId, cancellationToken))
                {
                    current = await tasks.GetTaskAsync(taskId)
                        ?? throw new InvalidOperationException("任务不存在或已被删除");
                    if (current.Status is not ("pausing" or "paused"))
                        throw new InvalidOperationException($"任务状态已变更为 {current.Status}，无法暂停");
                }
            }
            else if (current.Status is not ("pausing" or "paused"))
            {
                throw new InvalidOperationException($"任务当前状态为 {current.Status}，无法暂停");
            }

            if (TryGetActive(taskId, out var execution))
            {
                execution.RequestStop();
                ThrowIfSelf(execution);
                await WaitForExitAsync(execution, cancellationToken);
            }

            current = await tasks.GetTaskAsync(taskId)
                ?? throw new InvalidOperationException("任务不存在或已被删除");
            if (current.Status == "pausing"
                && !await tasks.TryConfirmPausedTaskAsync(taskId, cancellationToken))
            {
                current = await tasks.GetTaskAsync(taskId)
                    ?? throw new InvalidOperationException("任务不存在或已被删除");
                if (current.Status != "paused")
                    throw new InvalidOperationException($"任务状态已变更为 {current.Status}，无法确认暂停");
            }
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseGate(taskId, gate);
        }
    }

    public async Task ResumeTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var gate = AcquireGate(taskId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            var current = await tasks.GetTaskAsync(taskId)
                ?? throw new InvalidOperationException("任务不存在或已被删除");
            if (current.Status != "paused")
                throw new InvalidOperationException($"任务当前状态为 {current.Status}，无法恢复");

            if (TryGetActive(taskId, out var execution))
            {
                execution.RequestStop();
                ThrowIfSelf(execution);
                await WaitForExitAsync(execution, cancellationToken);
            }

            if (!await tasks.TryResumeTaskAsync(taskId, cancellationToken))
                throw new InvalidOperationException("任务状态已发生变化，未能恢复为待执行");
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseGate(taskId, gate);
        }
    }


    public async Task CancelTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var gate = AcquireGate(taskId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            var current = await tasks.GetTaskAsync(taskId)
                ?? throw new InvalidOperationException("任务不存在或已被删除");

            if (current.Status is "pending" or "running" or "pausing" or "paused")
            {
                if (!await tasks.TryCancelTaskAsync(taskId, cancellationToken))
                {
                    current = await tasks.GetTaskAsync(taskId)
                        ?? throw new InvalidOperationException("任务不存在或已被删除");
                    if (current.Status != "canceled")
                        throw new InvalidOperationException($"任务状态已变更为 {current.Status}，无法取消");
                }
            }
            else if (current.Status != "canceled")
            {
                throw new InvalidOperationException($"任务当前状态为 {current.Status}，无法取消");
            }

            if (TryGetActive(taskId, out var execution))
            {
                execution.RequestStop();
                ThrowIfSelf(execution);
                await WaitForExitAsync(execution, cancellationToken);
            }
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseGate(taskId, gate);
        }
    }

    public async Task DeleteTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var gate = AcquireGate(taskId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            var current = await tasks.GetTaskAsync(taskId)
                ?? throw new InvalidOperationException("任务不存在或已被删除");

            if (current.Status is "pending" or "running" or "pausing" or "paused")
            {
                if (!await tasks.TryCancelTaskAsync(taskId, cancellationToken))
                {
                    current = await tasks.GetTaskAsync(taskId)
                        ?? throw new InvalidOperationException("任务不存在或已被删除");
                    if (current.Status is not ("completed" or "failed" or "canceled"))
                        throw new InvalidOperationException($"任务状态已变更为 {current.Status}，无法删除");
                }
            }
            else if (current.Status is not ("completed" or "failed" or "canceled"))
            {
                throw new InvalidOperationException($"任务当前状态为 {current.Status}，无法删除");
            }

            if (TryGetActive(taskId, out var execution))
            {
                execution.RequestStop();
                ThrowIfSelf(execution);
                await WaitForExitAsync(execution, cancellationToken);
            }

            if (_lifecycle != null)
            {
                await _lifecycle.DeleteAsync(
                    current,
                    Guid.NewGuid().ToString("N"),
                    cancellationToken);
            }
            else
            {
                await tasks.DeleteTaskAsync(taskId);
            }
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseGate(taskId, gate);
        }
    }

    internal void CompleteExecution(BatchTaskExecutionLease execution)
    {
        RemoveExecution(execution);
        execution.MarkCompleted();
    }

    internal async Task CompleteExecutionAsync(
        BatchTaskExecutionLease execution,
        CancellationToken cancellationToken = default)
    {
        CompleteExecution(execution);
        using var scope = _scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var current = await tasks.GetTaskAsync(execution.TaskId);
        if (current?.Status == "pausing")
            await tasks.TryConfirmPausedTaskAsync(execution.TaskId, cancellationToken);
    }

    internal async Task RequestPauseFromExecutionAsync(
        BatchTaskExecutionLease execution,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(_current.Value, execution))
            throw new InvalidOperationException("只有当前任务执行实例可以请求自暂停");

        var gate = AcquireGate(execution.TaskId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            var current = await tasks.GetTaskAsync(execution.TaskId)
                ?? throw new InvalidOperationException("任务不存在或已被删除");

            if (current.Status is "pending" or "running")
            {
                if (!await tasks.TryBeginPauseTaskAsync(execution.TaskId, cancellationToken))
                    throw new InvalidOperationException("任务状态已变化，无法请求暂停");
            }
            else if (current.Status is not ("pausing" or "paused"))
            {
                throw new InvalidOperationException($"任务当前状态为 {current.Status}，无法请求暂停");
            }

            execution.RequestStop();
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReleaseGate(execution.TaskId, gate);
        }
    }

    internal IDisposable EnterExecutionContext(BatchTaskExecutionLease execution)
    {
        var previous = _current.Value;
        _current.Value = execution;
        return new ContextScope(_current, previous);
    }

    internal bool HasActiveExecution(int taskId) => TryGetActive(taskId, out _);
    internal int RetainedTaskGateCount => _gates.Count;

    public async Task<int> TrimHistoryTasksAsync(
        int keepCount,
        CancellationToken cancellationToken = default)
    {
        if (keepCount <= 0)
            return 0;

        using var scope = _scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var stale = (await tasks.GetAllTasksAsync())
            .Where(task => task.Status is "completed" or "failed" or "canceled")
            .OrderByDescending(task => task.CompletedAt ?? task.CreatedAt)
            .ThenByDescending(task => task.Id)
            .Skip(Math.Min(keepCount, 5000))
            .Select(task => task.Id)
            .ToList();

        var deleted = 0;
        foreach (var taskId in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteTaskAsync(taskId, cancellationToken);
            deleted++;
        }

        return deleted;
    }

    private async Task WaitForExitAsync(BatchTaskExecutionLease execution, CancellationToken cancellationToken)
    {
        try
        {
            await execution.Completion.WaitAsync(_stopTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new BatchTaskExecutionBarrierTimeoutException(
                execution.TaskId, execution.Generation, _stopTimeout, ex);
        }
    }

    private void ThrowIfSelf(BatchTaskExecutionLease execution)
    {
        if (ReferenceEquals(_current.Value, execution))
            throw new InvalidOperationException($"任务 #{execution.TaskId} 的执行实例不能等待自身退出；任务已保持暂停，当前实例将在返回后结束。");
    }

    private bool TryGetActive(int taskId, out BatchTaskExecutionLease execution)
    {
        if (_executions.TryGetValue(taskId, out var current))
        {
            if (!current.Completion.IsCompleted)
            {
                execution = current;
                return true;
            }
            RemoveExecution(current);
        }

        execution = null!;
        return false;
    }

    private void RemoveExecution(BatchTaskExecutionLease execution) =>
        ((ICollection<KeyValuePair<int, BatchTaskExecutionLease>>)_executions)
            .Remove(new KeyValuePair<int, BatchTaskExecutionLease>(execution.TaskId, execution));

    private Gate AcquireGate(int taskId)
    {
        while (true)
        {
            var gate = _gates.GetOrAdd(taskId, static _ => new Gate());
            if (gate.AddReference()) return gate;
            RemoveGate(taskId, gate);
        }
    }

    private void ReleaseGate(int taskId, Gate gate)
    {
        if (gate.ReleaseReference()) RemoveGate(taskId, gate);
    }

    private void RemoveGate(int taskId, Gate gate)
    {
        if (((ICollection<KeyValuePair<int, Gate>>)_gates)
            .Remove(new KeyValuePair<int, Gate>(taskId, gate)))
            gate.Dispose();
    }

    private sealed class ContextScope : IDisposable
    {
        private readonly AsyncLocal<BatchTaskExecutionLease?> _slot;
        private readonly BatchTaskExecutionLease? _previous;
        private int _disposed;
        public ContextScope(AsyncLocal<BatchTaskExecutionLease?> slot, BatchTaskExecutionLease? previous)
        {
            _slot = slot;
            _previous = previous;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _slot.Value = _previous;
        }
    }

    private sealed class Gate : IDisposable
    {
        private readonly object _sync = new();
        private int _references;
        private bool _retired;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public bool AddReference()
        {
            lock (_sync)
            {
                if (_retired) return false;
                _references++;
                return true;
            }
        }
        public bool ReleaseReference()
        {
            lock (_sync)
            {
                _references--;
                if (_references != 0) return false;
                _retired = true;
                return true;
            }
        }
        public void Dispose() => Semaphore.Dispose();
    }
}

internal sealed class BatchTaskExecutionLease
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;

    public BatchTaskExecutionLease(int taskId, long generation, CancellationToken stoppingToken)
    {
        TaskId = taskId;
        Generation = generation;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    }

    public int TaskId { get; }
    public long Generation { get; }
    public CancellationToken CancellationToken => _cancellation.Token;
    public Task Completion => _completion.Task;

    public void RequestStop()
    {
        if (Volatile.Read(ref _completed) != 0) return;
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _completed) != 0) { }
    }

    public void MarkCompleted()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        _completion.TrySetResult();
        _cancellation.Dispose();
    }

    public void DisposeWithoutExecution() => MarkCompleted();
}

public sealed class BatchTaskExecutionBarrierTimeoutException : TimeoutException
{
    public BatchTaskExecutionBarrierTimeoutException(int taskId, long generation, TimeSpan timeout, Exception innerException)
        : base($"任务 #{taskId} 的旧执行实例（代次 {generation}）在 {timeout.TotalSeconds:0} 秒内未退出；任务状态已保留，请稍后重试。", innerException)
    {
        TaskId = taskId;
        Generation = generation;
    }

    public int TaskId { get; }
    public long Generation { get; }
}
