using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Core.Services;
using TelegramPanel.Data;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Services;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ModuleTaskLifecycleServiceTests
{
    [Fact]
    public async Task Delete_calls_prepare_then_commit_after_host_row_is_removed()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: false);
        var task = await harness.Tasks.GetTaskAsync(harness.TaskId);

        await harness.Lifecycle.DeleteAsync(task!, "delete-operation");

        Assert.Null(await harness.Tasks.GetTaskAsync(harness.TaskId));
        Assert.Equal(new[] { "prepare:delete-operation", "commit:delete-operation" }, harness.Handler.Calls);
    }

    [Fact]
    public async Task Delete_calls_abort_and_keeps_host_row_when_host_delete_fails()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: true);
        var task = await harness.Tasks.GetTaskAsync(harness.TaskId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Lifecycle.DeleteAsync(task!, "failed-delete"));

        Assert.Equal("模拟宿主删除失败", error.Message);
        Assert.NotNull(await harness.Tasks.GetTaskAsync(harness.TaskId));
        Assert.Equal(new[] { "prepare:failed-delete", "abort:failed-delete" }, harness.Handler.Calls);
    }

    [Fact]
    public async Task Reconcile_passes_only_owned_task_ids_to_each_unique_handler()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: false);
        var otherOwnerTask = await harness.Tasks.CreateTaskAsync(new BatchTask
        {
            TaskType = "module.task",
            OwnerModuleId = "other.module",
            ExecutionKind = ModuleTaskExecutionKinds.Persistent,
            Total = 1
        });

        await harness.Lifecycle.ReconcileAsync();

        Assert.Equal(new[] { harness.TaskId }, harness.Handler.ReconciledTaskIds);
        Assert.DoesNotContain(otherOwnerTask.Id, harness.Handler.ReconciledTaskIds);
    }

    [Fact]
    public async Task Delete_does_not_dispatch_old_owner_task_to_current_module_handler()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: false);
        var oldOwnerTask = await harness.Tasks.CreateTaskAsync(new BatchTask
        {
            TaskType = "module.task",
            OwnerModuleId = "old.module",
            ExecutionKind = ModuleTaskExecutionKinds.Persistent,
            Total = 1
        });

        await harness.Lifecycle.DeleteAsync(oldOwnerTask, "old-owner-delete");

        Assert.Null(await harness.Tasks.GetTaskAsync(oldOwnerTask.Id));
        Assert.Empty(harness.Handler.Calls);
    }

    [Fact]
    public async Task Validate_dispatches_owned_paused_draft_to_module_handler()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: false);
        var task = await harness.Tasks.GetTaskAsync(harness.TaskId);

        await harness.Lifecycle.ValidateAsync(task!, "edit-validation");

        Assert.Equal(new[] { "validate:edit-validation" }, harness.Handler.Calls);
    }

    [Fact]
    public async Task Runtime_attention_is_persisted_on_host_task()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: false);
        var heartbeat = DateTime.UtcNow;

        await harness.Tasks.UpdateTaskRuntimeStateAsync(
            harness.TaskId,
            "paused",
            "需要人工检查",
            heartbeat,
            true);

        var reloaded = await harness.Tasks.GetTaskAsync(harness.TaskId);
        Assert.Equal("paused", reloaded!.RuntimePhase);
        Assert.Equal("需要人工检查", reloaded.RuntimeMessage);
        Assert.Equal(heartbeat, reloaded.HeartbeatAtUtc);
        Assert.True(reloaded.RequiresAttention);
    }

    [Fact]
    public async Task Startup_recovery_is_shared_across_runners_and_executes_only_once()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: false, initialStatus: "running");

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => harness.StartupRecovery.EnsureRecoveredAsync(CancellationToken.None)));

        Assert.Equal("pending", (await harness.Tasks.GetTaskAsync(harness.TaskId))!.Status);
        Assert.Equal(1, harness.Handler.ReconcileCallCount);
        Assert.Equal(new[] { harness.TaskId }, harness.Handler.ReconciledTaskIds);
    }

    [Fact]
    public async Task Startup_recovery_confirms_interrupted_pausing_task_as_paused()
    {
        await using var harness = await CreateHarnessAsync(throwOnDelete: false, initialStatus: "pausing");

        await harness.StartupRecovery.EnsureRecoveredAsync(CancellationToken.None);

        Assert.Equal("paused", (await harness.Tasks.GetTaskAsync(harness.TaskId))!.Status);
        Assert.Equal(1, harness.Handler.ReconcileCallCount);
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        bool throwOnDelete,
        string initialStatus = "paused")
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(connection);
        services.AddDbContext<AppDbContext>((provider, options) =>
            options.UseSqlite(provider.GetRequiredService<SqliteConnection>()));
        if (throwOnDelete)
            services.AddScoped<IBatchTaskRepository, ThrowingBatchTaskRepository>();
        else
            services.AddScoped<IBatchTaskRepository, BatchTaskRepository>();
        services.AddScoped<BatchTaskManagementService>();
        var handler = new RecordingLifecycleHandler("module.task");
        services.AddSingleton<IModuleTaskLifecycleHandler>(handler);
        var moduleRegistry = new ModuleRegistry();
        var module = new TestTaskModule();
        var moduleContext = new ModuleHostContext("1.0.0", Path.GetTempPath());
        moduleRegistry.Add(new LoadedModule(
            module.Manifest.Id,
            module.Manifest.Version,
            false,
            module,
            moduleContext,
            module.Manifest,
            ModuleRootPath: null));
        services.AddSingleton(moduleRegistry);
        services.AddSingleton(provider => new ModuleContributionRegistry(
            provider.GetRequiredService<ModuleRegistry>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModuleContributionRegistry>>(),
            provider));
        services.AddSingleton<ModuleTaskLifecycleService>();
        services.AddSingleton<BatchTaskStartupRecoveryService>();

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.BatchTasks.Add(new BatchTask
            {
                TaskType = "module.task",
                OwnerModuleId = "test.module",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                Status = initialStatus,
                Total = 1,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var tasks = provider.GetRequiredService<BatchTaskManagementService>();
        var taskId = (await tasks.GetAllTasksAsync()).Single().Id;
        return new TestHarness(
            connection,
            provider,
            tasks,
            provider.GetRequiredService<ModuleTaskLifecycleService>(),
            provider.GetRequiredService<BatchTaskStartupRecoveryService>(),
            handler,
            taskId);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        public TestHarness(
            SqliteConnection connection,
            ServiceProvider provider,
            BatchTaskManagementService tasks,
            ModuleTaskLifecycleService lifecycle,
            BatchTaskStartupRecoveryService startupRecovery,
            RecordingLifecycleHandler handler,
            int taskId)
        {
            _connection = connection;
            _provider = provider;
            Tasks = tasks;
            Lifecycle = lifecycle;
            StartupRecovery = startupRecovery;
            Handler = handler;
            TaskId = taskId;
        }

        public BatchTaskManagementService Tasks { get; }
        public ModuleTaskLifecycleService Lifecycle { get; }
        public BatchTaskStartupRecoveryService StartupRecovery { get; }
        public RecordingLifecycleHandler Handler { get; }
        public int TaskId { get; }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingLifecycleHandler : IModuleTaskLifecycleHandler
    {
        public RecordingLifecycleHandler(string taskType) => TaskType = taskType;
        public string TaskType { get; }
        public List<string> Calls { get; } = new();
        public IReadOnlyCollection<int> ReconciledTaskIds { get; private set; } = Array.Empty<int>();
        public int ReconcileCallCount { get; private set; }

        public Task ValidateAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"validate:{context.OperationId}");
            return Task.CompletedTask;
        }

        public Task PrepareDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"prepare:{context.OperationId}");
            return Task.CompletedTask;
        }

        public Task CommitDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"commit:{context.OperationId}");
            return Task.CompletedTask;
        }

        public Task AbortDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add($"abort:{context.OperationId}");
            return Task.CompletedTask;
        }

        public Task ReconcileAsync(IReadOnlyCollection<int> existingTaskIds, CancellationToken cancellationToken = default)
        {
            ReconcileCallCount++;
            ReconciledTaskIds = existingTaskIds.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class TestTaskModule : ITelegramPanelModule, IModuleTaskProvider
    {
        public ModuleManifest Manifest { get; } = new()
        {
            Id = "test.module",
            Name = "生命周期测试模块",
            Version = "1.0.0"
        };

        public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
        {
        }

        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints, ModuleHostContext context)
        {
        }

        public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context)
        {
            yield return new ModuleTaskDefinition
            {
                TaskType = "module.task",
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                DisplayName = "生命周期测试任务",
                CreateRoute = "/ext/test.module/tasks"
            };
        }
    }

    private sealed class ThrowingBatchTaskRepository : BatchTaskRepository
    {
        public ThrowingBatchTaskRepository(AppDbContext context) : base(context)
        {
        }

        public override Task DeleteAsync(BatchTask entity) =>
            throw new InvalidOperationException("模拟宿主删除失败");
    }
}
