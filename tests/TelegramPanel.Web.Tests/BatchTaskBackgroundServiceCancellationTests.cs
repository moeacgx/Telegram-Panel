using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Modules;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class BatchTaskBackgroundServiceCancellationTests
{
    [Fact]
    public async Task TryStart后暂停再进入执行器不会调用处理器()
    {
        await AssertStoppedBeforeHandlerAsync(cancel: false);
    }

    [Fact]
    public async Task TryStart后取消再进入执行器不会调用处理器()
    {
        await AssertStoppedBeforeHandlerAsync(cancel: true);
    }

    [Fact]
    public async Task 宿主停止后处理器正常返回不会标记任务完成()
    {
        using var harness = CreateHarness(handlerWaitsForRelease: true);
        using var stopping = new CancellationTokenSource();
        var lease = await harness.Control.TryStartExecutionAsync(1, stopping.Token);

        Assert.NotNull(lease);
        var execution = InvokeRunTaskAsync(harness.Service, 1, lease!, stopping.Token);
        await harness.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 处理器本身不抛取消异常，模拟宿主停止时正常返回的模块实现。
        stopping.Cancel();
        harness.Handler.AllowReturn.TrySetResult();

        await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("running", harness.Repository.GetStatus(1));
        Assert.NotEqual("completed", harness.Repository.GetStatus(1));
        Assert.False(harness.Control.HasActiveExecution(1));
    }

    [Fact]
    public async Task 并发设置热更新后会启动额外等待任务()
    {
        using var harness = CreateHarness(
            handlerWaitsForRelease: true,
            initialMaxConcurrent: 1,
            taskCount: 2);

        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => harness.Handler.CallCount == 1, TimeSpan.FromSeconds(6));
            Assert.Equal("running", harness.Repository.GetStatus(1));
            Assert.Equal("pending", harness.Repository.GetStatus(2));

            harness.Configuration["BatchTasks:MaxConcurrent"] = "2";

            await WaitUntilAsync(() => harness.Handler.CallCount == 2, TimeSpan.FromSeconds(4));
            Assert.Equal("running", harness.Repository.GetStatus(2));
        }
        finally
        {
            harness.Handler.AllowReturn.TrySetResult();
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 普通批任务执行器不会领取常驻任务()
    {
        using var harness = CreateHarness(taskExecutionKind: ModuleTaskExecutionKinds.Persistent);

        await harness.Service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.Equal(0, harness.Handler.CallCount);
            Assert.Equal("pending", harness.Repository.GetStatus(1));
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task AssertStoppedBeforeHandlerAsync(bool cancel)
    {
        using var harness = CreateHarness();
        var lease = await harness.Control.TryStartExecutionAsync(1, CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal("running", harness.Repository.GetStatus(1));

        var stop = cancel
            ? harness.Control.CancelTaskAsync(1)
            : harness.Control.PauseTaskAsync(1);
        var expectedStatus = cancel ? "canceled" : "paused";
        await WaitUntilAsync(() => harness.Repository.GetStatus(1) == expectedStatus);

        // 控制服务已经改变状态并请求停止，但旧执行实例尚未进入 RunTaskAsync。
        var execution = InvokeRunTaskAsync(harness.Service, 1, lease!, CancellationToken.None);
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, harness.Handler.CallCount);
        Assert.Equal(expectedStatus, harness.Repository.GetStatus(1));
        Assert.False(harness.Control.HasActiveExecution(1));
    }

    private static TestHarness CreateHarness(
        bool handlerWaitsForRelease = false,
        int initialMaxConcurrent = 1,
        int taskCount = 1,
        string taskExecutionKind = ModuleTaskExecutionKinds.Batch)
    {
        var baseOutputPath = Path.Combine(
            Path.GetTempPath(),
            "telegram-panel-tests",
            Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = baseOutputPath,
                ["BatchTasks:ExecutionStopTimeoutSeconds"] = "2",
                ["BatchTasks:HistoryRetentionLimit"] = "0",
                ["BatchTasks:MaxConcurrent"] = initialMaxConcurrent.ToString(),
                ["BatchTasks:PollIntervalSeconds"] = "1"
            })
            .Build();

        var repository = new TestBatchTaskRepository();
        for (var id = 1; id <= taskCount; id++)
        {
            repository.Seed(new BatchTask
            {
                Id = id,
                TaskType = "test",
                ExecutionKind = taskExecutionKind,
                Status = "pending",
                Total = 1,
                CreatedAt = DateTime.UtcNow.AddMilliseconds(id)
            });
        }

        var handler = new RecordingTaskHandler(handlerWaitsForRelease);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IBatchTaskRepository>(repository);
        services.AddScoped<BatchTaskManagementService>();
        services.AddScoped<IModuleTaskHandler>(_ => handler);
        services.AddSingleton<BatchTaskExecutionControlService>();
        services.AddSingleton<BatchTaskBackgroundService>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        return new TestHarness(
            provider,
            repository,
            handler,
            provider.GetRequiredService<BatchTaskExecutionControlService>(),
            provider.GetRequiredService<BatchTaskBackgroundService>(),
            configuration);
    }

    private static Task InvokeRunTaskAsync(
        BatchTaskBackgroundService service,
        int taskId,
        BatchTaskExecutionLease lease,
        CancellationToken stoppingToken)
    {
        var method = typeof(BatchTaskBackgroundService).GetMethod(
            "RunTaskAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(
            service,
            new object[] { taskId, lease, stoppingToken });
        return Assert.IsAssignableFrom<Task>(result);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(2));
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
            TestBatchTaskRepository repository,
            RecordingTaskHandler handler,
            BatchTaskExecutionControlService control,
            BatchTaskBackgroundService service,
            IConfigurationRoot configuration)
        {
            _provider = provider;
            Repository = repository;
            Handler = handler;
            Control = control;
            Service = service;
            Configuration = configuration;
        }

        public TestBatchTaskRepository Repository { get; }
        public RecordingTaskHandler Handler { get; }
        public BatchTaskExecutionControlService Control { get; }
        public BatchTaskBackgroundService Service { get; }
        public IConfigurationRoot Configuration { get; }

        public void Dispose() => _provider.Dispose();
    }

    private sealed class RecordingTaskHandler : IModuleTaskHandler
    {
        private readonly bool _waitForRelease;
        private int _callCount;

        public RecordingTaskHandler(bool waitForRelease)
        {
            _waitForRelease = waitForRelease;
        }

        public string TaskType => "test";
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReturn { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(
            IModuleTaskExecutionHost host,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            if (_waitForRelease)
                await AllowReturn.Task;
        }
    }

    private sealed class TestBatchTaskRepository : IBatchTaskRepository
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, BatchTask> _tasks = new();

        public void Seed(BatchTask task)
        {
            lock (_sync)
                _tasks[task.Id] = Clone(task);
        }

        public string? GetStatus(int id)
        {
            lock (_sync)
                return _tasks.TryGetValue(id, out var task) ? task.Status : null;
        }

        public Task<BatchTask?> GetFreshByIdAsync(int id) =>
            Task.FromResult(GetSnapshot(id));

        public Task<BatchTask?> GetByIdAsync(int id) =>
            Task.FromResult(GetSnapshot(id));

        public Task<IEnumerable<BatchTask>> GetAllAsync() =>
            Task.FromResult<IEnumerable<BatchTask>>(GetSnapshot());

        public Task<IEnumerable<BatchTask>> FindAsync(Expression<Func<BatchTask, bool>> predicate)
        {
            var filter = predicate.Compile();
            return Task.FromResult<IEnumerable<BatchTask>>(GetSnapshot().Where(filter).ToArray());
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
            lock (_sync)
                _tasks[entity.Id] = Clone(entity);
            return Task.CompletedTask;
        }

        public Task UpdateFreshAsync(BatchTask entity) => UpdateAsync(entity);

        public Task DeleteAsync(BatchTask entity)
        {
            lock (_sync)
                _tasks.Remove(entity.Id);
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(Expression<Func<BatchTask, bool>>? predicate = null)
        {
            var snapshot = GetSnapshot();
            return Task.FromResult(predicate == null ? snapshot.Count : snapshot.Count(predicate.Compile()));
        }

        public Task<bool> TryStartAsync(int id, DateTime startedAt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transition(id, task => task.Status == "pending", task =>
            {
                task.Status = "running";
                task.StartedAt = startedAt;
                task.CompletedAt = null;
            }));
        }

        public Task<bool> TryPauseAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transition(id,
                task => task.Status is "pending" or "running",
                task =>
                {
                    task.Status = "paused";
                    task.CompletedAt = null;
                }));
        }

        public Task<bool> TryResumeAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transition(id, task => task.Status == "paused", task =>
            {
                task.Status = "pending";
                task.StartedAt = null;
                task.CompletedAt = null;
            }));
        }

        public Task<bool> TryCancelAsync(
            int id,
            DateTime completedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transition(id,
                task => task.Status is "pending" or "running" or "paused",
                task =>
                {
                    task.Status = "canceled";
                    task.CompletedAt = completedAt;
                }));
        }

        public Task<bool> TryCompleteAsync(
            int id,
            bool success,
            DateTime completedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transition(id, task => task.Status == "running", task =>
            {
                task.Status = success ? "completed" : "failed";
                task.CompletedAt = completedAt;
            }));
        }

        public Task<bool> TryRequeueAsync(int id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transition(id, task => task.Status == "running", task =>
            {
                task.Status = "pending";
                task.StartedAt = null;
                task.CompletedAt = null;
            }));
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
            return Task.FromResult(Transition(id,
                task => task.Status is "paused" or "completed" or "failed" or "canceled",
                task =>
                {
                    task.Total = total;
                    task.Config = config;
                }));
        }

        public Task<bool> TryUpdateEditableDraftAsync(
            int id,
            int total,
            string? config,
            string? name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Transition(id,
                task => task.Status is "paused" or "completed" or "failed" or "canceled",
                task =>
                {
                    task.Name = name;
                    task.Total = total;
                    task.Config = config;
                }));
        }

        public Task<IEnumerable<BatchTask>> GetByStatusAsync(string status) =>
            Task.FromResult<IEnumerable<BatchTask>>(GetSnapshot().Where(task => task.Status == status).ToArray());

        public Task<IEnumerable<BatchTask>> GetRunningTasksAsync() =>
            GetByStatusAsync("running");

        public Task<IReadOnlyList<BatchTask>> GetActiveTasksAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BatchTask>>(GetSnapshot()
                .Where(task => task.Status is "pending" or "running" or "paused")
                .ToArray());
        }

        public Task<IEnumerable<BatchTask>> GetRecentTasksAsync(int count = 20) =>
            Task.FromResult<IEnumerable<BatchTask>>(GetSnapshot().Take(count).ToArray());

        public Task<IReadOnlyList<BatchTask>> GetTaskCenterItemsAsync(
            int historyCount = 100,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<BatchTask>>(GetSnapshot().Take(historyCount).ToArray());
        }

        public Task<int> CountActiveTasksAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetSnapshot().Count(task =>
                task.Status is "pending" or "running" or "paused"));
        }

        public Task<int> TrimHistoryTasksAsync(int keepCount, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        private bool Transition(
            int id,
            Func<BatchTask, bool> canTransition,
            Action<BatchTask> update)
        {
            lock (_sync)
            {
                if (!_tasks.TryGetValue(id, out var task) || !canTransition(task))
                    return false;
                update(task);
                return true;
            }
        }

        private BatchTask? GetSnapshot(int id)
        {
            lock (_sync)
                return _tasks.TryGetValue(id, out var task) ? Clone(task) : null;
        }

        private List<BatchTask> GetSnapshot()
        {
            lock (_sync)
                return _tasks.Values.Select(Clone).ToList();
        }

        private static BatchTask Clone(BatchTask task) => new()
        {
            Id = task.Id,
            TaskType = task.TaskType,
            OwnerModuleId = task.OwnerModuleId,
            ExecutionKind = task.ExecutionKind,
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
