# 文档维护

本项目使用 **MkDocs Material** 生成文档站，文档源文件统一放在 `docs/`。

## 本地预览

使用 `uv`（推荐）：

```bash
uv venv
uv pip install -r requirements-docs.txt
uv run mkdocs serve
```

生成静态站点：

```bash
uv run mkdocs build
```

## 目录约定（面向使用者优先）

- `docs/getting-started/`：从 0 到可用（安装、升级、FAQ）
- `docs/guides/`：日常使用与操作指南
- `docs/deployment/`：反向代理、Webhook、生产运维相关
- `docs/reference/`：配置/数据库/API 等参考型内容
- `docs/developer/`：模块开发与维护者说明

## 新增/移动页面的规则

- 新页面：直接在对应目录新增 `*.md`
- 侧边栏与顺序：在 `mkdocs.yml` 的 `nav:` 中维护
- 链接：尽量使用相对路径链接（例如 `../guides/sync.md`），避免写死仓库 URL

## 重要改动文档门禁

新增功能、用户可见行为、API、配置项、数据结构、模块宿主合同、部署方式、运维状态或兼容性行为时，必须在同一个提交或 PR 中同步更新文档。根目录 `AGENTS.md` 是 Agent 执行门禁；本页负责说明文档落点。

按改动类型选择文档位置：

- 模块开发、宿主 API、页面内嵌合同、任务编辑器和运行态：`docs/developer/modules.md`
- API、配置、环境变量、数据库和持久化格式：`docs/reference/`
- Docker、云端部署、升级、回滚和健康检查：`docs/deployment/` 或 `docs/getting-started/`
- 用户可见的新功能和快速配置：`README.zh-CN.md` 及对应使用指南
- 发布分支、云端验收和分支清理：[`开发发布流程`](release-process.md)

每个重要改动至少写清：适用版本、前置条件、行为或合同、验证步骤、失败排查和回滚方式。只更新代码而不更新文档，不能视为完成；若确实不需要文档，必须在提交说明或 PR 中记录原因。

## 近期功能文档对照

### 图片解码前的 TIFF 限制（v1.31.79）

宿主 `TelegramImageProcessor` 的头像和普通图片入口在 `Image.LoadAsync` 前检查真实文件头，
拒绝大小端 TIFF（42）和 BigTIFF（43），不依赖扩展名或 MIME。拒绝时抛出带中文提示的
`InvalidOperationException`，任务头像上传和图片字典上传返回 400；其他宿主调用沿用已有
失败处理。JPEG、PNG、WebP、EXIF 自动方向纠正、缩放和 JPEG 输出保持原行为。
头像、图片资产及 Telegram 图片文档预览复用此边界；外部模块自行调用解码器不在此保证范围内。

此限制用于阻断宿主 TIFF 解码入口，包括 CCITT 越界写和 BigTIFF 目录循环风险，
不代表修复 ImageSharp 的全部安全公告。当前仍固定上一版依赖 3.1.12；上游 3.2.0 已回移
相关修复并要求构建许可证，后续升级必须满足许可和兼容性要求，不得绕过许可检查。
验收运行 `TelegramImageProcessorTests`：正常未压缩 TIFF 被拒绝、四种文件头均被两个入口拒绝、
伪装为 JPEG 的任务头像返回 400 且不写入图片文件，常用格式及 EXIF 方向继续正确。
只用正常小图和短文件头测试，不运行致命越界或耗时型漏洞样本。
本次无数据库迁移；回滚应用会重新开放 TIFF 解码风险，已有 JPEG 资产无需转换。

### 手机号登录的人机验证诊断（v1.31.79）

前置条件：当前宿主使用 WTelegramClient 4.4.8。其 `Login` 流程没有 reCAPTCHA 交互步骤；
SDK 的 `InvokeWithReCaptcha` 文档标记为仅供官方客户端使用。Telegram 返回
`RECAPTCHA_CHECK_<action>__<key>` 时，面板只能解释限制，不能仅凭错误中的 `signup`
推断账号未注册，也不得伪装官方客户端或自动解题。

`TelegramLoginChallenge` 统一识别错误前缀并提供中文说明。`AccountService` 返回
`LoginResult(false, null, message)`，不记录原始异常；`TelegramClientPool` 同时过滤 SDK 的
原始日志，避免 `RpcError` trace 泄漏挑战串。原有登录失败、取消、临时 Session 和 WARP
清理流程不变，其他错误保持既有诊断内容；没有数据库迁移、配置或 API 字段变化。

验收需运行 `ManualLoginErrorFeedbackTests` 和 `ManualLoginProxyRoutingTests`：覆盖注册/登录
挑战、截断的挑战、原始日志过滤、普通限流错误，以及代理资源清理。挑战失败不应重试或进入
验证码步骤。真实账号是否能在官方验证后登录必须单独验收，不能用离线测试声明已解除限制。
失败时核对部署版本和错误类型，回滚仅需恢复旧应用；旧版会恢复原始提示和日志。

本次还将 ImageSharp 从浮动 `3.*` 固定为上一版本实际使用的 `3.1.12`，防止全新还原意外
升级到要求额外构建许可证的 `3.2.0`。这不是依赖安全升级；现有 NuGet 安全公告仍需独立处理。
后续升级必须先确认许可与兼容性，不能跳过依赖自身的许可校验。

当前最近一批改动的文档落点如下，后续开发按同一规则维护：

- 轻量 WARP 统一列表、单账号占用、档案上限及旧容器迁移：`README.zh-CN.md`、`docker-compose.yml`、`docs/guides/proxy-management.md`、`docs/deployment/wgcf-wireproxy.md`；运行器合同见 `docs/developer/wgcf-wireproxy.md`，模块影响见 `docs/developer/modules.md`。v1.31.82 起不再推荐旧 WARP Compose、Docker Socket 或容器巡检配置。
- 任务中心只展示宿主验证通过的可创建编辑器、独立配置页编辑已有任务：`docs/developer/modules.md`。
- 模块页面内嵌链路、运行态字段、打包校验和生产复核：`docs/developer/modules.md` 及 `skills/tgpanel-module-workflow/references/`。
- `dev -> 云端验收 -> main` 的发布顺序：[`开发发布流程`](release-process.md)。

后续功能若改变以上行为，必须同时修改对应条目，不得只追加代码或测试。

## 账号分类页面合同

适用版本：v1.31.78 及后续版本。前置条件是管理员已登录，且账号与分类接口可用。

- 账号分类页只展示分类列表、分类维护操作和分类账号数量，不渲染具体账号、账号筛选器、账号选择框或批量改分类入口。
- 账号数量汇总可沿用现有读取逻辑；不能因为不展示账号行而把非空分类的数量置零，也不能改变未分类账号的统计口径。
- 账号列表页继续提供筛选、选择账号和批量改分类能力；本次仅调整前端页面职责，不修改后端接口、账号归属、分类记录或数据库结构。

验收时同时检查空数据和已有分类、已有账号的数据：分类页能新增、编辑、删除分类并刷新数量；
页面中不出现手机号、账号昵称和批量改分类控件；账号列表仍能按分类筛选、对所选账号批量改分类，
随后刷新分类页能看到更新后的数量。分类为空或接口失败时，检查空态、错误提示和重新加载是否正常。
执行前端构建、前端测试及严格文档构建，并按[开发发布流程](release-process.md)记录云端验收证据。

失败排查先检查部署版本与浏览器缓存，再检查账号和分类读取接口的响应；数量错误时核对账号的
分类关联。回滚到本次调整前的程序版本即可恢复旧页面，无需回滚数据库或清除账号分类数据。

## 账号详情密码与登录邮箱合同

适用版本：v1.31.78 及后续版本。前置条件是管理员已登录，数据库迁移完成，
账号 Session 和连接出口可用；自动收码还需要可用的 Cloud Mail 配置。

管理员详情弹窗直接显示本地保存的二级密码，编辑和保存仍沿用现有密码接口。登录邮箱由
Core 的共享流程维护，手动更换、批量更换和计划任务必须使用同一份持久化与核验逻辑：

1. Telegram 成功发送更换验证码后，只保存待确认的目标地址；发送成功不代表当前登录邮箱已改变。
2. 验证码确认成功后，核对 `VerifyEmail` 返回的实际地址与待确认地址一致，才能保存完整登录邮箱；
   用 `GetPassword` 返回的真实官方掩码建立核验基线，不自行生成掩码，并结束待确认状态。
   如果此次 `GetPassword` 失败或返回空掩码，仍保存已确认的完整地址，但标为未核验；后续查询
   不得仅凭一个新掩码自动补建确认时缺失的基线。
3. 打开详情时查询 Telegram 当前邮箱状态。官方掩码与已确认记录一致时，展示本地完整邮箱；
   该判断是掩码一致性核验，不能宣称 Telegram 重新返回了完整地址，也无法识别另一个恰好具有
   相同掩码的邮箱。
4. 只有官方掩码明显不匹配或官方明确没有登录邮箱时，才使旧的已确认记录失效，改为展示当前
   官方掩码或无邮箱状态。官方表示有邮箱但掩码为空时，保留完整地址并标记未核验。
5. 网络、代理或 Session 等原因导致查询失败时，保留历史完整邮箱，并明确标记“未核验”，
   不得把查询失败当作没有邮箱，也不得把历史地址标记为已确认的当前地址。

旧账号若没有经面板确认并保存的完整邮箱，只能展示官方掩码；不能从星号还原地址。待确认地址
不得作为当前邮箱展示。接口输出和页面日志不得混淆登录邮箱与 2FA 找回邮箱。

迁移 `20261009000000_AddAccountLoginEmails` 创建独立 `AccountLoginEmails` 表。
详情由 `GetLoginEmailDisplayAsync` 返回
`LoginEmailDisplayResult(Success, Error, HasLoginEmail, LoginEmailPattern, LoginEmail, VerificationStatus)`；
Web 接口保留原字段，增加 `loginEmail` 和 `verificationStatus`（`verified`、`unverified`、
`unavailable`）。前端必须同时处理成功标志与核验状态，不能只根据地址非空判断云端查询成功。

验收覆盖：发送后仍保留原邮箱、确认成功后持久化、刷新及重启后恢复、官方掩码一致/不一致、
官方无邮箱、官方空掩码、查询失败保留历史明文且标记未核验、确认后首次读取失败且后续不自动补建
基线、旧账号无缓存、手动/批量/计划任务共用逻辑；
同时确认详情密码直接可读且能保存。执行对应 .NET 构建与测试、前端构建与测试、严格文档构建。

失败排查先检查迁移是否完成，再检查账号 Session、代理、Telegram 确认结果和 Cloud Mail 收码结果；
查询失败时先恢复连接后重新核验，不要通过清空历史记录修复网络故障。升级前备份数据库；回滚旧
程序后旧页面不会使用新增邮箱表，不应手动删除该表或迁移历史。若需撤销数据库迁移，应停止
服务并恢复升级前备份，避免丢失升级后保存的账号变更。

## 设备授权与账号导出合同（v1.31.66）

- 账号导出必须在生成独立 session 前读取当前 Telegram authorization；导出客户端的 `app_version`、`device_model` 和 `system_version` 优先复用当前授权，缺失时才使用 `TelegramClientDeviceProfile` 的 ApiId 族回退值。
- 在线设备 DTO 的授权 `hash` 必须序列化为十进制字符串。Telegram 哈希是 64 位整数，Vue/JavaScript 不得用 `number` 保存或拼接该值。
- 踢出接口必须检查 Telegram 返回的业务 `success`；成功后允许前端先移除行并延迟刷新，避免 Telegram 授权列表传播延迟造成“已踢出但仍显示”。
- 验证至少包括：当前授权画像覆盖测试、长哈希 JSON 字符串测试、前端 86 个测试、`vue-tsc`、前端构建和 .NET Release 构建。回滚到 v1.31.65；无数据库迁移。
## Telegram 设备指纹合同

适用版本：包含 `Account.DeviceProfileKey` 与迁移 `20260818090000_AddAccountDeviceProfileKey` 的版本。

- `TelegramDeviceProfileCatalog` 是唯一画像解析入口；未知、停用或空 key 必须回退系统默认画像，不得在各服务中复制默认值。保留 key `random` 表示不绑定目录项，而是按账号/会话 stable key 走 `TelegramClientDeviceProfile.ForStableKey` 稳定随机生成画像；自定义目录不得复用该 key。侧栏 `/device-profiles` 是设备画像独立入口，只展示画像目录和默认画像保存，不展示 Telegram API 状态；默认画像下拉必须把“随机设备指纹”作为首项单独展示，Telegram API 池必须保留在系统设置页，不再新增独立侧栏页。
- `TelegramApiProfilePool` 把启用中的内置官方 API 与自定义 API 配置合并成一个池子，并按权重轮询分配给新账号登录和不自带 API 的导入。`Telegram:ApiId`/`Telegram:ApiHash` 仅作旧版单 API 兼容；新版系统设置保存时应把它带入 `ApiProfiles` 并清空旧字段。设置接口必须通过 `telegram.officialApiEnabled`、`telegram.effectiveApiId`、`telegram.effectiveApiSource`、`telegram.officialApiId` 和 `telegram.hasUsableApi` 暴露有效状态，前端不得只用已写入的 `telegram.apiId/apiHash` 判断可用性。
- `TelegramClientPool`、Session 导入验证、账号导出和账号登录必须在创建 `WTelegram.Client` 前解析画像；手动登录页必须在发送验证码/生成二维码前提交 `deviceProfileKey`，后端需在临时登录状态中冻结该 key，登录成功后保存到账号。代理解析与画像解析相互独立，画像不得改变连接出口。
- 新登录/导入成功入库时保存画像 key；账号详情更新允许清空 key，表示跟随系统默认。更新画像不改写现有 Session，客户端缓存清理后在下一次创建客户端时生效。
- API 端点：`GET /api/panel/settings` 返回有效 Telegram API、来源字段、`officialApiEnabled` 与 `telegram.deviceProfiles`；`GET /api/panel/settings/device-profiles` 返回画像目录和可为 `random` 的默认 key；`POST /api/panel/settings/telegram-api` 保存 API 池、内置官方 API 启用状态和默认画像；账号 `PUT /api/panel/accounts/{id}` 和登录/导入请求接受画像 key、空字符串和 `random`。

验证：运行 .NET Release 构建和完整 Web 测试；运行前端 `vue-tsc`/build 与前端测试；手工检查系统设置中的 Telegram API 池、内置官方 API 顶部项、API 池轮询、设备指纹页面默认画像下拉首项“随机设备指纹”、手动登录设备指纹选择、账号详情清空/保存及新客户端创建。失败排查先检查迁移、画像 key、API 配置和本地配置文件权限。回滚需先备份数据库和 `appsettings.local.json`，再恢复旧程序；旧程序不会使用画像字段，但不应删除迁移历史。

## GitHub Pages 发布

已内置工作流：`.github/workflows/docs.yml`。

启用方式（只需要做一次）：

1) 仓库 Settings → Pages
2) Source 选择 **GitHub Actions**

之后每次合并到 `main`（且改动命中 `docs/**`/`mkdocs.yml` 等）会自动构建并发布。
