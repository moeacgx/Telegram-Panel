using TelegramPanel.Core.Services;
using TelegramPanel.Data.Entities;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;

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

            var ownedIds = existingTasks
                .Where(task => string.Equals(task.TaskType, group.Key, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(task.OwnerModuleId, registered.Module.Id, StringComparison.Ordinal))
                .Select(task => task.Id)
                .ToArray();
            await group.Single().ReconcileAsync(ownedIds, cancellationToken);
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
