# 配置与数据目录

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
- `Proxy__Warp__Enabled`：允许面板管理独立 WARP 容器
- `Proxy__Warp__Network`：WARP 容器加入的 Docker 网络
- `Proxy__Warp__Protocol`：自动创建 WARP 时默认使用 `http` 或 `socks5`
- `Proxy__Warp__MaxManagedProxyCount`：受管 WARP 最大数量；`0` 表示不限制
- `Proxy__Warp__Container__MemoryLimitBytes`：单个受管 WARP 的内存上限字节数；`0` 表示不设置
- `Proxy__Warp__Container__CpuLimit`：单个受管 WARP 的 CPU 限制，示例 `0.5`；`0` 表示不设置
- `Proxy__Warp__Container__PidsLimit`：单个受管 WARP 的 PIDs 上限；`0` 表示不设置
- `Proxy__Warp__Maintenance__Enabled`：启用受管 WARP 出口巡检与故障恢复
- `Proxy__Warp__Maintenance__HealthCheckIntervalMinutes`：巡检周期，默认 5 分钟
- `Proxy__Warp__Maintenance__FailureThreshold`：连续失败恢复阈值，默认 2 次
- `Proxy__Warp__Maintenance__RecoveryCooldownMinutes`：失败恢复冷却，默认 30 分钟
- `Proxy__Warp__Maintenance__ScheduledRefreshEnabled`：是否定时重启健康出口，默认关闭
- `Proxy__Warp__Maintenance__ScheduledRefreshIntervalMinutes`：健康出口定时刷新周期，默认 720 分钟
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

`Telegram:ApiId` / `Telegram:ApiHash` 仍是兼容旧版本的默认单 API 配置。需要分散新账号时，可在系统设置的“Telegram API 配置池”添加多个启用项，或手工配置：

```json
{
  "Telegram": {
    "ApiId": 123456,
    "ApiHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "ApiProfiles": [
      { "Name": "api-a", "ApiId": 123456, "ApiHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Enabled": true, "Weight": 1, "Notes": "主配置" },
      { "Name": "api-b", "ApiId": 234567, "ApiHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Enabled": true, "Weight": 1 }
    ]
  }
}
```

新手机号登录、二维码登录、Session 文件导入、StringSession 导入和纯 TData 导入会在启用配置中按账号已保存的 `ApiId/ApiHash` 使用量选择最少的一项；`Weight` 越大可承载的相对账号数越多。禁用项不会被分配。Telethon Zip 内自带 `api_id/api_hash` 的账号继续使用包内配置。已有账号操作优先使用账号表中保存的 `ApiId/ApiHash`，只有账号缺少这两个字段时才回退全局单 API，因此保存配置池不会迁移或改写已有账号。

如果配置池为空或所有项禁用，系统会继续使用 `Telegram:ApiId` / `Telegram:ApiHash`，保持单 API 部署兼容。保存 Telegram API 设置会清理客户端缓存；正在使用旧 Session 的账号不会被批量改写，如需切换 API 请重新登录或重新导入对应账号。
### 群聊活跃任务发送分配

“用户群聊活跃”任务在准备阶段会先过滤停用账号、异常账号、排除批量操作的分类，以及无法解析目标的账号-目标组合。
当 `MaxMessages` 大于 `0` 时，本次运行的有效发送条数会按准备后仍可用的执行账号数封顶；例如请求发送 10 条但最终只有 1 个账号可用时，只会计划 1 条，不会让该账号轮回发送剩余 9 条。账号足够时按账号一对一分配，每个账号在同一次有限运行中最多发送 1 条；发送失败自动重试仍只针对当前账号和当前消息生效，不会改变该分配。

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

前置条件是对象存储 URL 在面板容器内可访问，且签名允许对应 HTTP 方法上传 `application/zip`。成功判据是系统设置点击“立即备份”返回成功，存储桶出现 ZIP，解压后能看到 `telegram-panel.db` 和 `sessions/`。失败时先检查 URL 是否过期、方法是否匹配、容器 DNS/网络是否可达，以及 Authorization Header 是否需要清空后重填。回滚方式是关闭 `BucketBackup:Enabled` 或清空上传 URL；已上传对象需要在存储桶侧按生命周期或人工删除。

当前后台面板不提供“导入备份恢复”入口，也不支持运行中覆盖数据。需要恢复时，先下载备份 ZIP，停机替换持久化数据目录，再重启验收；步骤见[从存储桶备份恢复](../getting-started/update.md#bucket-backup-restore)。

备份包包含账号 Session 和后台凭据，必须限制对象存储访问权限，不要把备份桶公开读。

## UI 保存到本地覆盖配置

面板里的部分“保存”按钮会把设置写入 `appsettings.local.json`（Docker 下为 `/data/appsettings.local.json`），常见键：

- `Telegram:BotAutoSyncEnabled` / `Telegram:BotAutoSyncIntervalSeconds`：Bot 频道后台自动同步轮询开关/间隔
- `ChannelAdminDefaults:Rights`：批量设置管理员的“默认权限”
- `ChannelAdminPresets:Presets`：批量设置管理员的“用户名列表预设”（名称 -> usernames）
- `ChannelInvitePresets:Presets`：批量邀请成员的“用户名列表预设”（名称 -> usernames）

## 账号代理优先于全局代理

代理管理中的 HTTP、SOCKS5、MTProxy、外部 WireGuard WARP、WARP 和 Resin 可以绑定到单个或多个账号。
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

## 配置受管 WARP 默认值

使用 `docker-compose.warp.yml` 时，在 `.env` 设置：

```dotenv
TP_WARP_DOCKER_NETWORK=telegram-panel_default
TP_WARP_PROXY_PROTOCOL=http
TP_WARP_MAX_MANAGED_PROXY_COUNT=0
TP_WARP_CONTAINER_MEMORY_LIMIT_BYTES=0
TP_WARP_CONTAINER_CPU_LIMIT=0
TP_WARP_CONTAINER_PIDS_LIMIT=0
TP_WARP_AUTO_RECOVERY_ENABLED=true
TP_WARP_HEALTH_CHECK_INTERVAL_MINUTES=5
TP_WARP_FAILURE_THRESHOLD=2
TP_WARP_RECOVERY_COOLDOWN_MINUTES=30
TP_WARP_SCHEDULED_REFRESH_ENABLED=false
TP_WARP_SCHEDULED_REFRESH_INTERVAL_MINUTES=720
```

Compose 会映射为 `Proxy:Warp:Network`、`Proxy:Warp:Protocol`、`Proxy:Warp:MaxManagedProxyCount`
和 `Proxy:Warp:Container:*`。修改后需要使用包含 `docker-compose.warp.yml` 的命令重新创建
面板容器；已存在的 WARP 容器不会自动重建，资源限制只应用到后续创建的受管容器。代理管理中
的一键创建弹窗可以覆盖单次创建协议；登录和批量绑定自动创建 WARP 时使用这里的默认值。账号
导入的自动 WARP 池不会创建新容器，并沿用已有代理记录自身的协议。

`TP_WARP_MAX_MANAGED_PROXY_COUNT=0` 表示不限制数量，保持旧安装行为；设置为正整数后，达到上限
会在创建 Docker 卷或容器前失败。`TP_WARP_CONTAINER_MEMORY_LIMIT_BYTES`、
`TP_WARP_CONTAINER_CPU_LIMIT`（例如 `0.5`）和 `TP_WARP_CONTAINER_PIDS_LIMIT` 为单容器模板，
任一项为 `0` 或留空时不写入对应 Docker HostConfig。成功标准是新建 WARP 的 Docker inspect
中出现配置的 `Memory`、`NanoCpus` 或 `PidsLimit`，达到上限时没有新增代理记录或 Docker 资源；
排障先检查 `.env` 是否被 Compose 读入、数值是否为正数且在有效范围内。回滚时把这些值改回 `0`
并 `docker compose -f docker-compose.yml -f docker-compose.warp.yml up -d --force-recreate telegram-panel`，
无需迁移数据库；已创建容器如需移除限制，需要删除后按新模板重建。

默认 `Proxy:Warp:ProxyHostMode=container` 不发布宿主端口。自定义为 `published` 时，
`Proxy:Warp:HostPortStart` 默认从 `42080` 起步；已占用或 Docker 绑定时发生冲突的端口会
自动跳过并递增重试，失败重建不会删除已经创建的 WARP 数据卷。

自动恢复会保留原数据卷，只重启容器并重新检测出口。健康出口的周期刷新默认关闭，
因为它可能改变账号公网 IP；需要与 tokens-pro 相同的 720 分钟刷新行为时再显式开启。

## 账号数据同步

`Sync:AutoSyncEnabled` 控制后台账号频道/群组同步，`Sync:IntervalHours` 控制自动同步间隔（1～24 小时）。同步任务会在任务中心记录每个账号的进度和失败原因。

同步期间单个 Telegram 请求被取消（例如代理瞬时中断或请求超时）只记录本次任务失败，不会把账号状态写成“Session 失效”；只有明确的 Session 错误或账号受限才会更新账号状态。需要重试时可在任务中心重新运行同步任务，或对账号执行状态刷新。

## Bot 启用/停用（每个 Bot）

机器人管理页可以对单个 Bot 启用/停用：停用后该 Bot 不会再被后台轮询 `getUpdates`，也不会被需要 Bot 的模块/任务使用。

## Bot Webhook 模式（生产环境推荐）

Bot Webhook 的完整配置与注意事项已单独整理：见 [Bot Webhook](../deployment/bot-webhook.md)。
