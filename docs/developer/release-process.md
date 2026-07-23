# 开发发布流程

本文档定义 Telegram Panel 从功能开发到主分支发布的唯一流程。目标是让 `dev` 成为可部署、可验收的集成环境，让 `main` 只接收已经完成云端验证的代码。

## 分支职责

- `main`：稳定主分支，生产镜像和正式文档从这里发布。
- `dev`：集成和云端测试分支，推送后构建 `ghcr.io/moeacgx/telegram-panel:dev-latest`。
- `codex/<purpose>`：临时功能分支。完成合并后删除本地分支、远端分支和对应临时 worktree。

不使用历史版本分支、临时 merge 分支或长期个人分支承载发布状态。

## 标准流程

### 1. 开发与文档

开始前记录当前分支和工作区状态。重要改动必须同步开发文档，具体门禁见 [文档维护](documentation.md) 和根目录 `AGENTS.md`。

提交前至少确认：

- 功能行为、API、配置和部署方式已经在对应文档中说明；
- 文档写明前置条件、验证步骤、失败排查和回滚方式；
- 测试覆盖新增行为和关键回归场景；
- `mkdocs.yml` 已包含新增文档页面。

### 2. 合并到 `dev`

功能分支完成本地验证后，推送到远端并通过 PR 合并到 `dev`。没有云端验收结果时，不得合并到 `main`。

当前仓库的 Docker 工作流 `.github/workflows/docker.yml` 在 `dev` 推送后会构建并推送：

```text
ghcr.io/moeacgx/telegram-panel:dev-latest
```

`dev` 默认只构建 `linux/amd64`，便于更快完成集成验证；正式 `main` 和 tag 构建多架构镜像。

### 3. 部署云端测试环境

在 GitHub Actions 手动运行 `Deploy Telegram Panel`，选择 `dev` 分支，镜像使用对应 `dev-latest` 或带 SHA 的不可变标签。工作流会：

1. 在云端 `/home/docker/Telegram-Panel` 拉取 `dev`；
2. 备份 `docker-data` 中的 SQLite 数据文件；
3. 拉取镜像并保留 `docker-compose.warp.yml` 等 override；
4. 重建 `telegram-panel` 容器；
5. 检查容器状态、最近日志、`/ui/dashboard` 和 `/api/panel/auth/me`。

入口脚本会比较镜像内的 `version.txt` 与持久化自更新目录 `/data/app-current/version.txt`：

- 镜像版本更高时，优先启动镜像版本，并将旧的持久化程序目录归档为 `/data/app-obsolete-*`；
- 持久化自更新版本更高时，继续优先使用已确认启动成功的自更新版本；
- 该比较同时适用于新版确认标记和旧版 `.telegram-panel-self-update` 标记，避免历史自更新目录永久遮住新镜像；
- 旧版本包没有 `version.txt` 时保持兼容行为，仍按启动确认标记选择目录。

可通过 `.env` 的 `TP_UPDATE_MODE` 明确选择 `auto`、`image` 或 `binary`。该策略同时注入入口脚本和应用配置，避免 UI 显示的更新方式与容器实际启动目录不一致。

因此，升级 Docker 镜像后如果页面仍显示旧版本，应先查看容器日志中的版本选择记录和 `/data/app-obsolete-*`，确认是否存在旧自更新目录残留。

部署完成不等于验收完成。验收时还要实际操作本次改动涉及的页面、API、后台任务或代理链路，并记录：

- `dev` 提交 SHA 和实际镜像标签；
- 容器状态、启动日志和关键错误日志检查结果；
- 页面/API 的成功结果及关键响应；
- 数据持久化、重启恢复和权限边界检查结果；
- 失败时使用的回滚镜像或上一个可用提交。

若改动涉及 WARP 或其他代理管理能力，还要验证协议、容器网络、端口、账号绑定和重启后的状态恢复；不能只以 HTTP 健康检查作为通过依据。

### 4. 合并到 `main`

只有以下条件全部满足时，才创建或合并 `dev -> main` 的 PR：

- 本地构建和测试通过；
- 文档门禁通过；
- Docker 镜像工作流成功；
- 云端部署成功；
- 本次功能验收有明确通过证据；
- 没有未处理的回滚、数据迁移或安全风险。

`main` 合并后会构建 `latest` 和多架构镜像；若需要正式版本，再按现有 Release 工作流创建 tag。正式发布不得反向替代 `dev` 验收。

### 5. 清理分支

合并确认后执行清理：

```powershell
git fetch --prune origin
git branch --merged dev
git branch --merged main
git push origin --delete <已合并的功能分支>
git branch -d <已合并的功能分支>
```

删除前逐个检查 worktree 和未推送提交。`main`、`dev`、当前正在使用的分支和包含唯一未合并提交的分支不得删除。临时 worktree 只能在确认没有用户改动后移除。

## 发布证据模板

```text
功能：
文档：
dev 提交：
镜像：
部署工作流：
容器状态：
健康检查：
功能验收：
回滚点：
main 合并：
分支清理：
```
