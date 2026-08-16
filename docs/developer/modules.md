# 模块系统（可安装/可卸载）

本项目提供一个“模块系统”框架，用于把**任务能力**、**外部 API 能力**与**后台管理能力**以模块形式分发、安装、启用与回滚，避免因为扩展功能不兼容导致主站不可用。

> 当前实现为**同进程插件**（动态加载程序集）。为稳定起见：安装/启用/停用/卸载后通常需要**重启服务**才能生效。

## 目标

- 可安装/可卸载：面板内上传模块包并管理启用状态
- 版本管理：同一模块可安装多个版本，支持切换 `ActiveVersion`
- 依赖管理：模块声明依赖的模块与版本范围（`>=1.2.3 <2.0.0`）
- 兼容性：模块声明宿主版本区间（`host.min/host.max`）
- 失败自动兜底：模块加载失败时自动尝试回滚到 `LastGoodVersion`，否则自动禁用以避免拖垮系统
- Vue 后台适配：新模块优先提供管理端 API，由宿主 Vue 后台承载页面；旧 Razor 页面继续保留兼容入口

## 面板入口

- 「模块管理」：安装/启用/停用/卸载模块（通常需重启生效）
- 「API 管理」：基于已启用模块，创建对应的外部 API 配置项（`X-API-Key` 鉴权）
- 「任务中心」：基于已启用模块，动态展示任务类型与分类

## 示例扩展（可选）

- 模块打包脚本：`powershell tools/package-module.ps1 -Project <csproj> -Manifest <manifest.json>`（产物默认输出到 `artifacts/modules/`）
- 外部 API 示例：模块可通过 `IModuleApiProvider` 暴露 API 类型，并在 `MapEndpoints` 中注册自己的公开接口
- 后台页面示例：新模块优先提供 `/api/panel/extensions/{module-slug}` 管理接口，由 Vue 后台承载页面；旧 Razor 页面走兼容入口

## 扩展点一览（任务 / API / UI）

模块除 `ConfigureServices` / `MapEndpoints` 外，还可以选择性实现以下接口（位于 `TelegramPanel.Modules.Abstractions`）：

- `IModuleTaskProvider`：声明模块提供的任务类型（让任务中心可动态展示/创建）
- `IModuleTaskHandler`：实现任务中心后台执行器（让后台真正能跑该任务）
- `IModuleTaskRerunBuilder`：为“重新运行”提供专用的配置重建逻辑（适合需要清洗旧配置的任务）
- `IModuleApiProvider`：声明模块提供的外部 API 类型（让 API 管理页面可动态创建配置项）
- `IModuleUiProvider`：声明模块扩展导航与旧 Razor 页面（Vue 后台会通过兼容入口挂载）

> 说明：模块启用/停用通常需要重启；宿主启动时只会加载“启用”的模块，因此 UI/任务/API 列表会随启用状态变化。

## 账号代理由宿主统一处理

模块不需要再次实现账号代理选择，也不要在模块设置中重复保存代理地址、账号或密码。

账号在导入或手动登录前已经明确选择出口。后续模块只要按 `accountId` 调用宿主的账号服务，宿主就会在创建 Telegram 客户端前，根据账号当前状态自动应用：

- 账号绑定的 HTTP、SOCKS5、MTProxy、WARP 或 Resin 代理
- 账号选择继承的 Telegram 全局代理
- 账号明确选择的直连

导入或登录选择“已有代理”后，宿主会把该 `ProxyId` 长期保存到账号；选择“全局代理”
则保存继承标记并跟随以后对全局出口的修改。模块只消费账号当前路由，不需要区分代理是
普通代理、Resin、WARP 或全局引用，也不应在模块配置中再保存一份代理快照。

账号导入的 `warp_pool` 策略只从已启用、`DesiredEnabled=true` 且运行状态为 `active` 的受管
WARP 中选择，按已绑定账号数和代理 ID 稳定排序。候选代理在首次连接期间仍必须取得 WARP
使用租约；占用冲突时尝试下一候选，全部冲突则在 Telegram 连接前失败。该策略严禁调用 WARP
创建接口或回退直连。

账号导入的 `warp_per_account` 策略只能由宿主导入编排器执行：先校验受管 WARP 环境和单次 10 个
账号上限，再为每个账号创建新 WARP、冻结首次连接快照、取得使用租约，并在成功入库后绑定新
`ProxyId`。Session 验证或绑定失败时，未绑定账号的新代理必须删除，运行档案只保留 `deleted`
审计记录。模块和任务执行器不得自行调用该策略创建容器；需要账号出口时只消费宿主已保存的
账号路由。

验收至少覆盖：无候选不创建资源、新建 WARP 在成功导入后绑定账号、失败导入清理未绑定代理、
超过 10 个账号在创建 Docker 资源前拒绝，以及批量导入不会回退直连。

优先复用 `AccountTelegramToolsService`、`ChannelService`、`GroupService` 等宿主服务。
以任务执行器收到的 `IModuleTaskExecutionHost host` 为例：

```csharp
var accountTools = host.Services.GetRequiredService<AccountTelegramToolsService>();
var result = await accountTools.JoinChatOrChannelAsync(accountId, target, cancellationToken);

if (!result.Success)
    throw new InvalidOperationException(result.Error ?? "加入群组或频道失败");
```

这些服务最终通过宿主的 `ITelegramClientPool` 获取客户端。客户端池会用 `IAccountProxyResolver` 解析账号路由，并在首次连接前应用代理。账号切换代理时，宿主会先严格断开旧客户端；模块下一次按账号获取客户端时会使用新路由。

`AccountTelegramToolsService`、`ChannelService`、`GroupService` 和 `ITelegramClientPool` 位于
宿主 `TelegramPanel.Core`，不是 `TelegramPanel.Modules.Abstractions` 中的长期稳定契约。
模块如果引用这些类型，应收紧 `manifest.json` 的宿主版本范围，并针对目标宿主版本重新
编译和验证。打包时不要携带自己的 `TelegramPanel.Core.dll`，由宿主提供边界程序集。

模块需要遵守以下边界：

- 不要自行 `new WTelegram.Client(...)`，否则会绕过账号代理、客户端池和统一的连接生命周期。
- 不要调用带 `AccountProxyResolution` 覆盖参数的客户端池重载；该入口只供登录、导入等宿主内部流程冻结首次出口。
- 不要在静态字段或单例中长期缓存 `WTelegram.Client`。代理切换后旧实例会被释放，长任务应通过宿主服务重新获取账号客户端。
- 不要直接读取代理表或持久化代理凭据。代理的检测、启停、切换和 WARP 生命周期由代理管理功能负责。
- 启用的普通 HTTP/SOCKS5 与 Resin 代理由宿主每 5 分钟刷新出口快照；模块只消费宿主
  返回的最新元数据，不应按账号重复发起探测或创建 Resin Lease。WARP 仍由独立的容器
  维护流程处理，普通代理巡检不得进入 WARP 重启路径。
- 不要在模块中自行实现账号导入或登录。新账号尚未入库时没有可继承的账号路由，应调用宿主导入/登录流程，让宿主在第一条 Telegram 请求前冻结出口。

账号代理只约束该账号的 Telegram 客户端。模块自己创建的 `HttpClient`、第三方 API 请求或其它网络连接不会自动继承账号出口；这类连接如果确实需要独立代理，应作为另一项明确能力设计，不能假设它与账号代理共用路由。

用户侧的路由类型、WARP 和 Resin 配置见
[代理管理与账号出口](../guides/proxy-management.md)。

## 长时间运行任务与重启恢复（重要）

如果你的模块实现的是“持续监控 / 长轮询 / 等待条件出现后再执行”的任务，需要注意下面这几个规则：

### 1）批量任务框架默认仍然是“一次执行”

- 宿主的 `BatchTaskBackgroundService` 会从数据库里捞出 `pending` 任务，调用对应的 `IModuleTaskHandler.ExecuteAsync(...)`
- **只要你的 `ExecuteAsync(...)` 返回，宿主就会把这条批量任务标记为 `completed` 或 `failed`**
- 所以“持续任务”并不是宿主自动帮你持续；而是你的执行器必须自己维持循环，并在适当的时候才返回

换句话说：

- 一次性任务：执行器跑完就返回
- 持续监控任务：执行器自己 `while (...)` 循环，直到达到停止条件、被用户暂停/取消，或者你明确决定结束

### 2）持续任务必须轮询 `IsStillRunningAsync(...)`

宿主通过 `IModuleTaskExecutionHost.IsStillRunningAsync(...)` 把“当前任务是否还允许继续跑”暴露给模块。

模块作者在长循环里必须定期检查：

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    if (!await host.IsStillRunningAsync(cancellationToken))
        return;

    // 你的持续监控逻辑
}
```

推荐检查位置：

- 每一轮大循环开始时
- 每次 `Task.Delay(...)` 前后
- 每次外部请求、网络调用、数据库批量操作前

这样用户在任务中心点击“暂停 / 恢复 / 取消”时，模块才能及时响应。

### 3）持续任务的运行状态必须写回 `task.Config`

如果你的任务需要跨轮次记住状态，例如：

- 已处理过哪些用户名 / 频道 / 消息
- 上次检查时间
- 当前游标 / offset / pageToken
- 外部系统返回的中间状态

不要只存在内存里，应该定期序列化回 `BatchTask.Config`。

宿主提供了 `BatchTaskManagementService.UpdateTaskConfigAsync(...)`，推荐在模块里这样做：

```csharp
var taskManagement = host.Services.GetRequiredService<BatchTaskManagementService>();

config.LastCheckTime = DateTime.UtcNow;
config.ProcessedIds = processedIds.ToList();

await taskManagement.UpdateTaskConfigAsync(
    host.TaskId,
    JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
```

这样做的目的有两个：

- 任务详情里能看到实时状态
- 宿主重启后，任务可以从上次进度继续恢复，而不是从头开始

#### 一次性任务的失败明细也必须持久化

适用版本：v1.31.44 及以上。

一次性批量任务如果只更新 `Failed` 计数而丢弃异常，任务详情只能显示失败数量，
无法帮助管理员判断账号、目标或上游错误。处理器应在每次失败后立即把有界的失败明细写回
`BatchTask.Config`，不要只在 `ExecuteAsync` 返回前统一保存。

宿主内置的自动创建私密频道/群组任务使用以下运行态合同：

```json
{
  "recent_failures": [
    {
      "time_utc": "2026-08-02T01:02:03Z",
      "account_id": 15,
      "target_type": "channel",
      "target": "示例频道",
      "reason": "Telegram 返回的失败原因"
    }
  ]
}
```

- 最多保留最近 20 条，避免任务配置无限增长。
- 原因限制为单行 500 字符；不得写入密码、Token、代理凭据或 Session 内容。
- 记录异常日志时使用任务 ID、账号 ID 等结构化字段，保留服务端排障证据。
- 取消引发的 `OperationCanceledException` 不计为业务失败。

验收时创建一条可稳定失败的测试任务，确认 `GET /api/panel/tasks/{id}` 的 `config`
包含 `recent_failures`，并且任务详情出现“最近失败”。若仍只有失败计数，检查处理器是否在
`catch` 中调用 `UpdateTaskConfigAsync`，以及前端是否加载了完整任务详情。

回滚到旧版本不需要数据库迁移；旧处理器会忽略未知的 `recent_failures` JSON 字段。
回滚后新发生的失败将不再追加说明，已有记录仍保留在任务配置中。

### 4）宿主现在会自动恢复“中断中的 running 任务”

当前宿主实现中，`BatchTaskBackgroundService` 启动时会把数据库里残留的 `running` 批量任务重新置回 `pending`，然后由后台执行器重新拉起。

这意味着：

- 如果程序异常退出 / 重启
- 只要这条任务上次状态还停留在 `running`
- 宿主下次启动后会自动尝试恢复它

因此，**模块作者必须把持续任务写成“可重复进入、可从 Config 恢复”的形式**。

也就是说，不要依赖：

- 进程内静态变量
- 单次启动时生成但未持久化的随机状态
- 只存在内存里的队列 / 集合 / 指针

而应该依赖：

- `task.Config`
- 模块自己的持久化数据目录
- 外部系统里可重复读取的状态

### 5）“持续任务”和“Cron 计划任务”不是一回事

宿主里现在有两套概念：

- **批量任务（BatchTask）**
  说明：提交后立即执行一次；是否持续由模块执行器自己决定
- **计划任务（ScheduledTask / Cron）**
  说明：由宿主按 Cron 周期反复创建新的批量任务

适用建议：

- 想要“进程内一直守着等机会”：用持续批量任务
- 想要“每隔一段时间触发一次检查”：用 Cron 计划任务

如果模块页面没有走任务中心的“Cron 计划”创建入口，而是自己直接 `CreateTaskAsync(...)`，那它创建出来的就只是普通批量任务，不会自动变成计划任务。

### 6）持续任务的停止条件要写清楚

模块作者最好明确区分以下几种结束原因：

- 用户主动暂停 / 取消
- 达到运行时长上限
- 所有目标都已处理完成
- 当前资源暂时不足，但后续可能恢复

其中最后一种很常见，比如：

- 暂时没有可用私密频道
- 目标接口限流
- 外部站点临时不可达

这类情况如果业务上允许后续继续等待，**不要直接结束任务**，而应该：

1. 写入错误/提示状态到 `Config`
2. 等待一段时间
3. 进入下一轮重试

示例：

```csharp
if (availableChannels.Count == 0)
{
    config.Error = "当前没有可用私密频道";
    await SaveConfigAsync(taskManagement, host.TaskId, config);

    if (!await DelayWithPauseCheckAsync(host, TimeSpan.FromMinutes(5), cancellationToken))
        return;

    continue;
}
```

### 7）给持续任务的一个实践建议

如果你的模块是“监控类任务”，推荐至少维护这些字段：

- `StartedAtUtc`
- `LastCheckTime`
- `Error`
- `Canceled`
- 业务游标（例如 `AssignedUsernames` / `HandledMessageIds` / `LastOffset`）

这样无论是排错、前端展示，还是重启恢复，都会清晰很多。

## Bot 更新订阅（allowed_updates）

如果模块需要消费 Telegram Bot API 的更新（`getUpdates` / Webhook），**不要**在模块里对同一个 Bot Token 自行启动轮询器（会导致 409 Conflict）。请通过宿主的 `BotUpdateHub` 订阅/广播更新。

注意：宿主会为 `getUpdates` / `setWebhook` 固定传入 `allowed_updates` 白名单（见 `src/TelegramPanel.Core/Services/Telegram/BotUpdateHub.cs` 的 `AllowedUpdatesJson`）。当前已包含成员变更与入群请求：`chat_member`、`chat_join_request`；后续如你的模块需要其它更新类型，需要先在宿主侧扩展该白名单并发布宿主版本。

## 配置入口与“窗口编辑”

如果你的模块需要配置界面，优先使用模块自带静态 Vue 页，或在宿主仓库中提供 Vue 原生页。模块在 `MapEndpoints` 中提供管理端 API，页面负责展示和保存配置。

对还没有 Vue 原生页面的旧模块，可以继续用 **Razor 模块页面**（`IModuleUiProvider.GetPages`）作为兼容配置入口。模块可以通过导航项或自己的页面入口指向该路由；不要仅为了打开独立配置页而注册一个可创建任务定义：

- 模块页面路由固定为：`/ext/{ModuleId}/{PageKey}`
- `ModuleTaskDefinition.CreateRoute` 仅表示已有任务的独立编辑入口；仅有该字段的任务不会出现在“新建任务”弹窗。

`CreateRoute` 用于已有任务的配置和编辑入口。任务中心编辑外部任务时会把 `taskId` 附加到该路由；页面必须按任务 ID 读取和保存配置。只有 `canCreate=true` 且有专用表单的任务类型才会显示在新建任务列表。

`EditorComponentType` 是宿主内置任务创建/编辑器的合同；`TaskCenter.EditComponentType` 仍用于旧 Razor 编辑入口。外部模块不要仅依赖 .NET 组件类型向 Vue 浏览器扩展页面。

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
      "id": "example.module",
      "enabled": true,
      "activeVersion": "1.2.3",
      "lastGoodVersion": "1.2.3",
      "installedVersions": ["1.2.3"],
      "builtIn": false
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

> 默认会按宿主内置依赖做“轻量化打包”（等价于 `-SlimHost`）。如确需完整包可传 `-Full`（或 `-Slim:$false -SlimHost:$false`）。

产物默认输出到：`artifacts/modules/<moduleId>-<version>.tpm`

> 说明：该脚本依赖 Docker（会拉取/使用 `mcr.microsoft.com/dotnet/sdk:8.0` 镜像）。首次执行会比较慢属正常现象。

### 默认宿主轻量包（推荐）

不传打包模式时，脚本默认使用 `-SlimHost`。它会剔除两类由宿主提供的依赖：

- 共享边界程序集：`TelegramPanel.*`、`Microsoft.Extensions.*`、`Microsoft.AspNetCore.*`、`MudBlazor` 等
- 宿主内置依赖：`Microsoft.EntityFrameworkCore*`、`Microsoft.Data.Sqlite`、`SQLitePCLRaw*`、`WTelegramClient`、`SixLabors.ImageSharp`、`PhoneNumbers` 等

默认模式还会移除多平台 `runtimes/` 和宿主已经提供的 MudBlazor 静态资源。模块自己的
`wwwroot` 页面与资源会保留。共享边界程序集必须由 Default ALC 使用宿主版本；把它们
重复放进模块包只会增加体积，也可能造成类型身份不一致。

### 仅剔除共享边界程序集

如果模块确实带有宿主没有提供的原生运行时或第三方依赖，可显式使用 `-Slim`。该模式
只剔除共享边界程序集，不会删除整个 `runtimes/` 或宿主内置第三方 DLL：

```powershell
powershell tools/package-module.ps1 -Project "src/YourModule/YourModule.csproj" -Manifest "src/YourModule/manifest.json" -Slim
```

### 完整包（仅用于兼容性排障）

`-Full` 会保留 `dotnet publish` 的全部输出，包体明显更大。只有确认模块必须携带自己的
完整依赖，或正在定位轻量化剔除问题时才使用：

```powershell
powershell tools/package-module.ps1 -Project "src/YourModule/YourModule.csproj" -Manifest "src/YourModule/manifest.json" -Full
```

## manifest.json（示例）

```json
{
  "id": "example.echo-api",
  "name": "示例：Echo API",
  "version": "1.0.0",
  "host": { "min": "1.0.0", "max": "2.0.0" },
  "dependencies": [],
  "entry": {
    "assembly": "Example.EchoApi.dll",
    "type": "Example.EchoApi.ExampleEchoApiModule"
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

namespace Example.EchoApi;

public sealed class ExampleEchoApiModule : ITelegramPanelModule
{
    public ModuleManifest Manifest { get; } = new()
    {
        Id = "example.echo-api",
        Name = "示例：Echo API",
        Version = "1.0.0",
        Host = new HostCompatibility { Min = "1.0.0", Max = "2.0.0" },
        Entry = new ModuleEntryPoint { Assembly = "Example.EchoApi.dll", Type = typeof(ExampleEchoApiModule).FullName! }
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

## 宿主内置服务（模块可注入）

模块与宿主同进程运行，因此模块的 API/任务/页面都可以直接从 DI 获取宿主服务。

### 获取 Telegram 邮箱验证码（Cloud Mail）

宿主提供 `ITelegramEmailCodeService` 供模块复用“邮箱验证码”能力（例如：部分客户端会把验证码发送到邮箱而非短信）。

前置条件：在面板「系统设置」配置 `CloudMail:BaseUrl` / `CloudMail:Token` / `CloudMail:Domain`。

示例（在模块任意 DI 场景注入即可，如 `IModuleTaskHandler` / `MapEndpoints`）：

```csharp
using TelegramPanel.Modules;

public sealed class MyHandler : IModuleTaskHandler
{
    public string TaskType => "example.mail-code";
    private readonly ITelegramEmailCodeService _emailCodes;

    public MyHandler(ITelegramEmailCodeService emailCodes) => _emailCodes = emailCodes;

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken ct)
    {
        var r = await _emailCodes.TryGetLatestCodeByPhoneDigitsAsync("8413111454444", sinceUtc: DateTimeOffset.UtcNow.AddMinutes(-5), ct);
        // r.Success / r.Code
    }
}
```

内置 `auto_change_login_email` 任务也复用同一服务：处理器先按任务配置扫描 777000 系统通知窗口，
只在匹配登录邮箱重置提示（默认包含 `Settings > Privacy & Security > Login Email`）或显式
`force=true` 时调用 `AccountTelegramToolsService.SetLoginEmailAsync`，再通过 Cloud Mail 取码并调用
`ConfirmLoginEmailAsync`。模块若实现相同场景，应复用这些宿主服务，不要重复创建 Telegram 客户端或
绕过 Cloud Mail 配置。

### 调用宿主 AI 服务（推荐给模块复用）

宿主提供 `ITelegramPanelAiService`，模块可以直接复用主程序里已配置好的 OpenAI 兼容 AI 能力，不需要在模块里重复保存端点、Key 或自己再接一套 SDK。

前置条件：

- 在面板「系统设置 -> AI 设置」中已配置 `AI:OpenAI:Endpoint`
- 已配置 `AI:OpenAI:ApiKey`
- 已配置全局默认模型，或者模块调用时显式传入 `Model`
- 若系统设置里配置了 `AI:OpenAI:RetryCount`，模块调用也会自动享受同一套重试策略

当前宿主暴露两类能力：

- `ChooseActionAsync(...)`：根据消息文本、按钮列表、可选图片，返回动作决策
- `ReplyTextAsync(...)`：根据题目、上下文、可选图片，返回最终文本答案

相关契约位于：`src/TelegramPanel.Modules.Abstractions/AiServices.cs`

`ChooseActionAsync(...)` 的返回约定：

- `Success=true` 且 `Mode=click_button`：使用 `ButtonIndex`（0 基）点击按钮
- `Success=true` 且 `Mode=reply_text`：使用 `ReplyText` 发送文本
- `Success=false`：查看 `Error`
- `Reason` 仅用于日志或调试，不建议模块把它当成业务字段

示例（模块任务里调用宿主 AI 识别按钮）：

```csharp
using TelegramPanel.Modules;

public sealed class MyAiTaskHandler : IModuleTaskHandler
{
    public string TaskType => "example.ai-check";
    private readonly ITelegramPanelAiService _ai;

    public MyAiTaskHandler(ITelegramPanelAiService ai)
    {
        _ai = ai;
    }

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken ct)
    {
        var result = await _ai.ChooseActionAsync(
            new TelegramPanelAiChooseActionRequest(
                Model: null, // null 表示回退到系统设置里的默认模型
                MessageText: "请选择正确验证码",
                Buttons: new[]
                {
                    new TelegramPanelAiButtonOption(0, "12"),
                    new TelegramPanelAiButtonOption(1, "18"),
                    new TelegramPanelAiButtonOption(2, "21")
                },
                Image: null,
                Context: "这是 Telegram 群验证消息，请只返回最可靠动作。"),
            ct);

        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "AI 决策失败");

        if (string.Equals(result.Mode, "click_button", StringComparison.OrdinalIgnoreCase))
        {
            var buttonIndex = result.ButtonIndex ?? -1;
            // 这里结合你自己的 Telegram 调用链执行点击
        }
    }
}
```

示例（模块任务里调用宿主 AI 生成文本答案）：

```csharp
using TelegramPanel.Modules;

public sealed class MyAiReplyHandler : IModuleTaskHandler
{
    public string TaskType => "example.ai-reply";
    private readonly ITelegramPanelAiService _ai;

    public MyAiReplyHandler(ITelegramPanelAiService ai)
    {
        _ai = ai;
    }

    public async Task ExecuteAsync(IModuleTaskExecutionHost host, CancellationToken ct)
    {
        var result = await _ai.ReplyTextAsync(
            new TelegramPanelAiReplyTextRequest(
                Model: "gpt-4o-mini",
                Prompt: "你是 Telegram 验证助手，请只返回最终答案。",
                Query: "请计算：12 + 19 = ?",
                Image: null,
                Context: "不要解释，不要带多余符号。"),
            ct);

        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "AI 作答失败");

        var replyText = result.ReplyText ?? string.Empty;
        // 这里结合你自己的 Telegram 调用链发送 replyText
    }
}
```

建议：

- 优先把模型名做成模块配置项；未配置时传 `null`，回退全局默认模型
- 模块只关心 `Success / Error / Mode / ButtonIndex / ReplyText`，不要依赖具体提示词实现细节
- 若需要图像识别，传入 `TelegramPanelAiImageInput`，建议使用 JPEG 字节数组
- 模块不要自己拼 `/chat/completions` 或自己做端点规范化，这些都交给宿主

## 账号导出下载（Telethon / Tdata）

如果模块需要“下载某个账号的数据包”，建议优先使用宿主服务直接生成 Zip（同进程内调用），避免绕 HTTP 鉴权与 Cookie。

### 推荐方式：模块内直接调用导出服务

可注入：

- `TelegramPanel.Web.Services.AccountExportService`
- `TelegramPanel.Core.Services.AccountManagementService`

核心调用链：

1. 先通过 `AccountManagementService` 获取目标账号（或账号列表）
2. 调用 `AccountExportService.BuildAccountsZipAsync(accounts, ct, format)`
3. 将 `byte[]` 按模块自己的场景返回/落盘/上传

其中 `format`：

- `AccountExportFormat.Telethon`：导出 `.json + .session (+2fa.txt)`
- `AccountExportFormat.Tdata`：在以上基础上额外导出 `tdata/`

### HTTP 方式（备选）

宿主现有下载接口：

- `GET /downloads/accounts.zip`
- Query:
  - `ids=1,2,3`（可选，不传则导出全部）
  - `format=telethon|tdata`（不传默认 `telethon`）
  - `ts=<timestamp>`（可选，建议带上，避免浏览器缓存旧包）

注意：

- 若开启后台登录，接口受登录态保护（需带管理端 Cookie）
- 响应已设置 `no-store/no-cache`，但调用方仍建议加 `ts`

### Tdata 导出的实现要点（后续扩展必须保持）

1. `session -> telethon string` 时必须保留 Base64 padding（尾部 `=`）
2. `telethon string -> tdata` 时必须注入 `session.self.userId`
3. 生成 `telethon string` 时要优先选择“已授权 DCSession”（不是任意 DC）

否则会出现“包结构看似正常，但 Telegram Desktop 仍要求重新登录”。

## 新模块默认不要写 Razor 页面

主后台已经迁移到 Vue。这个迁移只改变宿主后台，不会自动把外部模块的 Razor 页面改成 Vue。模块如果继续通过 `IModuleUiProvider.GetPages` 注册页面，仍然会走 Blazor Server 兼容入口。

新模块需要管理界面时，优先选下面两种方式：

1. **宿主 Vue 原生页**：页面写在宿主 `frontend/src/views/extensions/`，模块只提供 `/api/panel/extensions/{module-slug}` 管理接口。
2. **模块自带静态 Vue 页**：模块使用普通 `Microsoft.NET.Sdk`，在 `wwwroot/` 放 `settings.html`、Vue、CSS、JS，并在 `MapEndpoints` 中自己暴露 `/ext/{moduleId}/settings` 和静态资源。

只有旧模块、临时过渡页面，或确实需要复用 Blazor 组件时，才使用下面的 Razor 兼容模式。

### 模块自带静态 Vue 页模板

静态 Vue 页不依赖 Blazor Server，也不需要 `MudBlazor`。模块项目建议使用普通 SDK：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../../src/TelegramPanel.Modules.Abstractions/TelegramPanel.Modules.Abstractions.csproj" />
    <Content Include="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

模块入口负责暴露页面和 API：

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
{
    var page = endpoints.MapGet("/ext/example.module/settings", GetSettingsPageAsync);
    var api = endpoints.MapGroup("/api/panel/extensions/example-module");
    api.MapGet("", GetStateAsync);
    api.MapPost("", SaveStateAsync);
}

public IEnumerable<ModuleNavItem> GetNavItems(ModuleHostContext context)
{
    yield return new ModuleNavItem
    {
        Title = "模块设置",
        Href = "/ext/example.module/settings",
        Group = "扩展模块",
        Order = 100
    };
}

public IEnumerable<ModulePageDefinition> GetPages(ModuleHostContext context)
    => Array.Empty<ModulePageDefinition>();
```

要点：

- 不实现旧 Razor 页面时，`GetPages()` 返回空。
- 静态资源不会被宿主自动映射，模块必须自己在 `MapEndpoints` 中提供资源访问接口，或把脚本样式内联到 HTML。
- 修改页面/API 后必须递增 `manifest.json` 的版本，重新打包并更新生产模块包。
- 如果线上仍看到旧 Razor 页面，通常是生产环境还装着旧 `.tpm`，或模块加载失败后回滚到了 `LastGoodVersion`。

## 旧版 UI 模块项目模板（Razor 组件，兼容模式）

如果你的模块已经有旧页面，或暂时没有对应的 Vue 原生页面，仍可以通过 `IModuleUiProvider.GetPages` 提供兼容 Razor 页面。此时可以把模块做成 `Microsoft.NET.Sdk.Razor` 项目（类似 Razor Class Library），例如：

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

旧 Razor 页面建议在模块根目录放一个 `_Imports.razor`，把常用命名空间一次性导入（例如 `MudBlazor`、`Microsoft.AspNetCore.Components` 等），避免每个页面重复写。

> 注意：模块项目引用 `MudBlazor` 主要用于旧页面编译期；运行时会跟随宿主加载。若模块需要自带静态资源（CSS/JS），宿主不会自动暴露模块的 `wwwroot`，你需要在 `MapEndpoints` 中自行提供静态文件访问（或把样式/脚本内联到页面里）。

## Vue 后台迁移后的模块页面约定

后台管理界面已经迁移到 Vue SPA，入口在 `/ui` 下。模块开发时需要区分三种页面形态：

1. **宿主 Vue 原生页面**：页面代码在宿主 `frontend/src/views/extensions/`，数据由模块提供 `/api/panel/extensions/{slug}` 管理接口。
2. **模块自带静态 Vue 页面**：页面和前端资源随 `.tpm` 打包，由模块自己的 endpoint 返回，通常入口是 `/ext/{moduleId}/settings`。
3. **模块原生 Razor 页面**：继续通过 `IModuleUiProvider.GetPages` 注册，宿主仍保留 `/ext/{moduleId}/{pageKey}` 作为兼容入口。

新模块默认按第一种或第二种方式设计。也就是说，模块负责能力、配置、运行态数据和保存接口，页面要么由宿主 Vue 承载，要么由模块自带静态 Vue 页承载。只有旧模块、简单页面或暂时没有 Vue 页面时，才继续使用 Razor 兼容页面。

如果模块没有被宿主 Vue 页面接管，不需要为了 Vue 迁移重写模块。宿主的通用 Vue 页面会用 iframe 加载旧模块页面：

```text
/ui/ext/{moduleId}/{pageKey}
  -> /ext/{moduleId}/{pageKey}?legacy=1&embed=1
```

如果模块已经有对应的 Vue 原生页面，就必须在模块里补齐管理端 API。否则 Vue 页面会请求不到接口，通常表现为 `404`，并回退到旧页面。

### 给 Vue 页面提供管理端 API

在模块入口的 `MapEndpoints` 中注册管理端接口，推荐统一放在：

```text
/api/panel/extensions/{module-slug}
```

示例：

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints, ModuleHostContext context)
{
    var group = endpoints.MapGroup("/api/panel/extensions/my-module");

    var configuration = endpoints.ServiceProvider.GetService<IConfiguration>();
    if (configuration?.GetValue<bool>("AdminAuth:Enabled") == true)
        group.RequireAuthorization();

    group.MapGet("", GetPageAsync);
    group.MapPost("/settings", SaveSettingsAsync);
}
```

约定：

- 这个前缀只用于后台管理接口，不要放匿名外链或公开 API。
- 返回 DTO，不要直接返回 EF 实体或内部运行态对象。
- Vue 页面需要的列表、设置、运行态快照，优先通过一个 `GET ""` 聚合返回，避免页面首次加载打很多请求。
- 写接口时要把“读取初始数据”和“保存配置”分清楚，避免页面刷新时触发耗时 Telegram 操作。
- 修改接口后必须递增 `manifest.json` 的 `version`，重新打包 `.tpm` 并更新生产模块包。
- 新接口上线前保留旧 Razor 页面，便于回退和排障。

### 导航与路由怎么写

模块仍然可以通过 `GetNavItems` 返回 `/ext/{moduleId}/settings`。如果这个链接来自 `GetNavItems`，Vue 菜单会按模块自带页面处理，点击后直接进入该 endpoint。

旧 Razor 页面不要只靠 `GetNavItems` 注册，应该通过 `GetPages()` 返回 `ModulePageDefinition`。宿主会把 `GetPages()` 注册的页面转换成 `/ui/ext/{moduleId}/{pageKey}` 兼容路由，并用 iframe 加载 `/ext/{moduleId}/{pageKey}?legacy=1&embed=1`。

```csharp
public IEnumerable<ModuleNavItem> GetNavItems(ModuleHostContext context)
{
    yield return new ModuleNavItem
    {
        Title = "模块设置",
        Href = "/ext/my-module/settings",
        Group = "扩展模块",
        Order = 100
    };
}
```

如果宿主已经为某个模块写了固定 Vue 页面，模块也可以不返回导航项，由宿主菜单直接提供入口。

## 开发/调试建议

模块开发最简单的闭环是：**打包 → 在面板中上传/安装 → 重启服务 → 验证**。

- 安装/启用/停用外部模块通常需要重启（因为 `ConfigureServices` 在宿主构建 DI 之前执行）。
- 开发阶段可以把版本号（`manifest.json` 的 `version`）按 `1.0.0 -> 1.0.1 -> ...` 递增，避免缓存/回滚机制干扰排查。

## 任务扩展（Task）

### 任务中心创建与编辑合同

任务定义本身可以继续用于历史任务展示、状态能力和重跑能力，但“新建任务”只展示宿主明确允许创建的定义。当前宿主会把 `canCreate` 下发给 Vue 管理端；没有 `CreateRoute` 且存在宿主验证通过的 `EditorComponentType` 的定义才会进入任务创建列表，内置模块和外部模块都适用。

仅有 `CreateRoute` 的常驻监听或配置模块不会出现在“新建任务”弹窗中。已有任务仍可在任务中心编辑；当没有宿主编辑器但定义声明了 `CreateRoute` 时，宿主会把 `taskId` 附加到该路由后打开模块页面。模块页面必须接受该参数，并按任务 ID 读取和保存对应配置。

模块开发必须验证：无效编辑器类型不会进入创建列表，路由-only 任务仍能在任务中心打开，创建列表不包含系统任务，且 `canCreate` 与实际页面能力一致。

Vue SPA 还会额外要求任务类型存在宿主内置的 `TaskConfigForm`；外部模块应使用 `CreateRoute` 提供自己的页面，不要假设 .NET 编辑器类型会自动下发到浏览器。

### 1) 声明任务类型与创建编辑器

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
            Icon = "task_alt",
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
        TaskType = "example_background_monitor",
        DisplayName = "示例后台监听",
        Description = "常驻后台监听，不占用批量任务队列；在配置里启用即可生效。",
        Icon = "notifications_active",
        CreateRoute = "/ext/example.monitor/settings",
        Order = 100
    };
}
```

这种模式的体验是：

- “新建任务”里点击后打开配置窗口（或跳转配置页）
- “任务中心”顶部可直接编辑该持续任务配置（方便增删频道/目标等）

### 示例：批量订阅/加群/启用 Bot（用户任务）

该类任务的典型形态是“多账号 × 多链接”的组合执行，并允许在 UI 中切换操作模式：

- `join`：订阅频道 / 加入群组 / 启用外部 Bot（发送 `/start`）
- `leave`：取消订阅 / 退群 / 停用外部 Bot（拉黑 Bot）

建议的 `host.Config`（JSON）结构：

```json
{
  "Mode": "join",
  "AccountIds": [1, 2],
  "Links": [
    "https://t.me/xxx",
    "t.me/+hash",
    "@username",
    "tg://join?invite=hash",
    "@examplebot",
    "https://t.me/examplebot?start=abc"
  ],
  "DelayMs": 2000,
  "TreatNoBotSuffixAsBot": false
}
```

模块执行器中可直接解析并调用宿主服务（示例）：

- `TelegramPanel.Core.Services.Telegram.AccountTelegramToolsService.JoinChatOrChannelAsync(...)`
- `TelegramPanel.Core.Services.Telegram.AccountTelegramToolsService.LeaveChatOrChannelAsync(...)`
- `TelegramPanel.Core.Services.Telegram.AccountTelegramToolsService.StartExternalBotAsync(...)`
- `TelegramPanel.Core.Services.Telegram.AccountTelegramToolsService.StopExternalBotAsync(...)`

这些宿主方法会对 `A task was canceled`、连接关闭、代理断开等瞬时连接错误执行一次客户端重建重试；调用方取消任务时仍会传播取消，不会被当作普通失败。

`user_chat_active` 账号持续活跃任务的目标字段也使用同一套目标解析边界：群组/频道链接按聊天目标解析，`@xxxbot`、`t.me/xxxbot?start=abc` 和 `tg://resolve?domain=xxxbot` 会解析为 Bot 私聊目标。自 v1.31.55 起，消息配置使用 `message_rules` 数组；每条规则由多行 `text` 和可选的单个 `image_dictionary_token` 组成，执行器按 `message_mode` 对整条规则随机或队列循环。规则可为纯文字、纯图片或图片加说明文字，内部换行必须原样保留。模块编辑器保存时同时维护旧版 `dictionary`，且仅在所有规则共享同一个非空图片字典时维护全局 `image_dictionary_token`；读取时若 `message_rules` 为空，则从这两个旧字段迁移。前置条件是目标和引用字典均可由宿主模板服务解析；成功判据是创建、重跑和实际执行使用同一套规则归一化结果。无效图片字典应在创建或启动阶段失败，不得静默降级。回滚到 v1.31.54 或更早版本前，应把配置收敛为纯文字或所有规则共享同一图片字典，否则旧版无法完整表达每条独立图片字典。图片字典只适用于群组/频道等支持媒体发送的目标；Bot 私聊保活建议使用文字规则。

自 v1.31.56 起，`user_chat_active` 增加发送动作合同：`message_action_mode=send_generated_text|forward_url`。默认 `send_generated_text` 保持原规则发送，并可通过 `reply_to_message_url` 或 `reply_to_message_id` 让文字/图片消息回复目标内的指定消息；链接只用于提取消息 ID，批量多目标时需确认各目标存在相同消息 ID。`forward_url` 会忽略 `message_rules`、`dictionary`、图片字典和 AI 验证，改用 `forward_source_urls` 作为来源消息链接列表，按 `message_mode` 随机或队列选择后调用 Telegram 原生转发；`forward_mode=with_attribution|hide_attribution` 控制是否保留原作者引用。`skip_if_last_message_from_self=true` 时，执行器会在每次发送或转发前读取目标最新普通消息，若该消息仍由当前执行账号发出，则把本轮记为已处理但不发送，用于避免同账号连续刷屏。前置条件是执行账号能访问来源消息和目标会话；成功判据是任务详情显示发送动作、来源数或回复消息，开启去重时同账号连续发言会跳过本轮，实际发送返回 Telegram 消息 ID。失败排查先看 `recent_failures.reason`，常见原因为来源链接无消息 ID、账号无权访问来源、目标无权发言、回复消息 ID 在目标中不存在，或开启去重后无法读取目标最新消息。回滚到 v1.31.55 或更早版本前，应把任务改回 `send_generated_text` 并关闭去重，否则旧版只会按空消息规则处理转发配置且不识别去重字段。

### 3) 使用 `CreateRoute` 提供自定义创建页

当前主后台是 Vue SPA。外部模块需要自定义表单时，应设置 `ModuleTaskDefinition.CreateRoute`，指向模块自带的静态 Vue 页或宿主 Vue 路由：

```csharp
yield return new ModuleTaskDefinition
{
    Category = "user",
    TaskType = "example.join-targets",
    DisplayName = "批量加入目标",
    CreateRoute = "/ext/example.join-targets/settings",
    Order = 100
};
```

该页面用于已有任务的配置和编辑。任务中心打开时会追加 `taskId`，页面通过模块管理接口读取对应任务，并在校验后保存配置。它不会因为声明了 `CreateRoute` 就自动出现在“新建任务”列表。

实用建议（针对“多账号/多目标”类任务）：

- 在页面里做基础校验，明确提示未选择账号、未填写链接等问题
- `Total` 建议按“账号数 × 链接数”或“账号数 × 用户名数”等可预估的总步数计算，便于任务中心展示进度
- 支持筛选：例如“账号分类筛选/搜索”，减少用户选择成本
- 遵循宿主的账号排除规则：默认不展示 `Category.ExcludeFromOperations=true` 的账号（常用于“工作账号”）；如你的模块确实需要，也可以提供“包含工作账号”的开关

没有专用创建编辑器的任务不会被宿主标记为 `canCreate`，也不会出现在“新建任务”列表。`EditorComponentType` 仅用于宿主内置任务的合法创建/编辑器；`EditComponentType` 保留给旧 Razor 兼容流程。

### 4) 任务中心能力声明（建议按新约定填写）

`ModuleTaskDefinition` 现在带有 `TaskCenter` 字段，可用于声明该任务在任务中心里希望暴露哪些操作能力：

```csharp
yield return new ModuleTaskDefinition
{
    Category = "user",
    TaskType = "example.long-running",
    DisplayName = "示例：持续任务",
    Icon = "tune",
    CreateRoute = "/ext/example.long-running/settings",
    TaskCenter = new ModuleTaskCenterCapabilities
    {
        CanPause = true,
        CanResume = true,
        CanEdit = true,
        CanRerun = true,
        AutoPauseBeforeEdit = true
    }
};
```

字段说明：

- `CanPause`：任务支持暂停
- `CanResume`：任务支持从暂停状态继续运行
- `CanEdit`：任务支持在任务中心修改 `Total` 与 `Config`；外部任务默认使用通用 JSON 表单
- `CanRerun`：任务支持基于历史配置重新创建一个新任务
- `AutoPauseBeforeEdit`：如果任务仍在运行，宿主可先暂停再进入编辑

当前建议：

- 对“一次性批量任务”，通常只需要 `CanRerun = true`
- 对“持续任务/常驻任务”，通常建议同时声明 `CanPause / CanResume / CanEdit / CanRerun`
- 自定义创建页面使用 `CreateRoute`；不要依赖旧 Razor 的 `EditorComponentType` / `EditComponentType`

> 注意：这组字段已经进入抽象层，并且内置持续任务已按此方式声明；外部模块也建议遵循相同结构，便于后续宿主统一扩展任务中心行为。

### 5) 宿主内置数据字典与模板变量（推荐优先复用）

如果你的模块任务需要“随机文案 / 队列文案 / 图片变量 / 标题模板 / 用户名模板”等能力，建议优先复用宿主已经内置的数据字典体系，而不是在模块里重复造一套词库配置。

当前宿主已经提供：

- 数据字典管理页面：`/data-dictionaries`
- 文本字典：返回 `string`
- 图片字典：返回图片资产引用（适合头像、图片消息等）
- 读取模式：`random` / `queue`
- 队列游标持久化：`queue` 模式的 `NextIndex` 会写入数据库，重启后继续
- 模板变量语法：固定为 `{name}`
- 内置变量：`{time}`（格式 `yyyyMMddHHmmss`）

相关宿主服务：

- `TelegramPanel.Web.Services.DataDictionaryService`
- `TelegramPanel.Web.Services.TemplateRenderingService`
- `TelegramPanel.Web.Services.ImageAssetStorageService`

推荐用法：

```csharp
var templateRendering = host.Services.GetRequiredService<TemplateRenderingService>();

var title = await templateRendering.RenderTextTemplateAsync("临时频道{time}_{city}", cancellationToken);
var avatar = await templateRendering.ResolveImageTemplateAsync("{avatar_dict}", cancellationToken);
```

约束说明：

- 标题、描述、公开用户名这类文本字段，只能解析到**文本值**
- 头像、图片消息这类图片字段，只能使用**固定图片**或**图片字典变量**
- 文本字典和图片字典**严格分型**，不要混用
- 未知变量、空字典、已停用字典、类型不匹配，宿主会直接抛出校验失败
- 图片变量必须是**单个 token**，例如 `{avatar}`，不能写成 `头像_{avatar}`

如果你的模块也提供任务编辑器，建议：

- 在 UI 中直接提示“支持 `{time}` 与 `{字典名}`”
- 文本输入框只展示文本字典变量
- 图片输入框只展示图片字典变量
- 让最终配置 JSON 只保存模板字符串 / 字典 token，不要把解析后的随机结果提前固化进配置

这样做的好处是：

- 宿主统一管理字典内容，模块间可以复用同一份变量源
- 后续扩展新变量 provider 时，模块通常不需要改协议
- 计划任务、一次性任务、模块页面都能复用同一套解析规则

### 6) 为“重新运行”提供专用构建器（适合复杂任务）

如果你的任务配置在运行过程中会写回运行态字段，或者重跑前需要清洗旧配置，建议额外实现 `IModuleTaskRerunBuilder`：

```csharp
public sealed class MyTaskRerunBuilder : IModuleTaskRerunBuilder
{
    public string TaskType => "example.long-running";

    public ModuleTaskCreateRequest Build(ModuleTaskSnapshot task)
    {
        // 这里把历史任务快照重新整理为新的创建请求
        return new ModuleTaskCreateRequest
        {
            TaskType = TaskType,
            Total = Math.Max(0, task.Total),
            Config = task.Config
        };
    }
}

public void ConfigureServices(IServiceCollection services, ModuleHostContext context)
{
    services.AddSingleton<IModuleTaskRerunBuilder, MyTaskRerunBuilder>();
}
```

这种方式适合：

- 运行中会把“最近失败/暂停标记/错误信息”等运行态字段写回 `Config`
- 重跑前需要把旧配置从“运行态 JSON”还原为“创建态 JSON”
- 需要在重跑时动态修正 `Total`

> `IModuleTaskRerunBuilder` 已进入抽象层，宿主任务页面会按 `TaskType` 查找已注册的构建器。

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

API 配置页只负责保存通用 `Config` JSON；具体字段、校验和执行逻辑由模块自己定义。

## UI 扩展（Vue 后台与旧页面兼容）

> 后台已经是 Vue SPA。新模块优先提供管理端 API，由宿主 Vue 页面承载。旧 Razor 页面仍然支持，但只作为兼容方案；如果该模块已有宿主 Vue 原生页，必须同步提供 `/api/panel/extensions/{slug}` 管理接口。完整约定见上面的“Vue 后台迁移后的模块页面约定”。

### 1) 添加导航链接（可选）

实现 `IModuleUiProvider.GetNavItems` 返回 `ModuleNavItem`（Title/Href/Icon/Group/Order）。

导航可以继续写 `/ext/{moduleId}/{pageKey}`，宿主会在 Vue 后台里转换成兼容路由。模块里不要硬编码 `/ui`。

### 2) 提供 Vue 管理接口（新模块推荐）

新模块如果需要管理界面，推荐先提供管理端 API：

```text
/api/panel/extensions/{module-slug}
```

然后由宿主 Vue 页面读取这些接口。这样页面刷新、侧栏切换、弹窗编辑都不依赖 Blazor Server 连接，也更容易保持和主后台一致的 UI。

### 3) 添加旧 Razor 模块页面（兼容）

实现 `IModuleUiProvider.GetPages` 返回 `ModulePageDefinition`：

- `Key`：页面键（模块内唯一）
- `ComponentType`：组件类型 `AssemblyQualifiedName`

宿主提供统一入口路由：`/ext/{moduleId}/{pageKey}`，会动态加载并渲染模块组件。

### 4) 模块页面参数约定（非常重要）

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
- 如果模块需要引用宿主工程里的类型，编译时可按需 `ProjectReference` 到
  `TelegramPanel.Modules.Abstractions`、`TelegramPanel.Core` 或 `TelegramPanel.Data`。
  `TelegramPanel.*` 是宿主共享边界程序集，不要手工复制进模块 `lib/`；默认轻量打包会将其剔除。

## 认证/授权（端点安全）

- **模块页面**：作为面板的一部分渲染，通常受宿主的后台登录控制（管理员登录开启时会要求授权）。
- **Vue 管理接口**（`/api/panel/extensions/{slug}`）：属于后台管理接口，通常应跟随宿主后台登录鉴权。
- **模块 API 端点**（`MapEndpoints`）：请显式选择：
  - `AllowAnonymous()`：公开接口（务必自行做好鉴权/限流/防泄露）
  - 或 `RequireAuthorization()`：跟随宿主后台登录鉴权

如果是“外置链接/匿名链接”类能力，建议：

- 不要放在 `/ext/...` 后台模块页面，也不要放在 `/api/panel/extensions/...` 管理接口下面
- 使用随机 token 作为访问凭证
- 设置过期时间，并按账号/客户隔离可见范围
- 做好限流（按 token + IP）
- 返回 `no-store` 防缓存

## 运行时行为（启用/回滚）

- 启用模块会进行宿主版本校验与依赖校验（依赖模块必须存在且版本满足范围）。
- 启动时加载模块：
  - 加载失败会尝试回滚到 `LastGoodVersion`；
  - 回滚也失败则自动 `Enabled=false`（避免拖垮系统）。

## 安全与稳定提示

### 账号同步任务的取消边界

账号同步任务的执行器会区分“整个任务被暂停/停机”和“单个 Telegram 请求临时取消”。任务处理器不得把未触发任务取消令牌的 `OperationCanceledException` 当作 Session 失效；这类异常应保留账号当前状态，仅记录本轮账号失败，等待后续重试。只有明确的 Telegram Session 错误或账号权限错误才允许更新账号状态。

### 账号状态与瞬时连接恢复边界

适用于 v1.31.46 及以上宿主。账号状态刷新和任务准备阶段的只读 Telegram 操作必须把“获取客户端
与发起请求”作为一个重试边界：遇到非调用方触发的取消、IO、Socket 或连接关闭时，先从
`ITelegramClientPool` 删除旧客户端，再通过宿主重新获取客户端并解析账号当前代理，最多重试一次。
不得在删除后继续复用局部变量中的旧 `Client`。调用方取消、RPC、权限、限流和 Session 错误不得重试。
重试边界内的 Telegram 读取必须使用宿主请求超时和取消令牌，不能在无界 `await` 后才检查取消。

临时连接异常只能记录为可复查状态，不能映射成 Session 永久失效，也不能进入废号删除判定。
新增账号状态或清理入口时，必须分别覆盖“首次瞬时失败后恢复”“二次失败后保留账号”“调用方取消”
和“明确 Session 错误不重试”四类测试。该合同不改变模块 ABI；回滚到 v1.31.45 无需迁移模块配置。

同进程插件无法做到“绝对不崩”。为了降低风险：

- 只安装可信来源的模块包
- 出现异常时先停用模块并重启
- 建议在生产环境使用“灰度/备份”方式试装模块

后续如需更强隔离，可以把模块改为“独立进程 Module Host”模式（主站通过 HTTP/gRPC 调用），进一步降低崩溃风险。






