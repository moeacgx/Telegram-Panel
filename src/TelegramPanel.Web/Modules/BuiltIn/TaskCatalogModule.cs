using MudBlazor;
using TelegramPanel.Core.BatchTasks;
using TelegramPanel.Modules;
using TelegramPanel.Web.Services;

namespace TelegramPanel.Web.Modules.BuiltIn;

public sealed class TaskCatalogModule : ITelegramPanelModule, IModuleTaskProvider
{
    public TaskCatalogModule(string version)
    {
        Manifest = new ModuleManifest
        {
            Id = "builtin.tasks",
            Name = "任务：内置批量任务",
            Version = version,
            Host = new HostCompatibility(),
            Entry = new ModuleEntryPoint { Assembly = "", Type = typeof(TaskCatalogModule).FullName ?? "" }
        };
    }

    public ModuleManifest Manifest { get; }

    public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
    {
        // 内置任务的执行由宿主 BatchTaskBackgroundService 负责；这里只提供“元数据”用于 UI 展示与创建。
        services.AddSingleton<IModuleTaskRerunBuilder, UserChatActiveTaskRerunBuilder>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
    {
        // 无 endpoints
    }

    public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context)
    {
        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.ChannelInviteUsers,
            DisplayName = "频道批量邀请用户",
            Description = "从频道列表提交批量邀请后在任务中心执行，支持取消以停止后续邀请。",
            Icon = Icons.Material.Filled.PersonAdd,
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = false,
                CanResume = false,
                CanEdit = false,
                CanRerun = true
            },
            Order = 105
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.GroupInviteUsers,
            DisplayName = "群组批量邀请用户",
            Description = "从群组列表提交批量邀请后在任务中心执行，支持取消以停止后续邀请。",
            Icon = Icons.Material.Filled.GroupAdd,
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = false,
                CanResume = false,
                CanEdit = false,
                CanRerun = true
            },
            Order = 106
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.UserJoinSubscribe,
            DisplayName = "批量加群/订阅/启用Bot",
            Description = "使用账号批量加入频道/群组，或启用/停用外部 Bot；从账号列表提交后在后台执行。",
            Icon = Icons.Material.Filled.GroupAdd,
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = false,
                CanRerun = true
            },
            Order = 110
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.UserChatActive,
            DisplayName = "账号持续活跃（群组/频道/Bot）",
            Description = "按账号分类持续向指定群组、频道或 Bot 私聊发送消息规则，支持多段文字、每条独立图片字典、间隔抖动和随机/队列循环。",
            Icon = Icons.Material.Filled.Chat,
            EditorComponentType = typeof(TelegramPanel.Web.Components.Dialogs.UserChatActiveTaskEditor).AssemblyQualifiedName ?? "",
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = true,
                CanRerun = true,
                EditComponentType = typeof(TelegramPanel.Web.Components.Dialogs.UserChatActiveTaskEditor).AssemblyQualifiedName ?? "",
                AutoPauseBeforeEdit = true
            },
            Order = 120
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.ChannelGroupPrivateCreate,
            DisplayName = "自动创建私密频道/群组",
            Description = "按账号分类批量创建私密频道或群组，支持标题变量、固定头像/图片字典头像、数量上限与延时抖动。",
            Icon = Icons.Material.Filled.AddCircle,
            EditorComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPrivateCreateTaskEditor).AssemblyQualifiedName ?? "",
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = true,
                CanRerun = true,
                EditComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPrivateCreateTaskEditor).AssemblyQualifiedName ?? "",
                AutoPauseBeforeEdit = true
            },
            Order = 130
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.ChannelGroupPublicize,
            DisplayName = "私密频道/群组公开化",
            Description = "从系统创建的私密频道/群组中按分类挑选候选对象，批量设置标题、描述、用户名与头像后公开。",
            Icon = Icons.Material.Filled.Public,
            EditorComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPublicizeTaskEditor).AssemblyQualifiedName ?? "",
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = true,
                CanRerun = true,
                EditComponentType = typeof(TelegramPanel.Web.Components.Dialogs.ChannelGroupPublicizeTaskEditor).AssemblyQualifiedName ?? "",
                AutoPauseBeforeEdit = true
            },
            Order = 140
        };

        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = BatchTaskTypes.AutoChangeLoginEmail,
            DisplayName = "自动更改登录邮箱",
            Description = "按账号分类扫描 777000 登录邮箱重置通知，匹配后用 Cloud Mail 邮箱验证码自动换绑登录邮箱。",
            Icon = Icons.Material.Filled.MarkEmailRead,
            EditorComponentType = typeof(TelegramPanel.Web.Components.Dialogs.AutoChangeLoginEmailTaskEditor).AssemblyQualifiedName ?? "",
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = false,
                CanResume = false,
                CanEdit = true,
                CanRerun = true,
                EditComponentType = typeof(TelegramPanel.Web.Components.Dialogs.AutoChangeLoginEmailTaskEditor).AssemblyQualifiedName ?? ""
            },
            Order = 150
        };

        yield return new ModuleTaskDefinition
        {
            Category = "bot",
            TaskType = BatchTaskTypes.BotChannelInviteUsers,
            DisplayName = "Bot频道批量邀请用户",
            Description = "从 Bot 频道列表提交批量邀请后在任务中心执行，支持取消以停止后续邀请。",
            Icon = Icons.Material.Filled.PersonAddAlt1,
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = false,
                CanResume = false,
                CanEdit = false,
                CanRerun = true
            },
            Order = 205
        };

        yield return new ModuleTaskDefinition
        {
            Category = "bot",
            TaskType = BatchTaskTypes.BotChannelSetAdminsByAccount,
            DisplayName = "Bot频道批量设置管理员（账号执行）",
            Description = "使用指定账号进入 Bot 频道并批量设置管理员，适用于 Bot 无法直接完成授权的场景。",
            Icon = Icons.Material.Filled.AdminPanelSettings,
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = true,
                CanResume = true,
                CanEdit = false,
                CanRerun = true
            },
            Order = 210
        };

        yield return new ModuleTaskDefinition
        {
            Category = "bot",
            TaskType = BatchTaskTypes.BotSetAdmins,
            DisplayName = "Bot频道批量设置管理员（机器人执行）",
            Description = "由机器人对已管理的频道批量设置管理员权限。",
            Icon = Icons.Material.Filled.SmartToy,
            TaskCenter = new ModuleTaskCenterCapabilities
            {
                CanPause = false,
                CanResume = false,
                CanEdit = false,
                CanRerun = true
            },
            Order = 220
        };

    }
}
