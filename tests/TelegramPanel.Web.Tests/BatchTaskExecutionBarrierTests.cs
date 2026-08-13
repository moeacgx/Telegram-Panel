using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Data;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class BatchTaskExecutionBarrierTests
{
    [Fact]
    public async Task 暂停会等待旧执行实例退出后再返回()
    {
        using var harness = CreateHarness();
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal("running", harness.Repository.GetStatus(1));

        var pause = harness.Control.PauseTaskAsync(1);
        await WaitUntilAsync(() => harness.Repository.GetStatus(1) == "paused");
        Assert.False(pause.IsCompleted);

        harness.Control.CompleteExecution(lease!);
        await pause.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("paused", harness.Repository.GetStatus(1));
        Assert.False(harness.Control.HasActiveExecution(1));
    }

    [Fact]
    public async Task 旧执行实例超时后任务仍保持暂停()
    {
        using var harness = CreateHarness(stopTimeoutSeconds: 1);
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);

        Assert.NotNull(lease);
        var error = await Assert.ThrowsAsync<BatchTaskExecutionBarrierTimeoutException>(() =>
            harness.Control.PauseTaskAsync(1));

        Assert.Equal(1, error.TaskId);
        Assert.Equal(lease!.Generation, error.Generation);
        Assert.Equal("paused", harness.Repository.GetStatus(1));
        Assert.True(harness.Control.HasActiveExecution(1));

        harness.Control.CompleteExecution(lease);
        Assert.False(harness.Control.HasActiveExecution(1));
    }

    [Fact]
    public async Task pending暂停与启动竞争时暂停优先且不会启动旧任务()
    {
        using var harness = CreateHarness();
        var pauseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Repository.PauseEntered = pauseEntered;
        harness.Repository.AllowPause = allowPause;

        var pause = harness.Control.PauseTaskAsync(1);
        await pauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var start = harness.Control.TryStartExecutionAsync(1, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(start.IsCompleted);

        allowPause.SetResult();
        await pause.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("paused", harness.Repository.GetStatus(1));
    }

    [Fact]
    public async Task 旧执行器重复收尾不会覆盖暂停状态()
    {
        using var harness = CreateHarness();
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);
        Assert.NotNull(lease);

        var pause = harness.Control.PauseTaskAsync(1);
        await WaitUntilAsync(() => harness.Repository.GetStatus(1) == "paused");
        harness.Control.CompleteExecution(lease!);
        await pause.WaitAsync(TimeSpan.FromSeconds(10));

        // 模拟旧执行器在取消回调之后又执行一次收尾。
        lease.MarkCompleted();
        Assert.Equal("paused", harness.Repository.GetStatus(1));
        Assert.Null(await harness.Control.TryStartExecutionAsync(1, CancellationToken.None));
        Assert.Equal("paused", harness.Repository.GetStatus(1));
    }

    [Fact]
    public async Task 宿主停止令牌会取消执行实例令牌()
    {
        using var harness = CreateHarness();
        using var stopping = new CancellationTokenSource();
        var lease = await harness.Control.TryStartExecutionAsync(1, stopping.Token);

        Assert.NotNull(lease);
        stopping.Cancel();
        await WaitUntilAsync(() => lease!.CancellationToken.IsCancellationRequested);

        Assert.True(lease!.CancellationToken.IsCancellationRequested);
        harness.Control.CompleteExecution(lease);
        Assert.False(harness.Control.HasActiveExecution(1));
    }

    [Fact]
    public async Task 执行实例在自身上下文暂停时立即失败且不会自等待死锁()
    {
        using var harness = CreateHarness();
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);
        Assert.NotNull(lease);

        using var context = harness.Control.EnterExecutionContext(lease!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Control.PauseTaskAsync(1).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("不能等待自身退出", error.Message);
        Assert.Equal("paused", harness.Repository.GetStatus(1));
        Assert.True(lease!.CancellationToken.IsCancellationRequested);
        harness.Control.CompleteExecution(lease);
    }

    [Fact]
    public async Task CancelWritesCanceledAndWaitsForExit()
    {
        using var harness = CreateHarness();
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);
        Assert.NotNull(lease);

        var cancel = harness.Control.CancelTaskAsync(1);
        await WaitUntilAsync(() => harness.Repository.GetStatus(1) == "canceled"
            && lease!.CancellationToken.IsCancellationRequested);
        Assert.False(cancel.IsCompleted);

        var canceled = await harness.Repository.GetFreshByIdAsync(1);
        Assert.Equal("canceled", canceled!.Status);
        Assert.NotNull(canceled.CompletedAt);

        harness.Control.CompleteExecution(lease!);
        await cancel.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(harness.Control.HasActiveExecution(1));
        Assert.Equal("canceled", harness.Repository.GetStatus(1));
    }

    [Fact]
    public async Task CancelTimeoutKeepsCanceledState()
    {
        using var harness = CreateHarness(stopTimeoutSeconds: 1);
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);
        Assert.NotNull(lease);

        var error = await Assert.ThrowsAsync<BatchTaskExecutionBarrierTimeoutException>(
            () => harness.Control.CancelTaskAsync(1));

        Assert.Equal(1, error.TaskId);
        Assert.Equal(lease!.Generation, error.Generation);
        Assert.Equal("canceled", harness.Repository.GetStatus(1));
        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.True(harness.Control.HasActiveExecution(1));

        harness.Control.CompleteExecution(lease);
        Assert.False(harness.Control.HasActiveExecution(1));
    }

    [Fact]
    public async Task DeleteRunningTaskWaitsForExitBeforeDeleting()
    {
        using var harness = CreateHarness();
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);
        Assert.NotNull(lease);

        var delete = harness.Control.DeleteTaskAsync(1);
        await WaitUntilAsync(() => harness.Repository.GetStatus(1) == "canceled"
            && lease!.CancellationToken.IsCancellationRequested);
        Assert.False(delete.IsCompleted);
        Assert.NotNull(await harness.Repository.GetFreshByIdAsync(1));

        harness.Control.CompleteExecution(lease!);
        await delete.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await harness.Repository.GetFreshByIdAsync(1));
        Assert.False(harness.Control.HasActiveExecution(1));
    }

    [Fact]
    public async Task TerminalTaskCannotBeCanceled()
    {
        using var harness = CreateHarness(initialStatus: "completed");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Control.CancelTaskAsync(1));
        Assert.Equal("completed", harness.Repository.GetStatus(1));
    }

    private static TestHarness CreateHarness(string initialStatus = "pending", int stopTimeoutSeconds = 10)
    {
        var repository = new InMemoryBatchTaskRepository();
        repository.Seed(new BatchTask
        {
            Id = 1,
            TaskType = "test",
            Status = initialStatus,
            Total = 1,
            CreatedAt = DateTime.UtcNow
        });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BatchTasks:ExecutionStopTimeoutSeconds"] = stopTimeoutSeconds.ToString()
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IBatchTaskRepository>(repository);
        services.AddScoped<BatchTaskManagementService>();
        var provider = services.BuildServiceProvider();
        var control = new BatchTaskExecutionControlService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration);
        return new TestHarness(provider, repository, control);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待测试状态变更超时");
            await Task.Delay(10);
        }
    }

    private sealed class TestHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public TestHarness(
            ServiceProvider provider,
            InMemoryBatchTaskRepository repository,
            BatchTaskExecutionControlService control)
        {
            _provider = provider;
            Repository = repository;
            Control = control;
        }

        public InMemoryBatchTaskRepository Repository { get; }
        public BatchTaskExecutionControlService Control { get; }

        public void Dispose() => _provider.Dispose();
    }

    private sealed class InMemoryBatchTaskRepository : IBatchTaskRepository
    {
        private readonly object _sync = new();
        private readonly ConcurrentDictionary<int, BatchTask> _tasks = new();

        public TaskCompletionSource? PauseEntered { get; set; }
        public TaskCompletionSource? AllowPause { get; set; }

        public void Seed(BatchTask task) => _tasks[task.Id] = Clone(task);

        public string? GetStatus(int id)
        {
            return _tasks.TryGetValue(id, out var task) ? task.Status : null;
        }

        public Task<BatchTask?> GetFreshByIdAsync(int id) => Task.FromResult(GetById(id));
        public Task<BatchTask?> GetByIdAsync(int id) => Task.FromResult(GetById(id));

        public Task<IEnumerable<BatchTask>> GetAllAsync() =>
            Task.FromResult<IEnumerable<BatchTask>>(Snapshot());

        public Task<IEnumerable<BatchTask>> FindAsync(Expression<Func<BatchTask, bool>> predicate)
        {
            var filter = predicate.Compile();
            return Task.FromResult<IEnumerable<BatchTask>>(Snapshot().Where(filter).ToArray());
        }

        public Task<BatchTask> AddAsync(BatchTask entity)
        {
            lock (_sync)
            {
                if (entity.Id == 0)
                    entity.Id = _tasks.Keys.DefaultIfEmpty(0).Max() + 1;
                _tasks[entity.Id] = Clone(entity);
                return Task.FromResult(Clone(entity));
            }
        }

        public Task UpdateAsync(BatchTask entity)
        {
            _tasks[entity.Id] = Clone(entity);
            return Task.CompletedTask;
        }

        public Task UpdateFreshAsync(BatchTask entity) => UpdateAsync(entity);

        public Task DeleteAsync(BatchTask entity)
        {
            _tasks.TryRemove(entity.Id, out _);
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(Expression<Func<BatchTask, bool>>? predicate = null)
        {
            var snapshot = Snapshot();
            return Task.FromResult(predicate == null ? snapshot.Count : snapshot.Count(predicate.Compile()));
        }

        public Task<bool> TryStartAsync(int id, DateTime startedAt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) || task.Status != "pending")
                    return Task.FromResult(false);
                task.Status = "running";
                task.StartedAt = startedAt;
                task.CompletedAt = null;
                return Task.FromResult(true);
            }
        }

        public async Task<bool> TryPauseAsync(int id, CancellationToken cancellationToken = default)
        {
            PauseEntered?.TrySetResult();
            if (AllowPause is { } allow)
                await allow.Task.WaitAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) ||
                    (task.Status != "pending" && task.Status != "running"))
                    return false;
                task.Status = "paused";
                task.CompletedAt = null;
                return true;
            }
        }

        public Task<bool> TryResumeAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) || task.Status != "paused")
                    return Task.FromResult(false);
                task.Status = "pending";
                task.StartedAt = null;
                task.CompletedAt = null;
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryCancelAsync(
            int id,
            DateTime completedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) ||
                    task.Status is not ("pending" or "running" or "paused"))
                    return Task.FromResult(false);
                task.Status = "canceled";
                task.CompletedAt = completedAt;
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryCompleteAsync(
            int id,
            bool success,
            DateTime completedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) || task.Status != "running")
                    return Task.FromResult(false);
                task.Status = success ? "completed" : "failed";
                task.CompletedAt = completedAt;
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryRequeueAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) || task.Status != "running")
                    return Task.FromResult(false);
                task.Status = "pending";
                task.StartedAt = null;
                task.CompletedAt = null;
                return Task.FromResult(true);
            }
        }

        public Task UpdateProgressColumnsAsync(
            int id,
            int completed,
            int failed,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_tasks.TryGetValue(id, out var task))
                {
                    task.Completed = completed;
                    task.Failed = failed;
                }
            }
            return Task.CompletedTask;
        }

        public Task UpdateConfigColumnAsync(
            int id,
            string? config,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_tasks.TryGetValue(id, out var task))
                    task.Config = config;
            }
            return Task.CompletedTask;
        }

        public Task UpdateDraftColumnsAsync(
            int id,
            int total,
            string? config,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_tasks.TryGetValue(id, out var task))
                {
                    task.Total = total;
                    task.Config = config;
                }
            }
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateEditableDraftAsync(
            int id,
            int total,
            string? config,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) ||
                    task.Status is not ("paused" or "completed" or "failed" or "canceled"))
                    return Task.FromResult(false);
                task.Total = total;
                task.Config = config;
                return Task.FromResult(true);
            }
        }

        public Task<IEnumerable<BatchTask>> GetByStatusAsync(string status) =>
            Task.FromResult<IEnumerable<BatchTask>>(Snapshot().Where(x => x.Status == status).ToArray());

        public Task<IEnumerable<BatchTask>> GetRunningTasksAsync() => GetByStatusAsync("running");

        public Task<IReadOnlyList<BatchTask>> GetActiveTasksAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BatchTask>>(Snapshot()
                .Where(x => x.Status is "pending" or "running" or "paused")
                .ToArray());
        }

        public Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20) =>
            Task.FromResult<IEnumerable<BatchTask>>(Snapshot().Take(count).ToArray());

        public Task<IReadOnlyList<BatchTask>> GetTaskCenterItemsAsync(
            int historyCount = 100,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BatchTask>>(Snapshot().Take(historyCount).ToArray());
        }

        public Task<int> CountActiveTasksAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot().Count(x => x.Status is "pending" or "running" or "paused"));
        }

        public Task<int> TrimHistoryTasksAsync(int keepCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        private BatchTask? GetById(int id)
        {
            lock (_sync)
                return _tasks.TryGetValue(id, out var task) ? Clone(task) : null;
        }

        private List<BatchTask> Snapshot() =>
            _tasks.Values.Select(Clone).ToList();

        private static BatchTask Clone(BatchTask task) => new()
        {
            Id = task.Id,
            TaskType = task.TaskType,
            Status = task.Status,
            Total = task.Total,
            Completed = task.Completed,
            Failed = task.Failed,
            Config = task.Config,
            CreatedAt = task.CreatedAt,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt
        };
    }
}
