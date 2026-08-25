using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;
using System.Runtime.ExceptionServices;

namespace TelegramPanel.Web.Services;

public sealed class ModuleTaskLifecycleService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModuleContributionRegistry _contributions;
    private readonly ILogger<ModuleTaskLifecycleService> _logger;

    public ModuleTaskLifecycleService(
        IServiceScopeFactory scopeFactory,
        ModuleContributionRegistry contributions,
        ILogger<ModuleTaskLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _contributions = contributions;
        _logger = logger;
    }

    public async Task ValidateAsync(
        BatchTask task,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var handler = ResolveHandler(scope.ServiceProvider, task, required: false);
        if (handler == null)
            return;

        await handler.ValidateAsync(CreateContext(task, operationId, scope.ServiceProvider), cancellationToken);
    }

    public async Task CommitUpsertAsync(
        BatchTask task,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var handler = ResolveHandler(scope.ServiceProvider, task, required: false);
        if (handler == null)
            return;
        await handler.CommitUpsertAsync(CreateContext(task, operationId, scope.ServiceProvider), cancellationToken);
    }

    public async Task<BatchTask> CommitCreatedTaskAsync(
        BatchTask task,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await CommitUpsertAsync(task, operationId, cancellationToken);
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            if (!await tasks.TryActivateInitializedTaskAsync(task.Id, cancellationToken))
                throw new InvalidOperationException("任务初始化状态已变化，无法激活执行");
            return await tasks.GetTaskAsync(task.Id)
                ?? throw new InvalidOperationException("任务初始化后已不存在");
        }
        catch (Exception ex)
        {
            await FailTransitionBestEffortAsync(
                task.Id,
                "initializing",
                "模块任务初始化失败，请检查服务日志后重跑任务",
                ex);
            throw;
        }
    }

    public async Task<BatchTask> CommitEditedTaskAsync(
        BatchTask updated,
        BatchTask previous,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await CommitUpsertAsync(updated, operationId, cancellationToken);
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            if (!await tasks.TryFinishEditableTaskUpdateAsync(updated.Id, cancellationToken))
                throw new InvalidOperationException("任务编辑状态已变化，无法提交配置");
            return await tasks.GetTaskAsync(updated.Id)
                ?? throw new InvalidOperationException("任务编辑后已不存在");
        }
        catch (Exception commitError)
        {
            try
            {
                await CommitUpsertAsync(previous, "update:rollback:" + operationId, CancellationToken.None);
                using var scope = _scopeFactory.CreateScope();
                var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
                if (!await tasks.TryRollbackEditableTaskUpdateAsync(
                        updated.Id,
                        previous.Total,
                        previous.Config,
                        previous.Name,
                        "模块配置提交失败，已恢复编辑前配置",
                        DateTime.UtcNow,
                        CancellationToken.None))
                    throw new InvalidOperationException("宿主任务配置无法恢复到编辑前状态");
            }
            catch (Exception rollbackError)
            {
                await FailTransitionBestEffortAsync(
                    updated.Id,
                    "updating",
                    "模块配置提交和回滚均失败，任务已停止，请检查服务日志后重跑",
                    rollbackError);
                throw new AggregateException("模块任务编辑失败且无法安全回滚", commitError, rollbackError);
            }

            ExceptionDispatchInfo.Capture(commitError).Throw();
            throw;
        }
    }

    public async Task DeleteAsync(
        BatchTask task,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var handler = ResolveHandler(scope.ServiceProvider, task, required: false);
        if (handler == null)
        {
            await tasks.DeleteTaskAsync(task.Id);
            return;
        }

        var context = CreateContext(task, operationId, scope.ServiceProvider);
        await handler.PrepareDeleteAsync(context, cancellationToken);
        try
        {
            await tasks.DeleteTaskAsync(task.Id);
        }
        catch
        {
            await handler.AbortDeleteAsync(context, cancellationToken);
            throw;
        }

        // Commit 失败时由下次启动的 ReconcileAsync 根据宿主现存 ID 修复。
        await handler.CommitDeleteAsync(context, cancellationToken);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
        var existingTasks = (await tasks.GetAllTasksAsync()).ToList();
        var handlers = scope.ServiceProvider.GetServices<IModuleTaskLifecycleHandler>().ToList();
        var reconciledTransientIds = new HashSet<int>();

        foreach (var group in handlers.GroupBy(handler => handler.TaskType, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() != 1)
            {
                _logger.LogError(
                    "Skip lifecycle reconciliation because handler is not unique: taskType={TaskType}, count={Count}",
                    group.Key,
                    group.Count());
                continue;
            }

            if (!_contributions.TaskTypeToDefinition.TryGetValue(group.Key, out var registered))
                continue;

            var ownedTasks = existingTasks
                .Where(task => string.Equals(task.TaskType, group.Key, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(task.OwnerModuleId, registered.Module.Id, StringComparison.Ordinal))
                .ToArray();
            await group.Single().ReconcileAsync(ownedTasks.Select(task => task.Id).ToArray(), cancellationToken);

            foreach (var task in ownedTasks.Where(task => task.Status == "initializing"))
            {
                try
                {
                    await group.Single().CommitUpsertAsync(
                        CreateContext(task, $"recovery:create:{task.Id}", scope.ServiceProvider),
                        cancellationToken);
                    if (await tasks.TryActivateInitializedTaskAsync(task.Id, cancellationToken))
                        _logger.LogInformation("Recovered initialized module task: taskId={TaskId}", task.Id);
                }
                catch (Exception ex)
                {
                    await tasks.TryFailTaskTransitionAsync(
                        task.Id,
                        "initializing",
                        "模块初始化恢复失败，任务未进入执行队列",
                        DateTime.UtcNow,
                        cancellationToken);
                    _logger.LogError(ex, "Failed to recover initialized module task: taskId={TaskId}", task.Id);
                }
                reconciledTransientIds.Add(task.Id);
            }

            foreach (var task in ownedTasks.Where(task => task.Status == "updating"))
            {
                try
                {
                    await group.Single().CommitUpsertAsync(
                        CreateContext(task, $"update:recovery:{task.Id}", scope.ServiceProvider),
                        cancellationToken);
                    if (await tasks.TryFinishEditableTaskUpdateAsync(task.Id, cancellationToken))
                        _logger.LogInformation("Recovered edited module task: taskId={TaskId}", task.Id);
                }
                catch (Exception ex)
                {
                    await tasks.TryFailTaskTransitionAsync(
                        task.Id,
                        "updating",
                        "模块编辑恢复失败，任务已停止，请重跑确认配置",
                        DateTime.UtcNow,
                        cancellationToken);
                    _logger.LogError(ex, "Failed to recover edited module task: taskId={TaskId}", task.Id);
                }
                reconciledTransientIds.Add(task.Id);
            }
        }

        foreach (var task in existingTasks.Where(task =>
                     (task.Status is "initializing" or "updating")
                     && !reconciledTransientIds.Contains(task.Id)))
        {
            await tasks.TryFailTaskTransitionAsync(
                task.Id,
                task.Status,
                "模块生命周期处理器不可用，任务未进入执行队列",
                DateTime.UtcNow,
                cancellationToken);
        }
    }

    private async Task FailTransitionBestEffortAsync(
        int taskId,
        string expectedStatus,
        string message,
        Exception sourceError)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
            await tasks.TryFailTaskTransitionAsync(
                taskId,
                expectedStatus,
                message,
                DateTime.UtcNow,
                CancellationToken.None);
        }
        catch (Exception transitionError)
        {
            _logger.LogError(
                transitionError,
                "Failed to preserve module task transition failure: taskId={TaskId}, expectedStatus={ExpectedStatus}, sourceError={SourceError}",
                taskId,
                expectedStatus,
                sourceError.Message);
        }
    }

    private IModuleTaskLifecycleHandler? ResolveHandler(
        IServiceProvider services,
        BatchTask task,
        bool required)
    {
        if (!_contributions.TaskTypeToDefinition.TryGetValue(task.TaskType, out var registered)
            || !string.Equals(registered.Module.Id, task.OwnerModuleId, StringComparison.Ordinal))
        {
            if (required)
                throw new InvalidOperationException($"任务 {task.Id} 的所有者模块不可用");
            return null;
        }

        var handlers = services.GetServices<IModuleTaskLifecycleHandler>()
            .Where(handler => string.Equals(handler.TaskType, task.TaskType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (handlers.Count == 1)
            return handlers[0];
        if (handlers.Count > 1 || required)
            throw new InvalidOperationException($"任务类型 {task.TaskType} 的生命周期处理器数量必须为 1，当前为 {handlers.Count}");
        return null;
    }

    private static ModuleTaskLifecycleContext CreateContext(
        BatchTask task,
        string operationId,
        IServiceProvider services) => new()
    {
        OperationId = operationId,
        Services = services,
        Task = new ModuleTaskSnapshot
        {
            TaskId = task.Id,
            TaskType = task.TaskType,
            OwnerModuleId = task.OwnerModuleId,
            ExecutionKind = task.ExecutionKind,
            Status = task.Status,
            Total = task.Total,
            Completed = task.Completed,
            Failed = task.Failed,
            Config = task.Config
        }
    };
}
