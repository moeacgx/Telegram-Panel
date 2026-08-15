# 数据库说明（简版）

默认使用 SQLite（Docker 下持久化到 `./docker-data/telegram-panel.db`）。

本页只列出核心表的“概念与用途”，避免把 README 写得太劝退；具体字段以 `src/TelegramPanel.Data/Migrations/` 为准。

## 核心表

- `Accounts`：账号信息、分类、最近状态检测结果缓存等
- `Channels`：频道信息（主要是账号创建的频道）与分组/展示字段
- `Groups`：群组信息（主要是账号创建的群组）
- `Bots` / `BotChannels`：机器人与其管理的频道（如果启用机器人管理）
- `BatchTasks`：批量任务（pending/running/completed/failed），`Name` 可保存计划任务触发后的用户可读名称
- `TaskLogs`：任务日志（用于任务中心展示与排障）

## 常见问题

### Docker 下数据库/Session 在哪？

统一在 `./docker-data`：

- `./docker-data/telegram-panel.db`
- `./docker-data/sessions/`

### 为什么刷新页面任务还在跑？

批量任务由后台服务从数据库拉取并执行，前端只是提交任务与展示进度（见 `BatchTasks`/`TaskLogs`）。
