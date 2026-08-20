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

当前最近一批改动的文档落点如下，后续开发按同一规则维护：

- WARP 默认协议、HTTP/SOCKS5 选择、资源限制模板、数量上限和轻量出口巡检配置：`README.zh-CN.md`、`.env.example`、`docker-compose.yml`、`docker-compose.warp.yml`、`docs/guides/proxy-management.md`；实现合同和模块影响见 `docs/developer/modules.md`。
- 任务中心只展示宿主验证通过的可创建编辑器、独立配置页编辑已有任务：`docs/developer/modules.md`。
- 模块页面内嵌链路、运行态字段、打包校验和生产复核：`docs/developer/modules.md` 及 `skills/tgpanel-module-workflow/references/`。
- `dev -> 云端验收 -> main` 的发布顺序：[`开发发布流程`](release-process.md)。

后续功能若改变以上行为，必须同时修改对应条目，不得只追加代码或测试。

## 设备授权与账号导出合同（v1.31.66）

- 账号导出必须在生成独立 session 前读取当前 Telegram authorization；导出客户端的 `app_version`、`device_model` 和 `system_version` 优先复用当前授权，缺失时才使用 `TelegramClientDeviceProfile` 的 ApiId 族回退值。
- 在线设备 DTO 的授权 `hash` 必须序列化为十进制字符串。Telegram 哈希是 64 位整数，Vue/JavaScript 不得用 `number` 保存或拼接该值。
- 踢出接口必须检查 Telegram 返回的业务 `success`；成功后允许前端先移除行并延迟刷新，避免 Telegram 授权列表传播延迟造成“已踢出但仍显示”。
- 验证至少包括：当前授权画像覆盖测试、长哈希 JSON 字符串测试、前端 86 个测试、`vue-tsc`、前端构建和 .NET Release 构建。回滚到 v1.31.65；无数据库迁移。
## Telegram 设备指纹合同

适用版本：包含 `Account.DeviceProfileKey` 与迁移 `20260818090000_AddAccountDeviceProfileKey` 的版本。

- `TelegramDeviceProfileCatalog` 是唯一画像解析入口；未知、停用或空 key 必须回退系统默认画像，不得在各服务中复制默认值。侧栏 `/device-profiles` 是设备画像独立入口，只展示画像目录和默认画像保存，不展示 Telegram API 状态；Telegram API 池必须保留在系统设置页，不再新增独立侧栏页。
- `TelegramApiProfilePool` 把启用中的内置官方 API 与自定义 API 配置合并成一个池子，并按权重轮询分配给新账号登录和不自带 API 的导入。`Telegram:ApiId`/`Telegram:ApiHash` 仅作旧版单 API 兼容；新版系统设置保存时应把它带入 `ApiProfiles` 并清空旧字段。设置接口必须通过 `telegram.officialApiEnabled`、`telegram.effectiveApiId`、`telegram.effectiveApiSource`、`telegram.officialApiId` 和 `telegram.hasUsableApi` 暴露有效状态，前端不得只用已写入的 `telegram.apiId/apiHash` 判断可用性。
- `TelegramClientPool`、Session 导入验证、账号导出和账号登录必须在创建 `WTelegram.Client` 前解析画像；手动登录页必须在发送验证码/生成二维码前提交 `deviceProfileKey`，后端需在临时登录状态中冻结该 key，登录成功后保存到账号。代理解析与画像解析相互独立，画像不得改变连接出口。
- 新登录/导入成功入库时保存画像 key；账号详情更新允许清空 key，表示跟随系统默认。更新画像不改写现有 Session，客户端缓存清理后在下一次创建客户端时生效。
- API 端点：`GET /api/panel/settings` 返回有效 Telegram API、来源字段、`officialApiEnabled` 与 `telegram.deviceProfiles`；`GET /api/panel/settings/device-profiles` 返回画像目录；`POST /api/panel/settings/telegram-api` 保存 API 池、内置官方 API 启用状态和默认画像；账号 `PUT /api/panel/accounts/{id}` 和登录/导入请求接受 `deviceProfileKey`。

验证：运行 .NET Release 构建和完整 Web 测试；运行前端 `vue-tsc`/build 与前端测试；手工检查系统设置中的 Telegram API 池、内置官方 API 顶部项、API 池轮询、设备指纹页面、手动登录设备指纹选择、账号详情清空/保存及新客户端创建。失败排查先检查迁移、画像 key、API 配置和本地配置文件权限。回滚需先备份数据库和 `appsettings.local.json`，再恢复旧程序；旧程序不会使用画像字段，但不应删除迁移历史。

## GitHub Pages 发布

已内置工作流：`.github/workflows/docs.yml`。

启用方式（只需要做一次）：

1) 仓库 Settings → Pages
2) Source 选择 **GitHub Actions**

之后每次合并到 `main`（且改动命中 `docs/**`/`mkdocs.yml` 等）会自动构建并发布。
