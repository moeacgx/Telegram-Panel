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

public sealed class PersistentModuleTaskRunnerTests
{
    [Fact]
    public async Task 延后会释放执行槽并在到期后重新领取完成()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"telegram-panel-persistent-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PersistentModuleTasks:Enabled"] = "true",
                ["PersistentModuleTasks:PollIntervalSeconds"] = "1",
                ["PersistentModuleTasks:MaxConcurrent"] = "1"
            })
            .Build());
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddScoped<IBatchTaskRepository, BatchTaskRepository>();
        services.AddScoped<BatchTaskManagementService>();

        var handler = new DeferThenCompleteHandler();
        services.AddSingleton<IModulePersistentTaskHandler>(handler);
        services.AddSingleton<IModuleTaskLifecycleHandler>(handler);
        var moduleRegistry = new ModuleRegistry();
        var module = new RunnerTestModule();
        var moduleContext = new ModuleHostContext("1.31.76", Path.GetTempPath());
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
        services.AddSingleton<BatchTaskExecutionControlService>();
        services.AddSingleton<BatchTaskStartupRecoveryService>();
        services.AddSingleton<PersistentModuleTaskBackgroundService>();

        var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<PersistentModuleTaskBackgroundService>();
        try
        {
            int taskId;
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
                taskId = (await tasks.CreateTaskAsync(new BatchTask
                {
                    TaskType = RunnerTestModule.TaskType,
                    OwnerModuleId = RunnerTestModule.ModuleId,
                    ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                    Total = 1
                })).Id;
            }

            await runner.StartAsync(CancellationToken.None);
            var deferredAt = await handler.Deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using (var scope = provider.CreateScope())
            {
                var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
                var deferred = await tasks.GetTaskAsync(taskId);
                Assert.Equal("pending", deferred!.Status);
                Assert.NotNull(deferred.NextEligibleAtUtc);
            }

            var completedAt = await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(completedAt - deferredAt >= TimeSpan.FromSeconds(2));

            using (var scope = provider.CreateScope())
            {
                var tasks = scope.ServiceProvider.GetRequiredService<BatchTaskManagementService>();
                var completed = await tasks.GetTaskAsync(taskId);
                Assert.Equal("completed", completed!.Status);
                Assert.Equal(1, completed.Completed);
                Assert.Equal(2, handler.ExecutionCount);
            }
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    private sealed class DeferThenCompleteHandler : IModulePersistentTaskHandler, IModuleTaskLifecycleHandler
    {
        private int _executionCount;

        public string TaskType => RunnerTestModule.TaskType;
        public int ExecutionCount => Volatile.Read(ref _executionCount);
        public TaskCompletionSource<DateTimeOffset> Deferred { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DateTimeOffset> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(
            IModulePersistentTaskExecutionHost host,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _executionCount) == 1)
            {
                var deferredAt = DateTimeOffset.UtcNow;
                await host.DeferAsync(deferredAt.AddSeconds(3), "等待再次执行", cancellationToken);
                Deferred.TrySetResult(deferredAt);
                return;
            }

            await host.CompleteAsync(1, 0, "执行完成", cancellationToken);
            Completed.TrySetResult(DateTimeOffset.UtcNow);
        }

        public Task ValidateAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PrepareDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task CommitDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task AbortDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ReconcileAsync(IReadOnlyCollection<int> existingTaskIds, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RunnerTestModule : ITelegramPanelModule, IModuleTaskProvider
    {
        public const string ModuleId = "test.runner";
        public const string TaskType = "test.runner.defer";

        public ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Name = "持久任务调度测试模块",
            Version = "1.0.0"
        };

        public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
        {
        }

        public void MapEndpoints(
            Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,
            ModuleHostContext context)
        {
        }

        public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context)
        {
            yield return new ModuleTaskDefinition
            {
                TaskType = TaskType,
                ExecutionKind = ModuleTaskExecutionKinds.Persistent,
                DisplayName = "延后重领测试",
                CreateRoute = "/ext/test.runner/settings"
            };
        }
    }
}
