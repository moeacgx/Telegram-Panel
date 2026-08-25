using System.Collections.Concurrent;
using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 外部模块常驻任务执行器。与普通批任务使用独立并发池，但共享执行租约和暂停屏障。
/// </summary>
public sealed class PersistentModuleTaskBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly BatchTaskExecutionControlService _executionControl;
    private readonly BatchTaskStartupRecoveryService _startupRecovery;
    private readonly ModuleContributionRegistry _contributions;
    private readonly ILogger<PersistentModuleTaskBackgroundService> _logger;
    private readonly ConcurrentDictionary<int, Task> _runningTasks = new();

    public PersistentModuleTaskBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        BatchTaskExecutionControlService executionControl,
        BatchTaskStartupRecoveryService startupRecovery,
        ModuleContributionRegistry contributions,
        ILogger<PersistentModuleTaskBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _executionControl = executionControl;
        _startupRecovery = startupRecovery;
        _contributions = contributions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("PersistentModuleTasks:Enabled", true))
        {
            _logger.LogInformation("Persistent module task runner disabled");
            return;
        }

        var pollSeconds = Math.Clamp(
            _configuration.GetValue("PersistentModuleTasks:PollIntervalSeconds", 2),
            1,
            30);
        var interval = TimeSpan.FromSeconds(pollSeconds);

        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        await _startupRecovery.EnsureRecoveredAsync(stoppingToken);

        _logger.LogInformation(
            "Persistent module task runner started, interval={IntervalSeconds}s, maxConcurrent={MaxConcurrent}",
            pollSeconds,
            ReadMaxConcurrent());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanupCompletedTasks();
                var maxConcurrent = ReadMaxConcurrent();
                while (_runningTasks.Count < maxConcurrent
                       && await TryStartOneAsync(stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persistent module task runner loop failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private int ReadMaxConcurrent() => Math.Clamp(
        _configuration.GetValue("PersistentModuleTasks:MaxConcurrent", 4),
        1,
        32);

    private void CleanupCompletedTasks()
    {
        foreach (var pair in _runningTasks)
        {
            if (pair.Value.IsCompleted)
                _runningTasks.TryRemove(pair.Key, out _);
        }
    }

    private async Task<bool> TryStartOneAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskManagement = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var handlers = scope.ServiceProvider.GetServices<IModulePersistentTaskHandler>()
            .Where(handler => !string.IsNullOrWhiteSpace(handler.TaskType))
            .GroupBy(handler => handler.TaskType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        var pending = (await taskManagement.GetTasksByStatusAsync("pending"))
            .Where(IsOwnedPersistentTask)
            .Where(task => handlers.ContainsKey(task.TaskType))
            .Where(task => !_runningTasks.ContainsKey(task.Id))
            .OrderBy(task => task.CreatedAt)
            .FirstOrDefault();
        if (pending == null)
            return false;

        var execution = await _executionControl.TryStartExecutionAsync(pending.Id, cancellationToken);
        if (execution == null)
            return false;

        try
        {
            _runningTasks[pending.Id] = RunTaskAsync(pending.Id, execution, cancellationToken);
        }
        catch
        {
            _executionControl.CompleteExecution(execution);
            throw;
        }

        return true;
    }

    private bool IsOwnedPersistentTask(BatchTask task)
    {
        if (!string.Equals(task.ExecutionKind, ModuleTaskExecutionKinds.Persistent, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!_contributions.TaskTypeToDefinition.TryGetValue(task.TaskType, out var registered))
            return false;

        return string.Equals(
                   registered.Definition.ExecutionKind,
                   ModuleTaskExecutionKinds.Persistent,
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(registered.Module.Id, task.OwnerModuleId, StringComparison.Ordinal);
    }

    private async Task RunTaskAsync(
        int taskId,
        BatchTaskExecutionLease execution,
        CancellationToken stoppingToken)
    {
        var executionToken = execution.CancellationToken;
        using var executionContext = _executionControl.EnterExecutionContext(execution);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            var task = await tasks.GetTaskAsync(taskId);
            if (task == null || task.Status != "running" || !IsOwnedPersistentTask(task))
                return;

            var handlers = scope.ServiceProvider.GetServices<IModulePersistentTaskHandler>()
                .Where(handler => string.Equals(handler.TaskType, task.TaskType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (handlers.Count != 1)
            {
                await tasks.UpdateTaskRuntimeStateAsync(
                    taskId,
                    "paused",
                    "常驻任务执行器不可用，请检查模块安装和处理器注册",
                    DateTime.UtcNow,
                    true,
                    CancellationToken.None);
                await tasks.TryBeginPauseTaskAsync(taskId, executionToken);
                _logger.LogError(
                    "Persistent task requires exactly one handler: taskId={TaskId}, taskType={TaskType}, count={Count}",
                    taskId,
                    task.TaskType,
                    handlers.Count);
                return;
            }

            var host = new PersistentExecutionHost(
                task,
                tasks,
                scope.ServiceProvider,
                _executionControl,
                execution,
                _logger);
            try
            {
                await handlers[0].ExecuteAsync(host, executionToken);
                executionToken.ThrowIfCancellationRequested();

                var latest = await tasks.GetTaskAsync(taskId);
                if (latest?.Status == "running")
                {
                    await tasks.UpdateTaskRuntimeStateAsync(
                        taskId,
                        "restarting",
                        "常驻处理器意外返回，等待重新启动",
                        DateTime.UtcNow,
                        false,
                        executionToken);
                    await tasks.RequeueRunningTasksAsync(item => item.Id == taskId, executionToken);
                    _logger.LogWarning(
                        "Persistent task returned without pause; requeued: taskId={TaskId}, taskType={TaskType}",
                        taskId,
                        task.TaskType);
                }
            }
            catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
            {
                if (stoppingToken.IsCancellationRequested)
                    _logger.LogInformation("Persistent task interrupted by shutdown: {TaskId}", taskId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Persistent task failed and will be paused: {TaskId} {TaskType}", taskId, task.TaskType);
                await tasks.UpdateTaskRuntimeStateAsync(
                    taskId,
                    "paused",
                    "常驻任务执行异常，请查看服务日志",
                    DateTime.UtcNow,
                    true,
                    CancellationToken.None);
                await tasks.TryBeginPauseTaskAsync(taskId, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            // 解析处理器或创建作用域期间收到停止请求。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persistent task crashed before handler execution and will be paused: {TaskId}", taskId);
            using var recoveryScope = _scopeFactory.CreateScope();
            var recoveryTasks = recoveryScope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            await recoveryTasks.UpdateTaskRuntimeStateAsync(
                taskId,
                "paused",
                "常驻任务启动失败，请查看服务日志",
                DateTime.UtcNow,
                true,
                CancellationToken.None);
            await recoveryTasks.TryBeginPauseTaskAsync(taskId, CancellationToken.None);
        }
        finally
        {
            _runningTasks.TryRemove(taskId, out _);
            await _executionControl.CompleteExecutionAsync(execution);
        }
    }

    private sealed class PersistentExecutionHost : IModulePersistentTaskExecutionHost
    {
        private readonly BatchTask _task;
        private readonly BatchTaskManagementService _tasks;
        private readonly BatchTaskExecutionControlService _executionControl;
        private readonly BatchTaskExecutionLease _execution;
        private readonly ILogger _logger;

        public PersistentExecutionHost(
            BatchTask task,
            BatchTaskManagementService tasks,
            IServiceProvider services,
            BatchTaskExecutionControlService executionControl,
            BatchTaskExecutionLease execution,
            ILogger logger)
        {
            _task = task;
            _tasks = tasks;
            Services = services;
            _executionControl = executionControl;
            _execution = execution;
            _logger = logger;
        }

        public int TaskId => _task.Id;
        public string TaskType => _task.TaskType;
        public int Total => _task.Total;
        public string? Config => _task.Config;
        public IServiceProvider Services { get; }

        public async Task<bool> IsStillRunningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = await _tasks.GetTaskAsync(_task.Id);
            return latest?.Status == "running";
        }

        public async Task UpdateProgressAsync(int completed, int failed, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _tasks.UpdateTaskProgressAsync(_task.Id, completed, failed);
            await _tasks.UpdateTaskRuntimeStateAsync(
                _task.Id,
                "running",
                null,
                DateTime.UtcNow,
                false,
                cancellationToken);
        }

        public async Task RequestPauseAsync(
            string reason,
            bool requiresAttention,
            CancellationToken cancellationToken = default)
        {
            _logger.LogWarning(
                "Persistent task requested pause: taskId={TaskId}, attention={RequiresAttention}, reason={Reason}",
                _task.Id,
                requiresAttention,
                reason);
            await _tasks.UpdateTaskRuntimeStateAsync(
                _task.Id,
                "paused",
                reason,
                DateTime.UtcNow,
                requiresAttention,
                cancellationToken);
            await _executionControl.RequestPauseFromExecutionAsync(_execution, cancellationToken);
        }
    }
}
