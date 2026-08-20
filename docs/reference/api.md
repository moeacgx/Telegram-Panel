# 管理接口速查

Vue 后台使用 `/api/panel` 下的管理接口。开启后台登录时，除登录等少数端点外都需要
管理员 Cookie；这些接口不是面向公网的稳定开放 API。完整行为以
`PanelAdminApiEndpoints.cs` 和各功能 Endpoint 文件为准。

## 登录与账号

- `POST /api/panel/auth/login`：后台登录
- `GET /api/panel/auth/me`：当前后台登录状态
- `POST /api/panel/settings/username`：修改后台用户名
- `GET /api/panel/accounts`：账号列表
- `GET /api/panel/accounts/{id}`：账号详情
- `POST /api/panel/accounts/import/zip`：导入 Telethon 或 TData 压缩包
- `POST /api/panel/accounts/import/session-files`：导入 Session 文件
- `POST /api/panel/accounts/import/string-session`：导入 StringSession
- `POST /api/panel/accounts/login/start`：开始手机号登录
- `POST /api/panel/accounts/login/qr/start`：开始二维码登录
- `POST /api/panel/accounts/login/code`：提交手机号验证码
- `POST /api/panel/accounts/login/password`：提交 2FA 密码
- `DELETE /api/panel/accounts/{id}`：删除账号
- `POST /api/panel/accounts/{id}/telegram-status`：刷新单个账号 Telegram 状态
- `POST /api/panel/accounts/telegram-status`：批量刷新账号 Telegram 状态
- `POST /api/panel/accounts/cleanup-waste`：复查并清理明确失效的账号
- `POST /api/panel/accounts/batch/category`：批量修改已选账号分类；`categoryId=null` 表示改为未分类，只影响请求里的 `accountIds`，不会覆盖分类的全部成员。
- `POST /api/panel/accounts/batch/recovery-email`：批量换绑 2FA 找回邮箱，可选同时换绑登录邮箱。单个账号可能等待 Telegram 发信和 Cloud Mail 收码；前端会按账号逐个调用该接口并聚合结果，外部自动化调用大量账号时也应拆成单账号或小批次请求，避免长连接被浏览器、Nginx 或网关超时中断。
- `GET /api/panel/accounts/{id}/devices`：读取账号在线设备；返回的 `hash` 始终是十进制字符串，避免 JavaScript 处理 Telegram 64 位授权哈希时丢失精度。
- `POST /api/panel/accounts/{id}/devices/{hash}/kick`：踢出指定非当前设备；`hash` 使用上述字符串原样放入 URL。
- `POST /api/panel/accounts/{id}/devices/kick-all`：踢出所有其他设备并保留当前授权。

自 v1.31.57 起，账号列表、账号详情、任务账号候选和风控确认中的账号 DTO 都返回
`displayNumber`。这是面向用户展示和手工填写任务账号范围的账号编号；`id` 仍是内部数据库
主键，只用于接口路径、权限校验和持久化关联。删除账号后，新账号可以复用空出的
`displayNumber`，因此外部系统不得把它当作长期不可变主键。成功判据是
`GET /api/panel/accounts` 与 `GET /api/panel/accounts/{id}` 同时返回正整数
`displayNumber`，前端任务表单在“账号来源”切到“账号编号填写”后可用 `#编号` 选择账号；该前端入口与“账号分类选择”二选一，不再把编号和分类合并执行。手工输入支持每行一个，也支持英文逗号 `,`、中文逗号 `，`、顿号 `、` 或分号分隔。回滚到旧版前无需清理数据，但旧前端不会展示该字段。


前端会为登录和导入请求明确携带 `proxyStrategy`；自定义调用也必须显式传入。省略策略、
策略无效或所选代理不可用时，服务端会在连接 Telegram 前拒绝请求，不会回退直连。不要
绕过这些入口自行先直连创建 Session。


### Telegram API 配置池设置

`GET /api/panel/settings` 的 `telegram` 字段包含兼容旧版的已写入 `apiId`、`apiHash`，以及 `officialApiEnabled` 和可选 `profiles` 数组；还会返回运行态字段 `effectiveApiId`、`effectiveApiSource`、`effectiveApiName`、`officialApiId`、`officialApiName` 和 `hasUsableApi`。`POST /api/panel/settings/telegram-api` 由系统设置页调用，并写入 `appsettings.local.json`：

```json
{
  "apiId": "",
  "apiHash": "",
  "officialApiEnabled": true,
  "profiles": [
    { "name": "api-a", "apiId": "123456", "apiHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "enabled": true, "weight": 1, "notes": "备用配置" },
    { "name": "api-b", "apiId": "234567", "apiHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "enabled": true, "weight": 1 }
  ]
}
```

`profiles` 省略时保留当前 API 配置池；传空数组表示清空配置池。系统设置不再提供单独“默认 API”选择，启用中的内置官方 API 固定排在池子最顶上，保存时旧版 `apiId/apiHash` 会被前端带入配置池并清空旧字段。服务端会校验每个 ApiHash 为 32 位十六进制字符串，名称不可重复，`weight` 规范到 `1-1000`。新账号登录和不自带 API 的导入入口按启用池子顺序轮询；权重大于 1 时该配置会在轮询序列中出现对应次数。已有账号继续使用数据库中保存的 `ApiId/ApiHash`。

### Telegram API 与设备画像

系统设置页管理 Telegram API 池；侧栏 `/device-profiles` 只管理内置/自定义设备画像和默认画像，不展示 Telegram API 状态。两者最终都通过 `POST /api/panel/settings/telegram-api` 保存，设备画像请求沿用现有 `deviceProfiles` 与 `defaultDeviceProfileKey` 字段。

`GET /api/panel/settings` 返回有效 Telegram API、API 池、启用画像和默认画像 key；`GET /api/panel/settings/device-profiles` 返回同一画像目录的 `{ items, defaultKey }`。`defaultDeviceProfileKey`/`defaultKey` 可以是保留值 `random`，表示按账号或会话稳定随机选取适合当前 API 的内置画像；`random` 不属于 `items` 画像目录，前端必须作为下拉首项单独展示。

`PUT /api/panel/accounts/{id}` 的请求可携带 `deviceProfileKey`：传画像 key 表示绑定该账号，传 `random` 表示该账号后续按稳定随机画像连接，传空字符串表示清除绑定并跟随系统默认。`GET /api/panel/accounts/{id}` 返回 `deviceProfileKey`。`POST /api/panel/accounts/import/zip`、`POST /api/panel/accounts/import/session-files` 的 multipart 字段名为 `deviceProfileKey`；StringSession JSON 请求同名字段。`POST /api/panel/accounts/login/start` 与 `POST /api/panel/accounts/login/qr/start` 也接受 `deviceProfileKey`，在发送验证码或生成二维码前应用该画像，并在登录成功后保存到账号。未知或停用 key 返回 `400` 或业务失败响应，不会修改账号。

成功判据是保存后重新读取账号详情仍返回该 key，清空后返回 `null`；画像只影响客户端设备字段，不改变 API 凭据、代理策略或 Session 文件格式。数据库列由 `20260818090000_AddAccountDeviceProfileKey` 迁移创建。

`POST /api/panel/settings/username` 的以下合同适用于 v1.31.42 及以上版本。调用方必须
已经通过管理员 Cookie 认证，请求 JSON 必须提供 `currentPassword` 和 `newUsername`。
新用户名要求 4-32 位，只包含字母、数字、下划线、短横线或点，且不能使用 `admin`、
`administrator` 或 `root`。成功时返回 `200`、`success=true`，当前登录 Cookie 会切换到
新用户名；输入不合法时返回 `400` 和可直接展示的 `message`，不会修改凭据文件。

若修改失败，先按 `message` 检查当前密码、长度、字符范围和保留名称，再修正后重试。
需要回滚时用相同接口提交原用户名，并以当前密码完成确认；成功判据是重新读取
`GET /api/panel/auth/me` 时返回原用户名。凭据文件损坏或无法写入时应先停止修改并检查
持久化目录权限，不要直接删除 `/data/admin_auth.json`。

### 在线设备动态代理恢复（v1.31.43 及以上）

`GET /api/panel/accounts/{id}/devices` 要求管理员已登录，且账号 Session 有效、账号当前选择的
直连/全局/已有代理路由可用。读取在线设备遇到超时、连接关闭或动态代理出口失效等瞬时故障时，
服务端会清理该账号的缓存客户端，重新解析当前代理并重试一次；不会为账号创建独立 WARP 容器。
Telegram 的限流、权限和 Session 等业务错误不会重试，避免扩大限流。

成功时返回 `200` 和设备数组；数组中的 `hash` 是十进制字符串，`lastActiveAtUtc` 是 Telegram 返回的服务端时间，刷新页面会重新请求 Telegram，但该字段的更新粒度由 Telegram 决定。设备踢出接口返回 `success=false` 时，前端必须展示失败原因，不得把 HTTP 200 当作业务成功。
设备踢出成功后，页面先移除已确认的设备，再延迟刷新一次设备列表，以覆盖 Telegram 授权列表的短暂传播延迟。
自动恢复后仍失败时返回 `502`，响应包含可直接展示的中文 `message` 和 `code=TELEGRAM_DEVICE_QUERY_FAILED`。此时先在代理管理中检测账号当前出口，再检查 Session 是否仍有效；不需要通过“切换代理再应用”手工清理客户端。
成功判据是代理出口短暂变化后再次打开“在线设备”仍返回 `200`，日志至多记录一次客户端重建。

在线设备响应会同时返回 `apiFamily`、`apiDisplayName`、`apiDescription` 和 `deviceDisplayName`，前端不得只根据 `ApiId` 自行猜测未知状态。`ApiId=2040` 是官方 Telegram Desktop API，属于正常官方桌面端会话；异常排查应结合 `appName/platform/deviceModel/ip` 和最近活跃时间判断。
如需回滚，恢复到 v1.31.65；本改动不包含数据库迁移，设备哈希响应从数字改为字符串，调用方需按字符串传回踢出接口。

### 账号状态恢复与安全清理（v1.31.46 及以上）

状态刷新端点遇到动态代理连接关闭、IO 错误、请求超时或非调用方触发的取消时，会清理账号缓存
客户端、重新解析当前代理并重试一次。调用方主动取消以及 Telegram RPC、权限、限流和 Session
错误不会重试。单账号端点仍返回 `TelegramStatusDto`，批量端点仍逐项返回结果，响应结构不变。

`POST /api/panel/accounts/cleanup-waste` 会在删除前重新检测账号，但只删除明确封禁、注销、受限、
冻结、需要两步验证密码或 Session 永久失效的账号。`连接失败`、`请求超时`、`刷新失败`、
创建频道探测失败和无法获取账号资料不会删除，
也不会被 `GET /api/panel/accounts?onlyWaste=true` 返回。成功判据是临时故障账号得到“跳过”结果且
数据库记录与 Session 文件均保留。持续连接失败时先检查代理出口；回滚到 v1.31.45 无需迁移数据。

### Zip 逐账号批量代理

`POST /api/panel/accounts/import/zip` 使用 `multipart/form-data`。普通导入支持
`proxyStrategy=direct|global|existing|warp_pool|warp_per_account`；`existing` 还必须提供
`proxyId`。

`warp_pool` 只自动分配代理管理中已存在、已启用且状态为 `active` 的 WARP，按绑定账号数升序、
代理 ID 升序选择。它不会创建容器或数据卷，也无需提供 `proxyId`。没有候选项或候选项都在
维护/被其他首次连接流程占用时，请求会在连接 Telegram 前失败。

`warp_per_account` 会在每个账号首次 Telegram 验证前创建一个受管 WARP，并在账号成功入库后把
新 `ProxyId` 绑定到账号。Zip 和 Session 文件导入单次最多 10 个账号，StringSession 固定 1 个；
超过上限、Docker/WARP 未启用或容器创建失败时，请求会在首次 Telegram 连接前失败。导入未成功
绑定账号的新代理会自动删除，运行档案保留 `deleted` 状态用于审计。

Zip 专属的一对一代理模式使用以下字段：

```text
file: accounts.zip
proxyStrategy: proxy_per_account
proxyText: http://user-a:password-a@proxy-a.example.com:8080
           socks5://user-b:password-b@proxy-b.example.com:1080
```

- `proxy_per_account` 只允许用于 `/accounts/import/zip`，Session 文件、StringSession、
  手机号登录和二维码登录不接受该策略。
- `proxyText` 每个有效行仅支持一个 HTTP 或 SOCKS5 地址；空行和以 `#` 开头的注释行
  不计数，重复行不去重并继续占用独立槽位。
- 单次最多匹配 100 个账号，`proxyText` 最长 100000 个字符。
- Telethon 候选按规范化的 Zip 相对 `.json` 路径稳定排序；纯 TData 候选按规范化的
  `tdata` 相对目录路径稳定排序。路径分隔符统一为 `/`，第 N 个候选固定使用第 N 个
  有效代理行。
- 账号候选数必须与有效代理行数完全一致。服务端会先解析全部代理并检测全部出口，全部
  成功后才在一个持久化阶段新增或复用代理记录，再冻结连接参数并开始第一个 Telegram
  请求。
- 任一格式、数量、凭据冲突或出口检测预检失败会返回 `400`；该请求新增代理数为 0、
  Telegram 连接数为 0，并且不会尝试面板直连。
- 全部代理持久化后，每个账号仍独立导入。某个账号的 Session 或 TData 后续失败时，其
  已持久化代理不会回滚；没有其他账号使用时会留在代理列表中并显示为未使用。

逐账号代理结果在通用导入响应的 `results` 项中增加以下审计字段：

```json
{
  "success": true,
  "phone": "8613111111111",
  "sourceKey": "8613111111111/8613111111111.json",
  "proxyLine": 1,
  "proxyId": 17,
  "proxyName": "http://proxy-a.example.com:8080",
  "proxyEgressIp": "203.0.113.10"
}
```

`sourceKey` 是 Zip 内的规范化相对路径，`proxyLine` 是 `proxyText` 中从 1 开始计算的
原始物理行号。`proxyName` 不含认证信息；响应和错误不会返回 `proxyText` 原文、代理
用户名、密码或 Secret。

## 代理与出口

- `GET /api/panel/network/egress`：检测面板服务自身出口
- `GET /api/panel/settings/global-proxy`：读取账号全局代理配置；密码与 Secret 仅返回是否已设置
- `POST /api/panel/settings/global-proxy`：启用、修改或关闭账号全局代理并清理客户端缓存
- `GET /api/panel/proxies`：代理列表
- `GET /api/panel/proxies?usage=used|unused&categoryId={id}`：按使用状态或分类筛选代理
- `GET/POST/PUT/DELETE /api/panel/proxy-categories[/{id}]`：查询和管理代理分类
- `POST /api/panel/proxies/batch/category`：批量设置代理分类
- `POST /api/panel/proxies/batch/delete`：逐项删除所选代理并返回成功、失败数及每项原因
- `POST /api/panel/proxies`：新增普通代理或 Resin
- `PUT /api/panel/proxies/{id}`：修改代理
- `POST /api/panel/proxies/{id}/test`：检测代理出口
- `GET /api/panel/proxies/warp/status`：受管 WARP 运行环境
- `POST /api/panel/proxies/warp`：创建受管 WARP
- `POST /api/panel/proxies/{id}/warp/refresh`：重启并复测单个受管 WARP
- `POST /api/panel/proxies/warp/refresh-all`：依次重启并复测全部期望启用的 WARP
- `POST /api/panel/accounts/{id}/proxy`：切换单个账号路由
- `POST /api/panel/accounts/batch/proxy`：批量切换账号路由
- `GET /api/panel/accounts/{id}/proxy/egress`：检测账号实际出口

`POST /api/panel/accounts/batch/proxy` 支持 `strategy=direct|global|existing|warp_per_account|proxy_per_account`。`proxy_per_account` 仅用于批量切换已存在账号代理，必须提供 `proxyText`，每个有效行一个 HTTP 或 SOCKS5 代理，空行和 `#` 注释不计数，数量必须与 `accountIds` 去重后的数量完全一致。服务端会先解析并检测全部代理出口，全部成功后新增或复用代理记录，再按 `accountIds` 顺序逐账号绑定；任一格式、数量、凭据冲突或出口检测失败会返回 `400`，不会修改账号路由。失败排查先看响应 `items[].error` 或全局错误；回滚方式是重新用原代理、全局代理或直连策略批量切换账号。

`POST /api/panel/settings/global-proxy` 使用 `sourceMode=manual|existing`。`existing` 模式
必须提供 `proxyId`，服务端只保存引用并在运行时解析代理；不会把 WARP 或 Resin 的连接
凭据复制到全局配置。

## 频道、群组和 Bot

- `GET /api/panel/channels` / `GET /api/panel/groups`：列表和筛选
- `GET /api/panel/channels/{id}` / `GET /api/panel/groups/{id}`：详情
- `POST /api/panel/channels` / `POST /api/panel/groups`：创建
- `GET /api/panel/bots`：Bot 列表
- `GET /api/panel/bot-channels`：Bot 频道列表

批量邀请、管理员变更、退出和解散等端点可在对应 Vue API 调用或 Endpoint 文件中查看。

`POST /api/panel/accounts/batch/profile` 的 `mode=bio` 支持与昵称/用户名相同的文本模板：`{time}` 和已启用文本字典变量会在每个账号执行前解析。模板和字典文本中的字面 `\\n` 或 `/n` 会转为真实换行；留空仍表示清空 Bio。成功判据是 Bio 在 Telegram 资料中按换行展示，模板变量解析失败时单项返回失败原因；回滚到旧版前应把 Bio 改回固定文本或提前写入真实换行。

## 任务和模块

- `GET /api/panel/tasks`：任务列表
- `GET /api/panel/tasks/{id}`：任务详情，包含完整 `config`
- `POST /api/panel/tasks`：创建任务
- `POST /api/panel/tasks/{id}/pause`：暂停
- `POST /api/panel/tasks/{id}/resume`：恢复
- `POST /api/panel/tasks/{id}/cancel`：取消
- `DELETE /api/panel/tasks/{id}`：删除
- `GET /api/panel/modules`：模块列表
- `POST /api/panel/modules/install`：安装模块包
- `/api/panel/extensions/{module-slug}`：模块自定义后台管理接口约定
- `GET /api/panel/extensions/fragment-username-checker`：Fragment 用户名监控静态页面初始化数据；可带 `taskId` 读取可编辑任务配置。
- `POST /api/panel/extensions/fragment-username-checker/tasks`：创建或保存 Fragment 用户名监控任务；请求包含 `usernames`、`targetGroupIds`、`checkIntervalSeconds`、`queryDelayMs`、`durationHours`，编辑时额外传 `taskId`。

- `fragment_username_monitor` 可直接通过 `POST /api/panel/tasks` 或 `PATCH /api/panel/tasks/{id}` 保存配置，前端表单写入的配置键为 `Usernames`、`TargetGroupIds`、`CheckIntervalSeconds`、`QueryDelayMs`、`DurationHours`；保存时会清空 `StartedAtUtc`、`AssignedUsernames`、`LastCheckTime`、`Error`、`Canceled` 等运行态字段。适用条件是宿主前端包含 Fragment 任务中心表单，且已安装提供该任务定义的 Fragment 模块 1.2.9+。

`GET /api/panel/tasks` 和 `GET /api/panel/tasks/{id}` 的 `BatchTaskDto` 包含可空 `name`。普通一次性任务可为空，前端使用“任务类型 #ID”兜底；计划任务触发或手动“立即执行”创建的批量任务会复制计划任务名称，历史任务应优先展示 `name`，并保留任务类型作为辅助说明。自 v1.31.57 起，`POST /api/panel/tasks` 和 `PATCH /api/panel/tasks/{id}` 支持可选 `name`，服务端会去除首尾空白并限制最多 100 个字符；即时任务传空或省略时继续使用兜底显示，编辑任务时省略 `name` 会保留原名称，传空字符串会清空名称。该字段通过 `BatchTasks.Name` 持久化；回滚到旧版需先忽略或删除该列。

任务中心“复制”入口对已知 `taskType` 通用：复制执行中/历史 `BatchTask` 会读取 `GET /api/panel/tasks/{id}`，复制 `ScheduledTask` 会读取 `GET /api/panel/scheduled-tasks/{id}`，再用原 `taskType` 和清理运行态后的配置打开“新建任务”弹窗。内置专用表单会按配置回填；模块任务即使有独立 `CreateRoute`，复制时也走通用 JSON 配置区，确认后仍通过 `POST /api/panel/tasks` 或 `POST /api/panel/scheduled-tasks` 创建新记录。复制不会修改原任务，也不会绕过后端 JSON 校验。

自 v1.31.59 起，任务中心的“编辑计划任务”弹窗在窄屏设备上使用 `min(760px, calc(100vw - 24px))` 宽度，并将表单标签切换为顶部布局；Cron、状态、专用任务配置和保存按钮不会依赖横向滚动。成功判据是在手机宽度打开计划任务编辑时，弹窗不超出视口、字段按单列排列且“保存计划任务”可见；回滚到旧版只会恢复固定 760px 弹窗，不涉及接口或数据库迁移。
自 v1.31.70 起，计划任务保存、恢复、后台补算和每次自动触发后会对 `NextRunAtUtc` 加入 `ScheduledTasks:RandomDelaySeconds` 范围内的随机延迟，默认 300 秒，且不会越过下一次 Cron 窗口。`GET /api/panel/scheduled-tasks` 和 `GET /api/panel/scheduled-tasks/{id}` 返回的是已经持久化的错峰后时间；`POST /api/panel/scheduled-tasks/{id}/run-now` 只立即创建本次执行记录，后续计划仍按 Cron 加随机延迟重算。
自 v1.31.44 起，`channel_group_private_create` 任务会在 `config.recent_failures`
返回最近 20 条失败明细。字段包括 `time_utc`、`account_id`、`target_type`、
`target` 和 `reason`。自 v1.31.48 起，`user_chat_active` 会在
`config.recent_failures` 返回最多 100 条账号活跃失败明细，字段包括 `time_utc`、
`account_id`、`account`、`target` 和 `reason`；自 v1.31.54 起，`user_chat_active`
目标支持群组、频道和 Bot 私聊（`@xxxbot`、`t.me/xxxbot?start=...`、
`tg://resolve?domain=xxxbot`）。目标列表的某一项可以是单个文本字典变量（例如 `{groups}`），执行器会把该字典的全部启用文本项展开成实际目标；字典项内部可用换行、空格或逗号分隔多个目标。目标字典不能使用 `{time}` 或图片字典，未知、停用或空文本字典会导致创建/启动失败。

自 v1.31.55 起，`user_chat_active.config.message_rules` 是消息选择的主合同。它是对象数组，
每项包含可选的 `text` 和 `image_dictionary_token`：`text` 保留内部换行，可用于多段消息；
`image_dictionary_token` 必须是单个已启用图片字典变量，例如 `{active_images}`。一条规则可以
只含文字、只含图片，或同时包含图片和说明文字；`message_mode=random|queue` 针对整条规则选择，
不会再把段落中的每一行拆成独立消息。前置条件是引用的文本/图片字典已启用且至少有一个可用项。

兼容字段 `dictionary` 和 `image_dictionary_token` 仍会读写：旧配置会在加载时转换为等价规则；
`dictionary` 保存所有非空规则文字，只有全部规则共享同一个非空图片字典时才回写全局
`image_dictionary_token`。成功判据是保存后任务详情包含 `message_rules`，重新编辑仍保留段落换行和
每条图片字典，实际执行按规则随机或循环。图片字典无效时创建页或任务启动会返回模板校验错误。

自当前开发版起，`user_chat_active` 创建/编辑表单的账号来源在“账号分类选择”和“账号编号填写”之间
二选一。前端提交分类来源时只写入 `category_ids`，提交编号来源时只写入 `account_numbers`；后端仍按现有
校验返回“请选择账号分类”或“请填写账号编号”。成功判据是创建页切换来源后只显示对应输入框，历史配置重新编辑时
按已保存的编号或分类恢复来源。回滚到旧版前无需迁移数据，但同时写入两类来源的旧配置在新版编辑时会优先按
账号编号来源展示。
回滚到 v1.31.54 或更早版本时，旧字段仍可继续发送文字；每条规则使用不同图片字典的配置无法被旧版
完整表达，回滚前应改为全部规则共用一个图片字典或纯文字规则。

自 v1.31.56 起，`user_chat_active.config` 增加 `message_action_mode`、`reply_to_message_url`、`reply_to_message_id`、`forward_source_urls`、`forward_mode` 和 `skip_if_last_message_from_self`。`message_action_mode` 默认为 `send_generated_text`，继续使用 `message_rules` 发送；前端只展示 `reply_to_message_url`，通过 Telegram 消息链接解析回复消息 ID。`reply_to_message_id` 保留为原始 API 兼容字段，自动化调用可继续大于 0 传入，但与 `reply_to_message_url` 同时存在时必须指向同一条消息。`message_action_mode=forward_url` 时，`forward_source_urls` 必须至少包含一个 Telegram 消息链接，`forward_mode=with_attribution|hide_attribution` 控制原生转发是否保留来源引用；此时前端不展示内容模式，默认按随机来源选择保存，原始 API 仍可用 `message_mode=queue` 改为队列选择。此模式不执行模板渲染、图片字典或 AI 验证。`skip_if_last_message_from_self=true` 时，发送或转发前会读取目标最新普通消息；若它仍由当前执行账号发出，本轮记为已处理但不发送，以避免同账号连续刷屏。成功判据是任务详情配置摘要显示“发送动作”和“去重发送”，且转发模式不再显示内容模式；`recent_failures` 为空或只记录真实 Telegram 访问/权限错误。开启去重但无法读取目标最新消息时，本轮会失败并记录原因。回滚到 v1.31.55 或更早版本前，应把任务重新保存为 `send_generated_text` 并关闭去重，否则旧版不会理解转发来源和去重字段。

自当前开发版起，`user_chat_active.config.forward_source_urls` 的单项也可以是单个文本字典变量（例如 `{forward_sources}`）。执行器启动时会把该字典全部启用内容展开为 Telegram 消息链接，字典内容可用换行、空格或逗号分隔多个来源链接；未知、停用、空文本字典或展开后不是有效 Telegram 消息链接都会启动失败，不会静默跳过。`account_mode=queue` 会维护 `account_queue_cursor`，有限任务多次运行时会从上次使用后的下一个账号继续轮询；回滚到旧版前无需迁移，但旧版会忽略该游标并从第一个账号重新开始。

`user_join_subscribe` 会在 `config.failures` 返回最多 200 条失败明细，字段包括 `accountId`、
`target` 和 `reason`，并会对 Telegram 瞬时连接错误执行一次客户端重建重试。
`user_join_subscribe.config.DelayMs` 和即时 `/accounts/chat-membership` 的 `delayMs` 允许 `0-60000` 毫秒，服务端会按该范围夹取；系统设置里的默认批量操作间隔为 `1000-60000` 毫秒。Plain Bot 链接（例如 `https://t.me/SpamBot`、`@examplebot`）没有 `start` 参数时会向 Bot 私聊发送普通 `/start`，不会再调用必须携带 `start_param` 的 deep-link 接口；带 `?start=abc` 时仍走 Telegram deep-link 启动接口。
成功判据是任务的 `failed` 大于零时，详情接口和任务中心均能看到对应失败账号、目标和原因；
该字段为空表示没有失败或运行的是尚未支持失败明细的旧版本。

失败原因来自当次执行，最长 500 字符。接口调用方不得把其中内容当作稳定错误码；需要自动化
判断时应优先匹配 Telegram/RPC 的明确错误标识。回滚到 v1.31.53 或更早版本不会解析 Bot 私聊活跃目标，
也不会继续写入新增重试后的失败描述。

`auto_change_login_email` 任务使用 `config.items` 返回最近账号级结果，字段包括 `time_utc`、
`account_id`、`phone`、`email`、`result`、`message`、`matched_message_id` 和
`matched_message_date_utc`。`result` 只表示该账号在本轮任务中的处理结果：`success` 表示已发送
并在开启自动确认时完成登录邮箱验证码确认，`skipped` 表示因 Cloud Mail 配置、目标邮箱或通知匹配
条件不足而未操作，`failed` 表示 Telegram/Cloud Mail 调用失败或收码确认失败。默认不会删除、停用
或禁用账号；除非配置 `force=true`，否则没有匹配 777000 登录邮箱重置通知的账号不会被换绑。

需要给外部系统调用时，优先使用模块的 `MapEndpoints` 明确设计鉴权、限流和响应模型，
不要直接把管理 Cookie 接口暴露到公网。
