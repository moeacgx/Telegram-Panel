# 模块系统（可安装/可卸载）

本项目提供一个“模块系统”框架，用于把**任务能力**与**外部 API 能力**以模块形式分发、安装、启用与回滚，避免因为扩展功能不兼容导致主站不可用。

> 当前实现为**同进程插件**（动态加载程序集）。为稳定起见：安装/启用/停用/卸载后通常需要**重启服务**才能生效。

## 目标

- 可安装/可卸载：面板内上传模块包并管理启用状态
- 版本管理：同一模块可安装多个版本，支持切换 `ActiveVersion`
- 依赖管理：模块声明依赖的模块与版本范围（`>=1.2.3 <2.0.0`）
- 兼容性：模块声明宿主版本区间（`host.min/host.max`）
- 失败自动兜底：模块加载失败时自动尝试回滚到 `LastGoodVersion`，否则自动禁用以避免拖垮系统

## 扩展点一览（任务 / API / UI）

模块除 `ConfigureServices` / `MapEndpoints` 外，还可以选择性实现以下接口（位于 `TelegramPanel.Modules.Abstractions`）：

- `IModuleTaskProvider`：声明模块提供的任务类型（让任务中心可动态展示/创建）
- `IModuleTaskHandler`：实现任务中心后台执行器（让后台真正能跑该任务）
- `IModuleApiProvider`：声明模块提供的外部 API 类型（让 API 管理页面可动态创建配置项）
- `IModuleUiProvider`：声明模块扩展 UI 导航与页面（让面板可挂载模块自定义页面）

> 说明：模块启用/停用通常需要重启；宿主启动时只会加载“启用”的模块，因此 UI/任务/API 列表会随启用状态变化。

## 配置入口与“窗口编辑”（推荐）

如果你的模块需要“配置界面”，推荐以 **模块页面**（`IModuleUiProvider.GetPages`）的形式提供，然后在 `ModuleTaskDefinition.CreateRoute` 中指向该页面的路由：

- 模块页面路由固定为：`/ext/{ModuleId}/{PageKey}`
- 当 `CreateRoute` 指向 `/ext/...` 时：
  - “新建任务”弹窗会提供“打开窗口/前往页面”两种方式
  - “任务中心”会在顶部的“持续任务（可配置）”区域展示该任务，并提供“编辑”按钮直接打开配置窗口

这样可以获得类似“配置窗口”的体验，同时仍复用模块页面渲染能力（`DynamicComponent`）。

> 提醒：保存配置应尽量做到“立即生效”；只有模块启用/停用（影响 DI/后台服务装载）才需要重启。

## 模块目录结构

模块默认使用持久化目录（Docker 内默认：`/data/modules`；可用配置 `Modules:RootPath` 覆盖）：

```
modules/
  state.json
  active/    # 预留：当前启用版本（部分实现会用到）
  data/      # 模块自有持久化数据（推荐放这里）
  packages/
    <moduleId>/
      <version>.tpm
  installed/
    <moduleId>/
      <version>/
        manifest.json
        lib/
          <entry assembly>.dll
          ...依赖 dll...
        ...其他资源文件...
  staging/   # 安装中临时目录
  trash/     # 删除后回收目录（可手动找回）
```

`state.json` 记录模块是否启用、当前使用版本与 last-good：

```json
{
  "schemaVersion": 1,
  "modules": [
    {
      "id": "builtin.kick-api",
      "enabled": true,
      "activeVersion": "1.2.3",
      "lastGoodVersion": "1.2.3",
      "installedVersions": ["1.2.3"],
      "builtIn": true
    }
  ]
}
```

## 模块数据持久化（推荐）

模块运行时可通过 `ModuleHostContext.ModulesRootPath` 获取模块系统根目录。推荐把模块自有数据放到：

`Path.Combine(context.ModulesRootPath, "data", Manifest.Id)`

示例（把路径封装为 Paths 并注入到 DI）：

```csharp
public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
{
    var dataRoot = Path.Combine(context.ModulesRootPath, "data", Manifest.Id);
    services.AddSingleton(new MyModulePaths(dataRoot));
}
```

这样可以保证 Docker/本机部署下都能持久化，并且不会污染宿主目录结构。

## 模块包格式（.tpm / .zip）

模块包本质是 Zip 文件（扩展名可为 `.tpm` 或 `.zip`），解压后的根目录必须包含：

- `manifest.json`
- `lib/<entry assembly>.dll`（入口程序集）

> 小提示：如果你是“右键压缩整个文件夹”，压缩包里通常会多一层根目录（`<folder>/manifest.json`）。宿主会尝试自动识别并提升这一层；但更推荐直接把 `manifest.json` 和 `lib/` 放在压缩包根目录。

安装流程会先解压到 `staging/` 并做基础校验，然后移动到 `installed/<id>/<version>/`，并将原包存档到 `packages/<id>/<version>.tpm` 便于留档与回滚。

## 模块打包（可选）

仓库内提供了一个基于 Docker 的打包脚本（无需本机安装 `dotnet`），用于把任意模块项目打包为可上传的 `.tpm`：

```powershell
powershell tools/package-module.ps1 -Project "src/YourModule/YourModule.csproj" -Manifest "src/YourModule/manifest.json"
```

产物默认输出到：`artifacts/modules/<moduleId>-<version>.tpm`

> 说明：该脚本依赖 Docker（会拉取/使用 `mcr.microsoft.com/dotnet/sdk:8.0` 镜像）。首次执行会比较慢属正常现象。

### 轻量打包（推荐）

模块运行时会与宿主共享一批“边界程序集”（例如 `TelegramPanel.*`、`Microsoft.Extensions.*`、`Microsoft.AspNetCore.*`、`MudBlazor` 等）。
这些程序集即使被打进模块包里，宿主也会强制从 Default ALC 解析（避免类型身份不一致），因此**携带它们只会徒增包体积**。

打包时可加 `-Slim` 开关自动剔除这类共享程序集：

```powershell
powershell tools/package-module.ps1 -Project "src/YourModule/YourModule.csproj" -Manifest "src/YourModule/manifest.json" -Slim
```

### 更激进的轻量打包（仅限 TelegramPanel 宿主）

如果确定目标宿主就是 TelegramPanel 主程序（必带 EFCore/Sqlite/WTelegramClient 等依赖），并且你希望把模块包做到尽可能小，可以使用 `-SlimHost`：

- 额外剔除：`Microsoft.EntityFrameworkCore*`、`Microsoft.Data.Sqlite`、`SQLitePCLRaw*`、`WTelegramClient`、`SixLabors.ImageSharp`、`PhoneNumbers` 等宿主内置依赖
- 剔除 `runtimes/`（多平台 SQLite native，体积占比很高）
- 剔除 `wwwroot/_content/MudBlazor`（静态资源由宿主提供）

```powershell
powershell tools/package-module.ps1 -Project "src/YourModule/YourModule.csproj" -Manifest "src/YourModule/manifest.json" -SlimHost
```

## manifest.json（示例）

```json
{
  "id": "example.kick-api",
  "name": "示例：踢人 API",
  "version": "1.0.0",
  "host": { "min": "1.0.0", "max": "2.0.0" },
  "dependencies": [
    { "id": "builtin.kick-api", "range": ">=1.0.0 <2.0.0" }
  ],
  "entry": {
    "assembly": "Example.KickApi.dll",
    "type": "Example.KickApi.ExampleKickApiModule"
  }
}
```

版本范围（`dependencies[].range`）支持：

- `1.2.3`（等于）
- `>=1.2.3`
- `>=1.2.3 <2.0.0`（空格分隔多个条件）

## 模块代码示例（入口点）

模块入口类型需实现 `TelegramPanel.Modules.ITelegramPanelModule`：

```csharp
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TelegramPanel.Modules;

namespace Example.KickApi;

public sealed class ExampleKickApiModule : ITelegramPanelModule
{
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "example.kick-api",
        Name = "示例：踢人 API",
        Version = "1.0.0",
        Host = new HostCompatibility { Min = "1.0.0", Max = "2.0.0" },
        Entry = new ModuleEntryPoint { Assembly = "Example.KickApi.dll", Type = typeof(ExampleKickApiModule).FullName! }
    };

    public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
    {
        // 可在这里注册该模块用到的 DI 服务（注意：启用/停用通常需要重启才能生效）
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
    {
        endpoints.MapPost("/api/example", () => Results.Ok(new { ok = true }));
    }
}
```

## UI 模块项目模板（Razor 组件）

如果你的模块需要提供页面（`IModuleUiProvider.GetPages`），推荐把模块做成 `Microsoft.NET.Sdk.Razor` 项目（类似 Razor Class Library），例如：

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../../src/TelegramPanel.Modules.Abstractions/TelegramPanel.Modules.Abstractions.csproj" />
    <PackageReference Include="MudBlazor" Version="7.*" />
  </ItemGroup>
</Project>
```

建议在模块根目录放一个 `_Imports.razor`，把常用命名空间一次性导入（例如 `MudBlazor`、`Microsoft.AspNetCore.Components` 等），避免每个页面重复写。

> 注意：模块项目引用 `MudBlazor` 主要用于编译期；运行时会跟随宿主加载。若模块需要自带静态资源（CSS/JS），宿主不会自动暴露模块的 `wwwroot`，你需要在 `MapEndpoints` 中自行提供静态文件访问（或把样式/脚本内联到页面里）。

## 开发/调试建议

模块开发最简单的闭环是：**打包 → 在面板中上传/安装 → 重启服务 → 验证**。

- 安装/启用/停用外部模块通常需要重启（因为 `ConfigureServices` 在宿主构建 DI 之前执行）。
- 开发阶段可以把版本号（`manifest.json` 的 `version`）按 `1.0.0 -> 1.0.1 -> ...` 递增，避免缓存/回滚机制干扰排查。

## 任务扩展（Task）

### 1) 声明任务类型（可在“新建任务”中出现）

实现 `IModuleTaskProvider` 返回 `ModuleTaskDefinition`：

```csharp
public sealed class MyTaskModule : ITelegramPanelModule, IModuleTaskProvider
{
    public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context)
    {
        yield return new ModuleTaskDefinition
        {
            Category = "user",
            TaskType = "my_task_type",
            DisplayName = "我的任务",
            Description = "自定义任务说明",
            Icon = MudBlazor.Icons.Material.Filled.Task,
            Order = 100
        };
    }
}
```

### 2) 实现任务执行器（后台真正运行）

实现 `IModuleTaskHandler` 并在 `ConfigureServices` 注册到 DI：

```csharp
public sealed class MyTaskHandler : IModuleTaskHandler
{
    public string TaskType => "my_task_type";

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken ct)
    {
        // host.Config 是创建任务时写入的 Config 字符串（建议是 JSON）
        // host.Services 可解析宿主的服务（AccountTelegramToolsService 等）
        // host.UpdateProgressAsync(...) 用于写入任务中心进度

        var completed = 0;
        var failed = 0;

        // 示例：跑 10 步
        for (var i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!await host.IsStillRunningAsync(ct))
                return;

            completed++;
            await host.UpdateProgressAsync(completed, failed, ct);
        }
    }
}

public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
{
    services.AddSingleton<IModuleTaskHandler, MyTaskHandler>();
}
```

## 持续任务（常驻后台能力）模式（推荐）

有些能力并不是“一次性批量任务”，而是需要模块启用后长期运行的后台监听/通知等。这类能力建议：

1) 在模块内注册 `HostedService` 常驻后台运行（`ConfigureServices` 中 `services.AddHostedService<...>()`）。
2) **不要**把它塞进批量任务队列（`IModuleTaskHandler`），避免队列阻塞或误触发。
3) 仍然可以在“新建任务/任务中心”里提供一个“配置入口”，做法是注册 `IModuleTaskProvider` 并设置 `CreateRoute` 指向模块配置页：

```csharp
public IEnumerable<ModuleTaskDefinition> GetTasks(ModuleHostContext context)
{
    yield return new ModuleTaskDefinition
    {
        Category = "bot",
        TaskType = "bot_monitor_notify",
        DisplayName = "监控频道更新通知",
        Description = "常驻后台监听，不占用批量任务队列；在配置里启用即可生效。",
        Icon = MudBlazor.Icons.Material.Filled.NotificationsActive,
        CreateRoute = "/ext/pro.bot-monitor-notify/settings",
        Order = 100
    };
}
```

这种模式的体验是：

- “新建任务”里点击后打开配置窗口（或跳转配置页）
- “任务中心”顶部可直接编辑该持续任务配置（方便增删频道/目标等）

### 示例：批量订阅/加群/退群（用户任务）

该类任务的典型形态是“多账号 × 多链接”的组合执行，并允许在 UI 中切换操作模式：

- `join`：订阅频道 / 加入群组
- `leave`：取消订阅 / 退群

建议的 `host.Config`（JSON）结构：

```json
{
  "Mode": "join",
  "AccountIds": [1, 2],
  "Links": [
    "https://t.me/xxx",
    "t.me/+hash",
    "@username",
    "tg://join?invite=hash"
  ],
  "DelayMs": 2000
}
```

模块执行器中可直接解析并调用宿主服务（示例）：

- `TelegramPanel.Core.Services.Telegram.AccountTelegramToolsService.JoinChatOrChannelAsync(...)`
- `TelegramPanel.Core.Services.Telegram.AccountTelegramToolsService.LeaveChatOrChannelAsync(...)`

### 3) 可选：提供自定义创建器（任务中心内嵌表单）

在 `ModuleTaskDefinition.EditorComponentType` 指定组件类型的 `AssemblyQualifiedName`，并实现组件参数：

- `Draft`（`ModuleTaskDraft`）
- `DraftChanged`（`EventCallback<ModuleTaskDraft>`）

宿主会用 `DynamicComponent` 渲染该组件，提交时使用 `Draft.Total` / `Draft.Config` 创建任务。

实用建议（针对“多账号/多目标”类任务）：

- 在编辑器里做基础校验：未选择账号、未填写链接时 `CanSubmit=false` 并给出 `ValidationError`
- `Total` 建议按“账号数 × 链接数”或“账号数 × 用户名数”等可预估的总步数计算，便于任务中心展示进度
- 支持筛选：例如“账号分类筛选/搜索”，减少用户选择成本

## 外部 API 扩展（API）

### 1) 声明 API 类型（可在“API 管理→新建 API”中出现）

实现 `IModuleApiProvider` 返回 `ModuleApiTypeDefinition`：

```csharp
public IEnumerable<ModuleApiTypeDefinition> GetApis(ModuleHostContext context)
{
    yield return new ModuleApiTypeDefinition
    {
        Type = "my_api",
        DisplayName = "我的 API",
        Route = "/api/my",
        Description = "自定义接口说明",
        Order = 100
    };
}
```

### 2) 映射 endpoints 并读取配置项

宿主会把 API 配置写入 `ExternalApi:Apis`（含 `Type` / `Enabled` / `ApiKey` / `Config(JSON object)`）。模块在 endpoint 里自行按 `X-API-Key` 匹配对应配置项并执行。

> 内置 kick 接口提供了一个参考实现：`src/TelegramPanel.Web/ExternalApi/KickApi.cs`

## UI 扩展（页面/导航）

### 1) 添加导航链接（可选）

实现 `IModuleUiProvider.GetNavItems` 返回 `ModuleNavItem`（Title/Href/Icon/Group/Order）。

### 2) 添加模块页面（推荐）

实现 `IModuleUiProvider.GetPages` 返回 `ModulePageDefinition`：

- `Key`：页面键（模块内唯一）
- `ComponentType`：组件类型 `AssemblyQualifiedName`

宿主提供统一入口路由：`/ext/{moduleId}/{pageKey}`，会动态加载并渲染模块组件。

### 3) 模块页面参数约定（非常重要）

宿主会把 `ModuleId` 与 `PageKey` 作为组件参数注入，因此模块页面组件必须声明以下两个参数，否则运行时会 500（组件不接受宿主注入的参数）：

```razor
@code {
  [Parameter] public string ModuleId { get; set; } = "";
  [Parameter] public string PageKey { get; set; } = "";
}
```

> 如果你的页面完全不需要这两个值，也必须保留参数声明。

## 依赖与加载（外部模块）

外部模块会从 `installed/<id>/<version>/lib/` 通过独立的 `AssemblyLoadContext` 加载入口程序集。

实践建议：

- 把入口程序集及其依赖（包含第三方 NuGet）都放进 `lib/`，最简单方式是对模块项目执行 `dotnet publish`（打包脚本已内置）。
- 避免依赖宿主的同名 DLL（版本不一致时容易出错）。
- 如果模块需要引用宿主工程里的类型，推荐通过 `ProjectReference` 引用 `TelegramPanel.Modules.Abstractions`/`TelegramPanel.Core`/`TelegramPanel.Data` 等项目（按需即可），并随模块一起发布到 `lib/`。

## 认证/授权（端点安全）

- **模块页面**：作为面板的一部分渲染，通常受宿主的后台登录控制（管理员登录开启时会要求授权）。
- **模块 API 端点**（`MapEndpoints`）：请显式选择：
  - `AllowAnonymous()`：公开接口（务必自行做好鉴权/限流/防泄露）
  - 或 `RequireAuthorization()`：跟随宿主后台登录鉴权

如果是“外置链接/匿名链接”类能力，建议：

- 使用随机 token 作为访问凭证
- 做好限流（按 token + IP）
- 返回 `no-store` 防缓存

## 运行时行为（启用/回滚）

- 启用模块会进行宿主版本校验与依赖校验（依赖模块必须存在且版本满足范围）。
- 启动时加载模块：
  - 加载失败会尝试回滚到 `LastGoodVersion`；
  - 回滚也失败则自动 `Enabled=false`（避免拖垮系统）。

## 安全与稳定提示

同进程插件无法做到“绝对不崩”。为了降低风险：

- 只安装可信来源的模块包
- 出现异常时先停用模块并重启
- 建议在生产环境使用“灰度/备份”方式试装模块

后续如需更强隔离，可以把模块改为“独立进程 Module Host”模式（主站通过 HTTP/gRPC 调用），进一步降低崩溃风险。
