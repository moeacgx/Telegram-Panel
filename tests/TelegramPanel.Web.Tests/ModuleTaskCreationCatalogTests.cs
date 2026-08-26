using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Modules;
using TelegramPanel.Web.Modules;
using TelegramPanel.Web.Modules.BuiltIn;
using Xunit;

namespace TelegramPanel.Web.Tests;

public sealed class ModuleTaskCreationCatalogTests
{
    [Fact]
    public void ConfigureServicesAtomically_restores_full_descriptor_snapshot_after_failure()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TestServiceMarker>();
        services.AddScoped<TestServiceReplacement>();
        var snapshot = services.ToArray();

        Assert.Throws<InvalidOperationException>(() => ModuleBootstrapper.ConfigureServicesAtomically(
            new MutatingThrowModule(),
            services,
            new ModuleHostContext("1.0.0", Path.GetTempPath())));

        Assert.Equal(snapshot.Length, services.Count);
        for (var index = 0; index < snapshot.Length; index++)
            Assert.Same(snapshot[index], services[index]);
    }

    [Fact]
    public void Registry_keeps_all_definitions_but_only_exposes_valid_editors_for_creation()
    {
        var validEditorType = typeof(ValidTaskEditor).AssemblyQualifiedName!;
        var module = new TestTaskModule(
            new ModuleTaskDefinition
            {
                Category = "user",
                TaskType = "editor-backed",
                DisplayName = "有效编辑器",
                EditorComponentType = validEditorType
            },
            new ModuleTaskDefinition
            {
                Category = "user",
                TaskType = "route-only",
                DisplayName = "独立页面",
                CreateRoute = "/ext/test/settings"
            },
            new ModuleTaskDefinition
            {
                Category = "user",
                TaskType = "metadata-only",
                DisplayName = "仅任务元数据"
            },
            new ModuleTaskDefinition
            {
                Category = "user",
                TaskType = "not-a-component",
                DisplayName = "无效组件类型",
                EditorComponentType = typeof(string).AssemblyQualifiedName
            },
            new ModuleTaskDefinition
            {
                Category = "user",
                TaskType = "invalid-contract",
                DisplayName = "缺少编辑器参数",
                EditorComponentType = typeof(EditorWithoutDraftChanged).AssemblyQualifiedName
            },
            new ModuleTaskDefinition
            {
                Category = "user",
                TaskType = "route-and-editor",
                DisplayName = "独立页优先",
                CreateRoute = "/ext/test/settings",
                EditorComponentType = validEditorType
            });

        var externalContributions = CreateContributions(module, builtIn: false);

        Assert.Equal(6, externalContributions.Tasks.Count);
        Assert.Equal(6, externalContributions.TaskTypeToDefinition.Count);
        Assert.Equal("/ext/test/settings", externalContributions.TaskTypeToDefinition["route-only"].Definition.CreateRoute);

        Assert.Empty(externalContributions.CreatableTasks);
        Assert.All(externalContributions.Tasks, task => Assert.False(task.CanCreate));

        var builtInContributions = CreateContributions(module, builtIn: true);
        var creatable = Assert.Single(builtInContributions.CreatableTasks);
        Assert.Equal("editor-backed", creatable.Definition.TaskType);
        Assert.True(creatable.CanCreate);

        Assert.False(builtInContributions.TaskTypeToDefinition["route-only"].CanCreate);
        Assert.False(builtInContributions.TaskTypeToDefinition["metadata-only"].CanCreate);
        Assert.False(builtInContributions.TaskTypeToDefinition["not-a-component"].CanCreate);
        Assert.False(builtInContributions.TaskTypeToDefinition["invalid-contract"].CanCreate);
        Assert.False(builtInContributions.TaskTypeToDefinition["route-and-editor"].CanCreate);
    }

    [Fact]
    public void Built_in_catalog_keeps_context_created_tasks_out_of_generic_create_dialog()
    {
        var contributions = CreateContributions(new TaskCatalogModule("1.0.0"), builtIn: true);

        Assert.Contains(BatchTaskTypes.ChannelInviteUsers, contributions.TaskTypeToDefinition.Keys);
        Assert.Contains(BatchTaskTypes.BotSetAdmins, contributions.TaskTypeToDefinition.Keys);

        var creatableTypes = contributions.CreatableTasks
            .Select(x => x.Definition.TaskType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(4, creatableTypes.Count);
        Assert.Contains(BatchTaskTypes.UserChatActive, creatableTypes);
        Assert.Contains(BatchTaskTypes.ChannelGroupPrivateCreate, creatableTypes);
        Assert.Contains(BatchTaskTypes.ChannelGroupPublicize, creatableTypes);
        Assert.Contains(BatchTaskTypes.AutoChangeLoginEmail, creatableTypes);
        Assert.DoesNotContain(BatchTaskTypes.ChannelInviteUsers, creatableTypes);
        Assert.DoesNotContain(BatchTaskTypes.BotSetAdmins, creatableTypes);
    }

    [Theory]
    [InlineData(ModuleTaskExecutionKinds.Batch)]
    [InlineData(ModuleTaskExecutionKinds.Persistent)]
    public void External_task_is_creatable_only_with_owned_route_and_matching_contract(string executionKind)
    {
        const string taskType = "external-owned-route";
        var module = new TestTaskModule(new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = taskType,
            DisplayName = "外部模块任务",
            ExecutionKind = executionKind,
            CreateRoute = "/ext/test.task-catalog/tasks/create"
        });

        var services = new ServiceCollection();
        if (executionKind == ModuleTaskExecutionKinds.Persistent)
            services.AddSingleton<IModulePersistentTaskHandler>(new TestPersistentHandler(taskType));
        else
            services.AddSingleton<IModuleTaskHandler>(new TestBatchHandler(taskType));
        services.AddSingleton<IModuleTaskLifecycleHandler>(new TestLifecycleHandler(taskType));
        using var provider = services.BuildServiceProvider();

        var contributions = CreateContributions(module, builtIn: false, provider);

        var creatable = Assert.Single(contributions.CreatableTasks);
        Assert.Equal(taskType, creatable.Definition.TaskType);
        Assert.Equal(executionKind, creatable.Definition.ExecutionKind);
        Assert.True(creatable.CanCreate);
    }

    [Theory]
    [InlineData("https://example.com/ext/test.task-catalog/tasks")]
    [InlineData("/ext/another-module/tasks")]
    [InlineData("/ext/test.task-catalog/../another-module/tasks")]
    [InlineData("/ext/test.task-catalog\\tasks")]
    public void External_task_rejects_routes_outside_its_module(string createRoute)
    {
        const string taskType = "unsafe-route";
        var module = new TestTaskModule(new ModuleTaskDefinition
        {
            TaskType = taskType,
            DisplayName = "不安全路由",
            CreateRoute = createRoute
        });
        var services = new ServiceCollection();
        services.AddSingleton<IModuleTaskHandler>(new TestBatchHandler(taskType));
        services.AddSingleton<IModuleTaskLifecycleHandler>(new TestLifecycleHandler(taskType));
        using var provider = services.BuildServiceProvider();

        var contributions = CreateContributions(module, builtIn: false, provider);

        Assert.False(Assert.Single(contributions.Tasks).CanCreate);
        Assert.Empty(contributions.CreatableTasks);
        Assert.Contains(contributions.Diagnostics, message => message.Contains("CreateRoute", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void External_task_requires_exactly_one_matching_executor_and_lifecycle(
        bool duplicateExecutor,
        bool duplicateLifecycle)
    {
        const string taskType = "ambiguous-contract";
        var module = new TestTaskModule(new ModuleTaskDefinition
        {
            TaskType = taskType,
            DisplayName = "合同冲突",
            CreateRoute = "/ext/test.task-catalog/tasks/create"
        });
        var services = new ServiceCollection();
        services.AddSingleton<IModuleTaskHandler>(new TestBatchHandler(taskType));
        if (duplicateExecutor)
            services.AddSingleton<IModuleTaskHandler>(new TestBatchHandler(taskType));
        services.AddSingleton<IModuleTaskLifecycleHandler>(new TestLifecycleHandler(taskType));
        if (duplicateLifecycle)
            services.AddSingleton<IModuleTaskLifecycleHandler>(new TestLifecycleHandler(taskType));
        using var provider = services.BuildServiceProvider();

        var contributions = CreateContributions(module, builtIn: false, provider);

        Assert.False(Assert.Single(contributions.Tasks).CanCreate);
        Assert.Contains(contributions.Diagnostics, message => message.Contains("需要唯一执行器和生命周期处理器", StringComparison.Ordinal));
    }

    [Fact]
    public void Persistent_definition_does_not_accept_batch_executor()
    {
        const string taskType = "persistent-with-batch-handler";
        var module = new TestTaskModule(new ModuleTaskDefinition
        {
            TaskType = taskType,
            DisplayName = "执行器类型不匹配",
            ExecutionKind = ModuleTaskExecutionKinds.Persistent,
            CreateRoute = "/ext/test.task-catalog/tasks/create"
        });
        var services = new ServiceCollection();
        services.AddSingleton<IModuleTaskHandler>(new TestBatchHandler(taskType));
        services.AddSingleton<IModuleTaskLifecycleHandler>(new TestLifecycleHandler(taskType));
        using var provider = services.BuildServiceProvider();

        var contributions = CreateContributions(module, builtIn: false, provider);

        Assert.False(Assert.Single(contributions.Tasks).CanCreate);
        Assert.Contains(contributions.Diagnostics, message => message.Contains("executor=0", StringComparison.Ordinal));
    }

    [Fact]
    public void Conflicting_task_types_are_not_indexed_or_creatable()
    {
        const string taskType = "conflicting-task";
        var first = new TestTaskModule(new ModuleTaskDefinition
        {
            TaskType = taskType,
            DisplayName = "第一个任务",
            CreateRoute = "/ext/test.task-catalog/tasks/create"
        });
        var second = new AlternateTestTaskModule(new ModuleTaskDefinition
        {
            TaskType = taskType,
            DisplayName = "第二个任务",
            CreateRoute = "/ext/test.task-catalog-alternate/tasks/create"
        });
        var registry = new ModuleRegistry();
        AddModule(registry, first, builtIn: false);
        AddModule(registry, second, builtIn: false);
        var services = new ServiceCollection();
        services.AddSingleton<IModuleTaskHandler>(new TestBatchHandler(taskType));
        services.AddSingleton<IModuleTaskLifecycleHandler>(new TestLifecycleHandler(taskType));
        using var provider = services.BuildServiceProvider();

        var contributions = new ModuleContributionRegistry(
            registry,
            NullLogger<ModuleContributionRegistry>.Instance,
            provider);

        Assert.Equal(2, contributions.Tasks.Count);
        Assert.Empty(contributions.CreatableTasks);
        Assert.DoesNotContain(taskType, contributions.TaskTypeToDefinition.Keys);
        Assert.Contains(contributions.Diagnostics, message => message.Contains("任务类型冲突", StringComparison.Ordinal));
    }

    private static ModuleContributionRegistry CreateContributions(
        ITelegramPanelModule module,
        bool builtIn,
        IServiceProvider? serviceProvider = null)
    {
        var registry = new ModuleRegistry();
        AddModule(registry, module, builtIn);

        return new ModuleContributionRegistry(
            registry,
            NullLogger<ModuleContributionRegistry>.Instance,
            serviceProvider);
    }

    private static void AddModule(ModuleRegistry registry, ITelegramPanelModule module, bool builtIn)
    {
        var context = new ModuleHostContext("1.0.0", Path.GetTempPath());
        registry.Add(new LoadedModule(
            module.Manifest.Id,
            module.Manifest.Version,
            builtIn,
            module,
            context,
            module.Manifest,
            ModuleRootPath: null));
    }

    public sealed class ValidTaskEditor : ComponentBase
    {
        [Parameter]
        public ModuleTaskDraft Draft { get; set; }

        [Parameter]
        public EventCallback<ModuleTaskDraft> DraftChanged { get; set; }
    }

    public sealed class EditorWithoutDraftChanged : ComponentBase
    {
        [Parameter]
        public ModuleTaskDraft Draft { get; set; }
    }

    private sealed class TestTaskModule : ITelegramPanelModule, IModuleTaskProvider
    {
        private readonly IReadOnlyList<ModuleTaskDefinition> _definitions;

        public TestTaskModule(params ModuleTaskDefinition[] definitions)
        {
            _definitions = definitions;
        }

        public ModuleManifest Manifest { get; } = new()
        {
            Id = "test.task-catalog",
            Name = "任务目录测试模块",
            Version = "1.0.0"
        };

        public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
        {
        }

        public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context) => _definitions;
    }

    private sealed class AlternateTestTaskModule : ITelegramPanelModule, IModuleTaskProvider
    {
        private readonly IReadOnlyList<ModuleTaskDefinition> _definitions;

        public AlternateTestTaskModule(params ModuleTaskDefinition[] definitions)
        {
            _definitions = definitions;
        }

        public ModuleManifest Manifest { get; } = new()
        {
            Id = "test.task-catalog-alternate",
            Name = "备用任务目录测试模块",
            Version = "1.0.0"
        };

        public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
        {
        }

        public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context) => _definitions;
    }

    private sealed class TestBatchHandler : IModuleTaskHandler
    {
        public TestBatchHandler(string taskType) => TaskType = taskType;
        public string TaskType { get; }
        public Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestPersistentHandler : IModulePersistentTaskHandler
    {
        public TestPersistentHandler(string taskType) => TaskType = taskType;
        public string TaskType { get; }
        public Task ExecuteAsync(IModulePersistentTaskExecutionHost host, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestLifecycleHandler : IModuleTaskLifecycleHandler
    {
        public TestLifecycleHandler(string taskType) => TaskType = taskType;
        public string TaskType { get; }
        public Task ValidateAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PrepareDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CommitDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AbortDeleteAsync(ModuleTaskLifecycleContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReconcileAsync(IReadOnlyCollection<int> existingTaskIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MutatingThrowModule : ITelegramPanelModule
    {
        public ModuleManifest Manifest { get; } = new()
        {
            Id = "test.configure-rollback",
            Name = "服务回滚测试模块",
            Version = "1.0.0"
        };

        public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
        {
            services.RemoveAt(0);
            services.Insert(0, ServiceDescriptor.Singleton(typeof(TestServiceReplacement), typeof(TestServiceReplacement)));
            services.AddSingleton<TestServiceMarker>();
            throw new InvalidOperationException("模拟模块注册失败");
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
        {
        }
    }

    private sealed class TestServiceMarker
    {
    }

    private sealed class TestServiceReplacement
    {
    }
}
