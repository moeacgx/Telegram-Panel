# 数据库说明（简版）

默认使用 SQLite（Docker 下持久化到 `./docker-data/telegram-panel.db`）。

本页只列出核心表的“概念与用途”，避免把 README 写得太劝退；具体字段以 `src/TelegramPanel.Data/Migrations/` 为准。

## 核心表

- `Accounts`：账号信息、分类、最近状态检测结果缓存和用户可见账号编号 `DisplayNumber` 等

- `Channels`：频道信息（主要是账号创建的频道）与分组/展示字段
- `Groups`：群组信息（主要是账号创建的群组）
- `Bots` / `BotChannels`：机器人与其管理的频道（如果启用机器人管理）
- `BatchTasks`：批量与常驻任务；`OwnerModuleId` 固化任务所有者，`ExecutionKind` 为 `batch|persistent`，状态包含 `pending/running/pausing/paused/completed/failed/canceled`，`Name` 保存用户可读名称
- `TaskLogs`：任务日志（用于任务中心展示与排障）


`DisplayNumber` 自 v1.31.57 起存在唯一索引。迁移会按既有 `Id` 顺序把旧账号回填为 `1..N`；
之后保存新账号时由 `AppDbContext` 在事务保存前分配当前最小可用正整数，因此删除账号后编号
允许复用。接口和任务配置仍以内部 `Id` 做关联，`DisplayNumber` 只用于后台展示、搜索和人工
填写账号范围。成功判据是迁移后 `Accounts.DisplayNumber` 全部大于 0 且唯一；失败时先检查
迁移日志和唯一索引冲突。回滚到旧版会删除该列，回滚前不要把外部自动化只绑定到显示编号。

当前开发版迁移会把已有 `BatchTasks` 回填为 `OwnerModuleId=host.legacy`、`ExecutionKind=batch`，并增加 `RuntimePhase`、`RuntimeMessage`、`HeartbeatAtUtc`、`RequiresAttention` 保存宿主级运行提醒。新模块任务创建时必须固化真实模块 ID 与执行通道，调度器不根据当前清单临时推导。成功判据是升级后旧任务仍由批任务通道执行，新建常驻任务保留真实所有者，宿主暂停提醒在重启后仍可查询。失败时检查 `IX_BatchTasks_ExecutionKind_Status`、模块任务类型冲突诊断和迁移日志。回滚前先暂停并删除常驻任务并备份数据库；迁移 Down 会删除这些新列。

## 常见问题

### Docker 下数据库/Session 在哪？

统一在 `./docker-data`：

- `./docker-data/telegram-panel.db`
- `./docker-data/sessions/`

### 为什么刷新页面任务还在跑？

批量任务由后台服务从数据库拉取并执行，前端只是提交任务与展示进度（见 `BatchTasks`/`TaskLogs`）。
