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

## 模块目录结构

模块默认使用持久化目录（Docker 内默认：`/data/modules`；可用配置 `Modules:RootPath` 覆盖）：

```
modules/
  state.json
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

### 3) 可选：提供自定义创建器（任务中心内嵌表单）

在 `ModuleTaskDefinition.EditorComponentType` 指定组件类型的 `AssemblyQualifiedName`，并实现组件参数：

- `Draft`（`ModuleTaskDraft`）
- `DraftChanged`（`EventCallback<ModuleTaskDraft>`）

宿主会用 `DynamicComponent` 渲染该组件，提交时使用 `Draft.Total` / `Draft.Config` 创建任务。

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
