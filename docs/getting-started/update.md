# 更新升级（Docker 部署）

> 更新前建议先备份：`./docker-data/telegram-panel.db` 与 `./docker-data/`（尤其是重要账号的 sessions）。

也可以在 **系统设置 → 存储桶备份** 配置对象存储上传 URL，更新前点击“立即备份”把数据库、配置、后台凭据和 sessions 上传到 S3/R2/OSS/COS 等存储桶。

Cloudflare R2 预签名 `PUT` URL 若返回 `Missing x-amz-content-sha256`，升级到包含该修复的版本后重试；面板会对 R2 上传自动发送 `x-amz-content-sha256: UNSIGNED-PAYLOAD`。

<a id="bucket-backup-restore"></a>

## 从存储桶备份恢复

当前后台面板只提供“立即备份”，不提供运行中导入备份恢复入口。恢复必须停机执行，避免 SQLite 连接、WAL/SHM 和账号 Session 文件在运行中被覆盖。

以下示例假设 Docker 项目目录下的 `docker-data/` 挂载到容器 `/data`；如果你的部署使用自定义数据目录，请把命令中的 `docker-data` 替换为实际持久化目录。

1. 从 S3/R2/OSS/COS 下载备份 ZIP 到服务器，先确认里面至少包含 `telegram-panel.db` 和 `sessions/`：

   ```bash
   unzip -l /path/to/telegram-panel-backup.zip | sed -n '1,80p'
   ```

2. 停止面板，并在恢复前保留当前数据快照：

   ```bash
   docker compose stop telegram-panel
   ts=$(date +%Y%m%d%H%M%S)
   cp -a docker-data "docker-data.before-restore-$ts"
   mkdir -p "/tmp/telegram-panel-restore-$ts"
   unzip /path/to/telegram-panel-backup.zip -d "/tmp/telegram-panel-restore-$ts"
   ```

3. 用备份内容替换数据文件。恢复数据库时必须同时处理 WAL/SHM：先删除当前 WAL/SHM，再按备份包实际存在的文件复制，避免旧 WAL 套到新数据库上。

   ```bash
   restore="/tmp/telegram-panel-restore-$ts"

   rm -f docker-data/telegram-panel.db docker-data/telegram-panel.db-wal docker-data/telegram-panel.db-shm
   cp "$restore/telegram-panel.db" docker-data/telegram-panel.db
   [ -f "$restore/telegram-panel.db-wal" ] && cp "$restore/telegram-panel.db-wal" docker-data/telegram-panel.db-wal
   [ -f "$restore/telegram-panel.db-shm" ] && cp "$restore/telegram-panel.db-shm" docker-data/telegram-panel.db-shm

   [ -f "$restore/appsettings.local.json" ] && cp "$restore/appsettings.local.json" docker-data/appsettings.local.json
   [ -f "$restore/admin_auth.json" ] && cp "$restore/admin_auth.json" docker-data/admin_auth.json
   if [ -d "$restore/sessions" ]; then
     rm -rf docker-data/sessions
     cp -a "$restore/sessions" docker-data/sessions
   fi
   ```

4. 启动并验收：

   ```bash
   docker compose start telegram-panel
   docker compose logs --tail=80 telegram-panel
   ```

   成功判据：容器正常启动，`/api/panel/auth/me` 可访问，后台能登录，账号列表存在，抽查账号 Session 可读取。若恢复后异常，停止容器，把 `docker-data.before-restore-$ts` 改回 `docker-data` 后再启动，即可回到恢复前状态。

备份 ZIP 包含账号 Session 和后台凭据。下载、解压和临时目录都应只允许管理员访问；恢复完成后按需删除服务器上的 ZIP 和 `/tmp/telegram-panel-restore-*` 临时目录。
## 设备指纹与 Telegram API 验收

包含设备指纹功能的版本会执行 `20260818090000_AddAccountDeviceProfileKey`，只向 `Accounts` 增加可空的 `DeviceProfileKey` 文本列，不改写现有 Session。升级前备份 `docker-data/telegram-panel.db` 和 `docker-data/appsettings.local.json`。

启动后验收：容器状态为 running；`/api/panel/auth/me` 返回 200；侧栏保留“设备指纹”入口，Telegram API 回到“系统设置”页；系统设置里的 Telegram API 池第一项是可启停的内置官方 API；新登录/导入会在启用项中按权重轮询；设备指纹页只显示画像目录并能保存默认画像；手动登录页在发送验证码/生成二维码前可选择“本次登录设备指纹”；`GET /api/panel/settings` 返回 `telegram.officialApiEnabled=true` 与 `telegram.effectiveApiSource=built_in_official`；账号详情能保存和清空单账号画像；导入页能提交所选画像。若迁移失败，保留容器日志和数据库备份，不要手工删列或删除 `__EFMigrationsHistory`；先停止容器并恢复升级前快照，再回滚到旧镜像。

官方 API 作为 API 池顶层项参与轮询；自定义 API 池按权重排在其后，已有账号保存的 ApiId/ApiHash 会优先用于该账号后续操作。关闭内置官方 API 且没有启用自定义项时，新账号登录和不带 API 的导入会提示 Telegram API 不可用。

## 更新策略

Docker 部署支持通过项目根目录 `.env` 的 `TP_UPDATE_MODE` 切换更新方式：

- `auto`（默认）：镜像版本和面板二进制版本都可用，启动时优先使用版本更高且已确认成功的程序；如果旧自更新包没有 `version.txt` 而镜像有版本号，则归档旧包并使用镜像；
- `image`：只运行 Docker 镜像内的 `/app`，适合统一由 CI/CD、Watchtower 或人工 `docker compose pull` 发布；
- `binary`：优先运行面板一键更新落地到 `/data/app-current` 的二进制，适合不想因镜像更新覆盖临时版本的场景。

修改 `.env` 后必须重建容器使入口策略生效：

```bash
TP_UPDATE_MODE=image
docker compose up -d --force-recreate
```

更新策略只决定程序来源，不会自动替用户执行 Docker 编排。镜像模式的实际发布仍使用 `TP_IMAGE`、`docker compose pull` 和 `docker compose up -d`。

`auto` 会在启动时读取 `/app/version.txt` 和 `/data/app-current/version.txt`。旧版更新包可能没有后一个文件；此时只要镜像有版本号，就会把旧目录移动到 `/data/app-obsolete-*` 后启动 `/app`。如果镜像和持久化目录都没有版本文件，才按已确认标记维持旧兼容行为。

> **重要：** 如果当前账号列表已经在旧版一键更新后变空，先不要再次点击“一键更新”。
> `v1.31.31` 及更早版本的旧更新器可能在切换目录前删除原有 `app-previous`，
> 而有效旧库可能就在其中。请先停止容器并同时备份宿主机 `docker-data` 与容器内 `/app`：

```bash
docker compose stop telegram-panel
cp -a docker-data "docker-data.backup-$(date +%s)"
docker cp telegram-panel:/app "./container-app.backup-$(date +%s)"
docker compose start telegram-panel
```

> `v1.31.32` 会恢复仍然存在的有效旧库，但无法重新生成已被旧更新器或人工操作删除的文件。

## 方式一：面板内一键更新（推荐先用）

入口：`左上角版本号 -> 版本信息弹窗 -> 一键更新并重启`

说明：

- 该方式基于 GitHub Release 更新包（`linux-x64/linux-arm64 zip`）
- 会自动匹配架构并部署到 `/data/app-current`
- 适合快速更新业务版本（无需手动执行命令）
- 数据库、后台凭据和 Session 默认统一保存到 `/data`，不会随程序目录轮换
- 从旧版本升级时，如果 `/data` 中目标库没有业务数据，首次启动会从 `/app`、`app-previous*` 或同一持久目录优先选择仍含账号的有效旧库；多个同类快照按数据库和 WAL 的最近写入时间选择，恢复前会备份目标，且不会删除来源
- 首次迁移完成后会在持久目录分别写入数据库、后台凭据和 Session 的 `.storage-*-migration-v1*.complete` 标记；后续启动不再从旧程序目录回灌已迁移的数据，避免用户已删除的数据被旧快照重新恢复
- 旧快照被占用、无权限或发生临时 IO 错误时会终止本次启动且不写完成标记，不会静默退回更旧快照；解除占用或权限问题后重新启动即可重试
- 新版本只有在服务真正启动成功后才会被确认；未确认便退出时会自动归档失败版本并回退 `app-previous`，首次更新无备份时回退镜像内 `/app`

### 更新后账号或登录凭据异常

如果更新后账号列表为空，先不要重新登录或覆盖任何文件，检查持久化目录：

```bash
docker exec telegram-panel sh -lc 'ls -l /data/telegram-panel.db /data/telegram_panel.db /data/admin_auth.json 2>/dev/null; find /data/sessions -maxdepth 1 -type f | head'
```

当前版本会在启动日志中打印实际使用的数据库、凭据和 Session 路径。若自定义部署把这些路径指向 `/app` 或 `/data/app-current`，一键更新会主动阻止，需先改到挂载卷（通常是 `/data`）再重试。

## 方式二：更新 Docker 镜像（建议定期执行）

在项目目录下执行：

```bash
docker compose pull
docker compose up -d
```

适用场景：

- 更新基础镜像层（运行时/系统依赖/安全补丁）
- `.env` 的 `TP_IMAGE` 改为新 tag 后切换到指定镜像版本

## 常见现象：镜像更新了，页面还是旧版

先检查当前程序实际运行目录：

```bash
docker exec telegram-panel sh -lc 'readlink /proc/1/cwd'
```

如果输出是 `/data/app-current`，说明当前在运行「面板一键更新」落地的版本，而不是镜像内 `/app` 版本。

若使用 `auto` 仍显示旧版本，先查看入口日志和版本文件：

```bash
docker logs --tail 120 telegram-panel | grep telegram-panel-entrypoint
docker exec telegram-panel sh -lc 'cat /app/version.txt 2>/dev/null; printf "\n-- current --\n"; cat /data/app-current/version.txt 2>/dev/null || true'
```

镜像有版本号而 `app-current/version.txt` 不存在时，重启容器应归档旧目录并使用 `/app`；部署脚本会通过 `/api/panel/auth/me` 校验这一结果。

### 切回“手动镜像更新”模式（推荐）

```bash
cd /home/docker/Telegram-Panel

docker compose down
mv docker-data/app-current docker-data/app-current.bak-$(date +%s)

docker compose pull
docker compose up -d --force-recreate
```

再次确认：

```bash
docker exec telegram-panel sh -lc 'readlink /proc/1/cwd'
```

应输出 `/app`。

## 远程镜像 与 本地构建：如何切换

### A. 远程镜像 -> 本地构建镜像

1. 把 `.env` 里的镜像改为本地标签（示例）：

```bash
TP_IMAGE=telegram-panel:local
```

2. 在项目根目录构建本地镜像：

```bash
docker build -t telegram-panel:local .
```

3. 以本地镜像重建容器（避免拉取远端）：

```bash
docker compose up -d --pull never --force-recreate
```

### B. 本地构建镜像 -> 远程镜像（latest/dev-latest/tag）

1. 把 `.env` 里的 `TP_IMAGE` 改回 GHCR 镜像，例如：

```bash
TP_IMAGE=ghcr.io/moeacgx/telegram-panel:dev-latest
```

2. 拉取并重建：

```bash
docker compose pull
docker compose up -d --force-recreate
```

### C. 校验当前容器到底跑的是哪个镜像

```bash
docker inspect telegram-panel --format '{{.Config.Image}}'
docker exec telegram-panel sh -lc 'readlink /proc/1/cwd'
```

## 从源码部署的用户（可选）

如果你不是用 GHCR 远程镜像，而是本地构建镜像部署，可使用：

```bash
git pull --rebase
docker compose up -d --build
```

## 更新出错：`git pull` 提示本地修改会被覆盖

典型报错：

```
error: Your local changes to the following files would be overwritten by merge:
        docker-compose.yml
Please commit your changes or stash them before you merge.
Aborting
```

原因：你本地改过 `docker-compose.yml`，导致更新时 Git 不允许直接覆盖（仅源码更新路径会遇到）。

推荐做法：尽量不要直接改 `docker-compose.yml`：

- Webhook 等部署差异：用 `.env`（参考 `.env.example`）
- 功能开关/参数：用面板「系统设置」保存到 `./docker-data/appsettings.local.json`（见 [配置与数据目录](../reference/configuration.md)）

处理方式（二选一）：

1) 放弃本地修改（最快、推荐）

```bash
git restore docker-compose.yml
git pull --rebase
docker compose up -d
```

2) 保留本地修改（自己承担后续合并成本）

```bash
git stash push -m "local docker-compose" -- docker-compose.yml
git pull --rebase
git stash pop
docker compose up -d
```

如果 `git stash pop` 出现冲突，按提示手动合并 `docker-compose.yml` 后再继续。

## Release 更新内容

GitHub Release 会自动生成完整变更记录，并在 Release 正文保留“本次版本主要更新”“部署与更新”“应用内更新资产”等固定段落。面板左上角版本弹窗会读取最新 Release Notes 并展示；如果页面没有显示更新内容，先点击“立即检查”，再打开对应 GitHub Release 页面确认正文是否为空。

成功判据是 Release 页面不为空，面板版本弹窗能看到“Release Notes”。若自动生成内容缺失，检查 `.github/workflows/release.yml` 中 `generate_release_notes: true` 是否仍启用，以及发布 tag 是否包含合并提交。回滚只需恢复旧 release workflow，不影响运行时数据。

