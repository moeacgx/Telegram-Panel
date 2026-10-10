# 配置与数据目录

## 在线模块仓库数据

包含在线仓库功能的版本在模块根目录（`Modules:RootPath`，Docker 通常为 `/data/modules`）
保存 `repositories.json`。字段包括仓库 ID、名称、类型、地址、分支和加密令牌；该文件不是模块 `state.json`。
初次没有配置文件时提供官方源；保存或移除来源后按配置文件读取，不会自动恢复已移除的来源。

仓库令牌依赖持久化的 DataProtection 密钥，必须与仓库配置一起备份；密钥丢失时重新输入令牌。
不需要数据库迁移或新增环境变量。回滚旧宿主时可保留该文件，旧版本不会读取它。
检查面板能列出来源并成功连接目录作为配置验收；故障处理、网络约束和备份边界见
[在线模块仓库维护](../developer/module-repositories.md)。

## 技术栈

- .NET 8 / ASP.NET Core Minimal API
- Vue 3 / Element Plus（主后台）
- Razor / MudBlazor（旧模块页面兼容）
- EF Core（默认 SQLite）
- WTelegramClient（MTProto）

## Docker 数据目录（强相关）

`docker-compose.yml` 会把宿主机 `./docker-data` 挂载到容器 `/data`，核心文件包括：

- `/data/telegram-panel.db`：SQLite 数据库
- `/data/sessions/`：账号 session 文件
- `/data/appsettings.local.json`：UI 保存后的本地覆盖配置
- `/data/admin_auth.json`：后台登录账号/密码（首次会用初始默认值生成）
- `/data/uploads/`：图片资产（数据字典图片、头像素材等）

## 后台任务（刷新页面不影响）

部分批量任务会在后台静默执行（避免“刷新页面就中断”）：

- 批量邀请
- 批量设置管理员

## 账号状态检测（深度探测）

为更可靠识别冻结/受限等状态，支持深度探测（例如通过创建/删除测试频道来探测权限）。

检测结果会持久化到数据库，避免刷新页面又变回“未检测”。

## 清理废号（封禁/受限/未登录/session 失效）

在「账号列表」与「外置验证码链接」页面支持“清理废号”（多选批量）：

- 会先执行 Telegram 状态检测（可选普通/深度）
- 仅当判定为废号（封禁/受限/被冻结/需要 2FA/Session 失效或损坏）才会删除
- `连接失败`、`请求超时`、`刷新失败`、`创建频道探测失败` 和 `无法获取账号资料` 属于不确定状态，不进入“只看废号”结果，也绝不会触发删除
- 删除范围：数据库记录 + `*.session`（含常见备份/同名 json）
- 若遇到 `*.session` 文件被占用，会先尝试从 `TelegramClientPool` 释放客户端并重试删除

另外，系统「账号列表」支持“一键清理所有废号”（扫描系统全部账号）。

## 配置项速查

Docker 下常用环境变量（见 `docker-compose.yml`）：

- `ConnectionStrings__DefaultConnection`：SQLite 路径（默认 `/data/telegram-panel.db`）
- `Telegram__SessionsPath`：session 目录（默认 `/data/sessions`）
- `Telegram__Proxy__Enabled`：显式启用或关闭 Telegram 全局代理
- `Telegram__Proxy__SourceMode`：`manual`（手动地址）或 `existing`（引用代理表中的代理）
- `Telegram__Proxy__ProxyId`：`SourceMode=existing` 时引用的代理 ID
- `Telegram__Proxy__Protocol`：全局代理协议，支持 `http`、`socks5`、`mtproto`
- `Telegram__Proxy__Server` / `Telegram__Proxy__Port`：Telegram 全局代理地址和端口
- `Telegram__Proxy__Username` / `Telegram__Proxy__Password`：SOCKS5 代理认证（可选）
- `Telegram__Proxy__Secret`：MTProxy Secret（仅 `mtproto` 使用）
- `Proxy__Egress__ProbeUrl`：普通代理、外部 WireGuard WARP 和 Resin 后台巡检使用的轻量探针 URL，默认 `https://208.67.222.222/`
- `Proxy__Egress__MetadataUrl`：手动检测面板或代理出口元数据时使用的 URL，默认 `https://cloudflare.com/cdn-cgi/trace`
- `Proxy__Egress__Maintenance__Enabled`：v1.31.44 起启用普通代理、外部 WireGuard WARP 和 Resin 出口健康巡检，默认开启
- `Proxy__Egress__Maintenance__InitialDelaySeconds`：服务启动后首次巡检延迟，默认 30 秒
- `Proxy__Egress__Maintenance__IntervalMinutes`：普通代理、外部 WireGuard WARP 和 Resin 巡检周期，默认 5 分钟
- `AdminAuth__CredentialsPath`：后台密码文件（默认 `/data/admin_auth.json`）
- `Sync__AutoSyncEnabled`：账号创建的频道/群组自动同步（默认关闭）
- `Telegram__BotAutoSyncEnabled`：Bot 频道自动同步（默认关闭）
- `Telegram__WebhookEnabled`：Bot Webhook 模式开关（默认关闭，使用长轮询）
- `Telegram__WebhookBaseUrl`：Webhook 公网 HTTPS 地址
- `Telegram__WebhookSecretToken`：Webhook 验证密钥
- `Telegram__MaxRetries`：批量 Telegram 操作的最大自动重试次数，`0` 表示关闭，范围 `1-5`。
- `BucketBackup__Enabled`：启用存储桶在线备份，默认关闭
- `BucketBackup__UploadUrl`：备份 ZIP 上传 URL，支持 `{date}`、`{timestamp}`、`{version}` 占位符，可填写 S3/R2/OSS/COS 预签名 URL
- `BucketBackup__Method`：上传 HTTP 方法，支持 `PUT`（默认）或 `POST`
- `BucketBackup__AuthorizationHeader`：可选 Authorization 请求头；敏感值不会在 UI 回显
- `BucketBackup__TimeoutSeconds`：上传超时，范围 30-1800 秒，默认 300

### Telegram API 配置池

`Telegram:OfficialApiEnabled` 控制内置 Telegram 官方 Android API（ApiId `6`）是否参与 API 池，默认 `true`。`Telegram:ApiId` / `Telegram:ApiHash` 只保留为兼容旧版本的单 API 字段；在新版系统设置保存时会被带入 `Telegram:ApiProfiles` 并清空旧字段。需要自建 API、隔离额度或分散新账号时，在「系统设置」里的 Telegram API 区块添加多个启用项，或手工配置：

```json
{
  "Telegram": {
    "OfficialApiEnabled": true,
    "ApiId": 0,
    "ApiHash": "",
    "ApiProfiles": [
      { "Name": "api-a", "ApiId": 123456, "ApiHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Enabled": true, "Weight": 1, "Notes": "备用配置" },
      { "Name": "api-b", "ApiId": 234567, "ApiHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Enabled": true, "Weight": 1 }
    ]
  }
}
```

新手机号登录、二维码登录、Session 文件导入、StringSession 导入和纯 TData 导入会在启用池子中按顺序轮询；`Weight` 越大，该配置在轮询序列里出现次数越多。禁用项不会被分配。Telethon Zip 内自带 `api_id/api_hash` 的账号继续使用包内配置。已有账号操作优先使用账号表中保存的 `ApiId/ApiHash`；保存配置池不会迁移或改写已有账号。

如果关闭内置官方 API 且没有任何启用的自定义配置，新账号登录和不带 API 的导入会被视为 Telegram API 不可用。保存 Telegram API 设置会清理客户端缓存；正在使用旧 Session 的账号不会被批量改写，如需切换 API 请重新登录或重新导入对应账号。
### Telegram 设备指纹画像

适用版本：包含 `20260818090000_AddAccountDeviceProfileKey` 迁移的版本。通过侧栏「设备指纹」管理默认画像；面板内置四个可选画像：`android-default`、`ios-default`、`macos-default`、`windows-default`；也可在 `Telegram:DeviceProfiles` 中按同样字段覆盖或增加画像。

```json
{
  "Telegram": {
    "DefaultDeviceProfileKey": "android-default",
    "DeviceProfiles": [
      {
        "Key": "android-default",
        "Name": "Android 默认指纹",
        "Family": "android",
        "AppVersion": "12.7.3",
        "DeviceModel": "Samsung SM-G991B",
        "SystemVersion": "Android 14",
        "SystemLangCode": "en-US",
        "LangCode": "en",
        "Enabled": true
      }
    ]
  }
}
```

设备指纹页可保存默认画像；手动登录页、导入页和账号详情页可为单次登录/导入或单个账号选择画像，留空表示跟随系统默认。新手机号登录、二维码登录、Session/StringSession/TData 导入会把当时选定的 key 保存到账号；已有账号继续使用其已保存的 key。画像只控制 Telegram 客户端的 `app_version`、`device_model`、`system_version`、语言字段，不改变 API ID/Hash 或代理出口。

成功判据：刷新设备指纹页仍显示默认画像；手动登录和导入账号后账号详情显示所选画像；账号详情保存后重新打开仍显示所选画像；下一次客户端创建日志/Telegram 授权显示对应设备字段。失败时检查 key 是否启用、`appsettings.local.json` 权限和 `DeviceProfileKey` 数据库列；不要手工删除账号 Session。回滚到不含该迁移的版本前，先把账号画像改回默认并备份数据库；旧版会忽略该列，但需要按项目启动迁移兼容策略执行。

### 批量操作间隔与并发

`Telegram:DefaultDelayMs` 是系统设置页“默认操作间隔（毫秒）”的持久化字段，允许 `1000-60000`。
该默认值用于没有单独传入间隔的批量操作；`user_join_subscribe` 任务和即时加群/订阅/Bot 操作的
单次 `DelayMs/delayMs` 允许 `0-60000`。成功判据是保存 `60000` 后刷新设置页仍显示该值，任务详情
里的 `delayMs` 与提交值一致；失败时检查接口返回的范围错误。回滚到旧版前应把该值调回 `10000` 以内，
避免旧版 UI 或服务端校验拒绝保存。

`BatchTasks:MaxConcurrent` 是系统设置页“最大并发任务数”的持久化字段，允许 `1-10`。
后台任务执行器会在每轮轮询前重新读取该值；从后台保存后，新增并发槽会在下一次轮询时启动等待中的任务，
无需重启面板。成功判据是先以 `1` 运行一个长任务，再保存为 `2` 后，另一个 `pending` 任务会自动变为
`running`；失败时先确认设置接口返回成功、`appsettings.local.json` 已写入该值，并检查后台日志中的
`Batch task runner started` 与后续任务启动记录。回滚到旧版前无需迁移数据，但旧版执行器可能只在启动时读取
并发值，调整后需要重启面板才会生效。

### 模块常驻任务执行器

`PersistentModuleTasks:Enabled` 默认 `true`；`PollIntervalSeconds` 默认 `2`，允许 `1-30`；`MaxConcurrent` 默认 `4`，允许 `1-32`。这些任务使用独立并发池，不占用 `BatchTasks:MaxConcurrent`。当前设置页尚不编辑这三个值，需要在本地配置或部署覆盖中设置。

成功判据是启动日志出现 `Persistent module task runner started`，任务行 `ExecutionKind=persistent` 后由常驻通道领取，普通批任务仍能并行运行。任务一直 `pending` 时检查模块是否启用、`OwnerModuleId` 是否匹配、是否恰好注册一个 `IModulePersistentTaskHandler` 和生命周期处理器。回滚前先暂停并删除常驻任务；旧版会忽略配置键，但不会执行这些任务。

### 群聊活跃任务发送分配

“用户群聊活跃”任务在准备阶段会先过滤停用账号、异常账号、排除批量操作的分类，以及无法解析目标的账号-目标组合。
当 `MaxMessages` 大于 `0` 时，本次运行的有效发送条数会按准备后仍可用的执行账号数封顶；例如请求发送 10 条但最终只有 1 个账号可用时，只会计划 1 条，不会让该账号轮回发送剩余 9 条。账号足够时按账号一对一分配，每个账号在同一次有限运行中最多发送 1 条；发送失败自动重试仍只针对当前账号和当前消息生效，不会改变该分配。

自 v1.31.56 起，任务配置可开启 `skip_if_last_message_from_self` 去重发送。执行器会在每次发送或转发前读取目标最新普通消息；如果该消息仍由当前执行账号发出，本轮不再发送但会计入已处理数量，避免有限任务反复占用同一轮次。该检查依赖执行账号对目标历史消息的读取权限；读取失败时本轮会作为失败记录写入 `recent_failures.reason`，便于排查权限、代理或目标访问问题。

### 群聊活跃任务失败重试（v1.31.42 及以上）

在 **系统设置 → 批量操作设置** 开启“失败自动重试”并设置最大重试次数后，
“用户群聊活跃”任务会进行有限次重试，只有全部尝试失败才把当前消息记为最终失败。
适用范围包括连接取消或超时，以及 `CHANNEL_INVALID`、`PEER_ID_INVALID`、
`CHAT_ID_INVALID` 等失效 peer；重试前会重建异常连接并重新解析目标，退避时间依次为
1-5 秒。

权限不足、Session 失效、账号受限、词典内容错误和 `FLOOD_WAIT` 等永久或风控错误不会
自动重试，避免重复发送和扩大限流。重试成功时本轮按成功计数；全部失败时只计一次失败，
最近失败详情会注明已重试次数。成功判据是任务日志出现 `send recovered after retry`，且任务
失败数不因中间尝试增加。若仍失败，检查账号是否已加入目标、是否具备发言权限及代理连接；
需要回滚时关闭“失败自动重试”或将 `Telegram:MaxRetries` 设为 `0`，无需迁移数据库。
重试发生在客户端未收到成功确认时；若 Telegram 已接收消息但响应恰好中断，极端情况下
可能出现重复消息，对重复敏感的任务应关闭自动重试。

### 在线设备连接恢复（v1.31.43 及以上）

账号列表的“在线设备”读取复用 `Telegram:RequestTimeoutSeconds`，无需新增配置。账号使用动态代理时，
若缓存客户端遇到超时、连接关闭或代理断开，面板会释放旧客户端、按账号当前路由重新解析代理并
重试一次。该流程只使用已有直连、全局代理、普通代理、外部 WireGuard WARP、Resin 或 WARP
绑定，不会为账号创建新的 WARP 容器。调用方主动取消、Telegram 限流、权限错误和 Session 错误不会自动重试。

验收时先确认账号代理出口检测成功，再连续打开两次在线设备；接口应返回 `200`，故障恢复日志对
单次请求至多出现一次。最终返回 `502` 时，先检测动态代理是否能建立 TCP 连接并检查 Session；
无需通过切换代理来清缓存。回滚到 v1.31.42 即恢复旧行为，无数据库或配置迁移。

### 状态刷新与任务准备连接恢复（v1.31.46 及以上）

前置条件是账号 Session 有效，且账号当前选择的直连、全局代理或已有代理路由可用。账号状态
刷新和群聊活跃任务解析目标时，如果缓存客户端因动态代理出口变化、连接关闭、IO 错误或非调用方
触发的请求取消而失败，面板会释放该账号的旧客户端，重新读取当前代理配置并重试一次。该恢复
不会创建代理或 WARP，也不会改变账号绑定。

调用方主动取消、Telegram RPC 限流、权限、风控和 Session 错误不会自动重试。账号列表会把
`连接失败`、`请求超时` 和 `刷新失败` 显示为“连接异常”；创建频道探测失败和无法获取账号资料
显示为“检测异常”，均与明确的账号失效分开。后台仍按账号状态刷新设置小批量复查这些不确定状态。

验收时可让动态代理出口自然轮换，再刷新账号状态并运行群聊活跃定时任务。成功判据是无需重新
应用同一代理即可恢复，日志中每次操作至多出现一次客户端重建，且“只看废号”和清理结果不包含
临时连接异常。若第二次仍失败，先检测账号代理出口，再核对代理有效期、认证和 Session；不要反复
提高重试次数。回滚到 v1.31.45 即恢复旧行为，无数据库、Session 或配置迁移。

### Docker 更新来源

- `TP_UPDATE_MODE`：容器启动时的程序来源。`auto`（默认）按版本选择镜像或 `/data/app-current`；`image` 固定使用镜像 `/app`；`binary` 固定优先使用已确认的 `/data/app-current`。
- `TP_IMAGE`：Docker Compose 使用的镜像标签，例如 `ghcr.io/moeacgx/telegram-panel:dev-latest` 或 `latest`。

`auto` 需要镜像内存在 `/app/version.txt` 才能比较版本。若旧的一键更新目录缺少
`version.txt`，会将其视为未知旧版本，归档到 `/data/app-obsolete-*` 并使用有版本号的镜像。
修改 `TP_UPDATE_MODE` 或 `TP_IMAGE` 后，需要执行 `docker compose up -d --force-recreate`。


### 存储桶在线备份

系统设置里的“存储桶备份”会把当前 SQLite 数据库、WAL/SHM、`appsettings.local.json`、`admin_auth.json` 和 `sessions/` 打成 ZIP，然后上传到配置的 URL。适用于 S3、Cloudflare R2、阿里云 OSS、腾讯云 COS 等支持预签名 URL 或自定义 Authorization Header 的对象存储。

推荐使用预签名 `PUT` URL，并在 URL 中加入 `{timestamp}` 生成唯一对象名，例如：

```text
https://bucket.example.com/telegram-panel/tp-{timestamp}.zip?X-Amz-Signature=...
```

前置条件是对象存储 URL 在面板容器内可访问，且签名允许对应 HTTP 方法上传 `application/zip`。Cloudflare R2 预签名 `PUT` URL 使用 AWS Signature V4 时，面板会在请求中自动补充 `x-amz-content-sha256: UNSIGNED-PAYLOAD`；如果生成 URL 时已经把 `X-Amz-Content-Sha256` 写入查询参数，面板不会覆盖该签名约束。成功判据是系统设置点击“立即备份”返回成功，存储桶出现 ZIP，解压后能看到 `telegram-panel.db` 和 `sessions/`。失败时先检查 URL 是否过期、方法是否匹配、容器 DNS/网络是否可达，以及 Authorization Header 是否需要清空后重填。回滚方式是关闭 `BucketBackup:Enabled` 或清空上传 URL；已上传对象需要在存储桶侧按生命周期或人工删除。

当前后台面板不提供“导入备份恢复”入口，也不支持运行中覆盖数据。需要恢复时，先下载备份 ZIP，停机替换持久化数据目录，再重启验收；步骤见[从存储桶备份恢复](../getting-started/update.md#bucket-backup-restore)。

备份包包含账号 Session 和后台凭据，必须限制对象存储访问权限，不要把备份桶公开读。

## UI 保存到本地覆盖配置

面板里的部分“保存”按钮会把设置写入 `appsettings.local.json`（Docker 下为 `/data/appsettings.local.json`），常见键：

- `Telegram:BotAutoSyncEnabled` / `Telegram:BotAutoSyncIntervalSeconds`：Bot 频道后台自动同步轮询开关/间隔
- `ChannelAdminDefaults:Rights`：批量设置管理员的“默认权限”
- `ChannelAdminPresets:Presets`：批量设置管理员的“用户名列表预设”（名称 -> usernames）
- `ChannelInvitePresets:Presets`：批量邀请成员的“用户名列表预设”（名称 -> usernames）

## 账号代理优先于全局代理

代理管理中的 HTTP、SOCKS5、MTProxy、外部 WireGuard WARP 和 Resin 可以绑定到单个或多个账号。
内置轻量 WARP 每个出口只能绑定一个账号，不可用作全局代理；v1.31.82 已移除旧容器 WARP 路由。
账号的 Telegram 客户端、后台任务和模块操作都会复用这条账号路由。完整操作说明见
[代理管理与账号出口](../guides/proxy-management.md)。

路由优先级由账号明确选择决定：

- **已有代理**：使用账号绑定的代理。
- **全局设置**：继承下面的 `Telegram:Proxy`。
- **直连**：明确绕过账号代理和全局代理。

## 配置 Telegram 全局代理

推荐在后台 **代理管理 → 全局代理** 中配置。支持 HTTP、SOCKS5 和 MTProxy；
保存后会立即重载 `appsettings.local.json` 并清理 Telegram 客户端缓存，无需重启。

也可以手工配置，默认继承“全局设置”的账号会使用该代理：

```json
{
  "Telegram": {
    "Proxy": {
      "Enabled": true,
      "Protocol": "socks5",
      "Server": "127.0.0.1",
      "Port": 40000,
      "Username": "",
      "Password": "",
      "Secret": ""
    }
  }
}
```

- `Protocol` 可填写 `http`、`socks5` 或 `mtproto`；旧配置未填写时会按 `Secret` 兼容推断。
- HTTP / SOCKS5 按需填写 `Username`、`Password`。
- MTProxy 填写 `Secret`，不需要用户名和密码。
- `Enabled=false` 会显式关闭全局代理，即使环境变量仍保留旧地址也不会重新启用。
- `SourceMode=existing` 时必须同时设置有效的 `ProxyId`；代理停用或删除后会闭锁连接，
  不会静默回退为面板直连。后台代理管理页会自动写入这两个字段。
- 后台停用时会保留已保存的连接参数；凭据不会回显，编辑留空表示保持原值。
- 账号管理中的“已有代理”优先于全局设置；“直连”会明确绕过全局代理；“全局设置”可恢复继承该配置。升级前已有账号默认继续继承全局设置。
- Docker 部署的配置文件位于宿主机 `docker-data/appsettings.local.json`。容器内的 `127.0.0.1` 指向容器自身；访问宿主机代理时应使用容器可访问的宿主机地址（Docker Desktop 通常可用 `host.docker.internal`），并确保代理监听地址和防火墙允许容器连接。
- 手工编辑配置文件后应重启主程序；从后台保存时会自动重载并释放缓存客户端。

## 代理出口巡检探针

后台普通代理、外部 WireGuard WARP 和 Resin 巡检使用 `Proxy:Egress:ProbeUrl`，Docker 默认值为
`TP_PROXY_EGRESS_PROBE_URL=https://208.67.222.222/`。该请求只用于确认出站 HTTP/SOCKS 链路仍可用，
不会调用 Cloudflare Trace，也不会刷新出口 IP、地理位置或 WARP 状态。

手动“检测面板出口/检测代理出口”仍使用 `Proxy:Egress:MetadataUrl`，默认
`https://cloudflare.com/cdn-cgi/trace`，用于读取公网 IP、国家码和 `warp=` 状态。成功标准是后台
巡检每 5 分钟只访问 ProbeUrl，而手动检测仍能刷新出口元数据；失败时先检查 ProbeUrl 是否可由面板
容器访问、代理认证是否有效、Resin 控制面是否健康。回滚可设置
`Proxy__Egress__Maintenance__Enabled=false` 停止后台巡检，或把 `Proxy__Egress__ProbeUrl` 改回默认值。

## 外部 WireGuard WARP 端点

可选独立工具 `tools/wgcf-warp` 由共享 userspace wireproxy 容器管理多份独立配置，通过既有
`wireguard_warp` API 接入。它不改变面板主容器配置；内置轻量运行器则随 Linux 镜像提供。
完整前置、条款选择、恢复和回滚见
[共享 WireGuard 运维工具](../deployment/wgcf-wireproxy.md)。

- `TP_WGCF_NETWORK`：工具加入的现有 Docker 网络，默认 `telegram-panel_default`。
- `TP_WGCF_PANEL_CREDENTIALS`：仅叠加 `compose.panel.yml` 时需要，指向仓库外管理员
  凭据 JSON；只读挂载到 `/run/secrets/panel-auth.json`，不会复制到档案卷。
- 档案卷：每 profile 保存 `wgcf-account.toml`、生成的配置、`proxy-auth.json`、
  `meta.json` 和 `runtime.json`；包含密钥，必须按私有凭据备份，不加入 Git。
- `meta.json` 记录注册尝试、期望启停、监听端口、原代理 ID 和恢复标识；`runtime.json`
  只记录进程/监听状态和时间。`status` 不报告实网连通性，出口仍以面板检测为准。

工具默认资源限制为共享 512 MiB 内存、1 CPU、256 PIDs；没有配置数量与账号容量的换算保证。
失败排查先看运行器状态和面板检测，注册结果不明确时停止自动重试；回滚保留卷并恢复账号路由。

外部 WireGuard WARP 不需要新增环境变量。运营方在面板外负责 WARP/WireGuard 注册、
`wg` 接口、路由和 HTTP/SOCKS 监听，面板只保存该监听的代理记录。代理类型保存为
`wireguard_warp`，协议只能是 `http` 或 `socks5`；批量导入可使用
`wg-warp+socks5://user:pass@host:1080` 或 `wg-warp+http://host:8080` 模板。

前置条件：监听地址必须能被面板进程或容器访问，且经该监听访问 Cloudflare Trace 时返回
`warp=on` 或 `warp=plus`。成功标准：代理检测为“可用”、保存公网出口 IP，并且账号绑定或
全局已有代理解析不再报“外部 WireGuard WARP 端点尚未检测成功”。排障时先确认容器到监听
地址的 TCP 连通性，再在外部代理侧检查 WireGuard 路由和 WARP 注册状态。

回滚不需要配置迁移：把账号或全局代理切换到其它已有代理、全局手动配置或直连，再删除
对应 `wireguard_warp` 代理记录。面板不会停止外部 WireGuard/gost/3proxy 进程；这些进程
需要由运营方按原部署方式回滚。

## 轻量 WARP 配置与旧配置退役（v1.31.82）

从 v1.31.81 起，所有新建 WARP 使用 Linux 主容器内置 wgcf＋wireproxy，固定 SOCKS5，
无需开启 `Proxy:Warp:Enabled` 或挂载 Docker Socket。v1.31.82 已删除旧容器管理器、巡检和
刷新接口，旧 `Proxy:Warp:*` / `Proxy__Warp__*` / `TP_WARP_*` 配置不再生效，应从部署环境
移除。轻量档案固定最多 100 份，保存于持久根
`wgcf-warp/`，每份一个进程，不代表已经验证的账号容量。依赖不可用时必须更新并重新创建
Docker 镜像，参见 [部署与验收](../deployment/wgcf-wireproxy.md)。

部署仅使用主 `docker-compose.yml`，不再叠加旧 WARP Compose 文件或 Docker Socket 挂载。
旧 `kind=warp` 记录不会被自动删除，但无法用于账号或全局路由；残留引用将明确报错，
不回退直连。升级前先备份，再逐账号迁往新轻量出口并验证 Telegram 只读连接，全部成功后
才清理旧全局引用和容器资源。

成功判据：代理管理只显示一张统一列表，创建中与失败的轻量档案仍可见；就绪出口显示
检测结果和代理编号，单账号绑定在重启后保持；面板容器不再挂载 Docker Socket。
排障时先检查实际镜像版本、内置运行器依赖、持久目录和网络，不要重新启用已退役配置。
回滚至支持轻量运行器的 v1.31.81 时保留新账号路由和注册材料；不能恢复迁移前数据库，
以免账号重新引用已经删除的旧容器。

## 计划任务随机延迟

`ScheduledTasks:RandomDelaySeconds` 控制 Cron 计划任务下次运行时间的随机延迟，默认 `300` 秒，`0` 表示禁用，最大 `3600` 秒。该延迟会在创建、编辑、恢复、重算下次运行时间和每次自动触发后写入 `ScheduledTasks.NextRunAtUtc`；实际延迟不会越过下一次 Cron 窗口，例如每分钟任务最多延迟 59 秒。用途是把多个同 Cron 的任务错峰启动，避免整点同时创建大量批量任务。

Docker 环境可设置 `ScheduledTasks__RandomDelaySeconds=300`。成功判据是创建多个 `0 * * * *` 计划任务后，列表里的“下次运行”分散在整点后的随机秒数/分钟内，而不是全部等于整点；回滚时把该值设为 `0` 并重启容器即可恢复精确 Cron 时间，无需数据库迁移。

## 账号数据同步

`Sync:AutoSyncEnabled` 控制后台账号频道/群组同步，`Sync:IntervalHours` 控制自动同步间隔（1～24 小时）。同步任务会在任务中心记录每个账号的进度和失败原因。

同步期间单个 Telegram 请求被取消（例如代理瞬时中断或请求超时）只记录本次任务失败，不会把账号状态写成“Session 失效”；只有明确的 Session 错误或账号受限才会更新账号状态。需要重试时可在任务中心重新运行同步任务，或对账号执行状态刷新。

## Bot 启用/停用（每个 Bot）

机器人管理页可以对单个 Bot 启用/停用：停用后该 Bot 不会再被后台轮询 `getUpdates`，也不会被需要 Bot 的模块/任务使用。

## Bot Webhook 模式（生产环境推荐）

Bot Webhook 的完整配置与注意事项已单独整理：见 [Bot Webhook](../deployment/bot-webhook.md)。
