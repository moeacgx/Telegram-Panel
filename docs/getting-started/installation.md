# 安装部署

本文档用于把 Telegram Panel 跑起来（推荐 Docker 远程镜像）。

## Docker 部署（推荐）

### 环境要求

- Docker（Windows 推荐 Docker Desktop + WSL2；Linux 直接安装 Docker Engine）

### 第一步：准备项目

```bash
git clone https://github.com/moeacgx/Telegram-Panel
cd Telegram-Panel
cp .env.example .env
```

### 第二步：选择镜像版本

默认是稳定版（无需改动）：

```bash
TP_IMAGE=ghcr.io/moeacgx/telegram-panel:latest
```

如果你要开发版，把 `.env` 里的 `TP_IMAGE` 改成：

```bash
TP_IMAGE=ghcr.io/moeacgx/telegram-panel:dev-latest
```

### 第三步：启动

```bash
docker compose pull
docker compose up -d
```

启动后访问：`http://localhost:5000`

### 默认后台账号（首次登录）

- 用户名：`tgpanel`
- 密码：`tgpanel123`

登录后到「修改密码」页面改掉即可。

### 可选配置：自定义 Telegram API 凭据

面板内置 Telegram 官方 Android API（ApiId `6`），系统设置里的 Telegram API 池默认把它放在最顶上并启用；不需要手工填写官方 API Hash。

如果需要自建 API、隔离额度或分散新账号，可到 https://my.telegram.org/apps 用任意一个 Telegram 账号申请 `api_id` / `api_hash`，然后在面板「系统设置」的 Telegram API 区块添加到 API 池。`api_hash` 是 **32 位十六进制字符串（0-9a-f）**，请不要填 Token/用户名/URL 等其它值。

### 导入或登录前先选择账号出口

导入账号、手机号登录和二维码登录会在第一条 Telegram 请求前要求选择代理出口、自动分配
已有 WARP、有效的全局代理或明确直连。导入和登录都只复用已准备好的 WARP，不会为每个账号
新建容器。系统不会先用面板公网 IP 登录，再补绑代理。

普通 HTTP、SOCKS5、MTProxy 和 Resin 可直接在 **代理管理** 中添加。需要面板一键创建
WARP 时，使用受管 WARP 叠加配置：

```bash
docker compose -f docker-compose.yml -f docker-compose.warp.yml up -d
```

该配置会挂载 Docker Socket，仅应在可信主机启用。完整说明见
[代理管理与账号出口](../guides/proxy-management.md)。

### 数据持久化（别乱删）

容器内所有持久化数据统一挂载到宿主机 `./docker-data`：

- 数据库：`./docker-data/telegram-panel.db`
- Sessions：`./docker-data/sessions/`
- 系统设置本地覆盖：`./docker-data/appsettings.local.json`
- 后台登录凭据文件：`./docker-data/admin_auth.json`
- 图片资产：`./docker-data/uploads/`

更多说明见：[配置与数据目录](../reference/configuration.md)。

### 常用命令

```bash
# 查看日志
docker compose logs -f

# 更新到当前 .env 指定的镜像版本
docker compose pull
docker compose up -d

# 重启 / 停止
docker compose restart
docker compose down
```

## 生产部署入口

- 反向代理（含 WebSocket）：见 [反向代理](../deployment/reverse-proxy.md)
- Bot Webhook（可选，生产推荐）：见 [Bot Webhook](../deployment/bot-webhook.md)

## 更新与常见问题

- 更新升级：见 [更新升级](update.md)
- 常见问题：见 [常见问题](faq.md)
- 如果出现“镜像已更新但页面还是旧版”，请看 [更新升级](update.md) 里的
  「常见现象：镜像更新了，页面还是旧版」与「远程镜像 与 本地构建：如何切换」。

## 下一步

- 账号导入：见 [账号导入（Zip / TData）](../guides/account-import.md)
- 新号必看：见 [防冻结指南](../guides/anti-freeze.md)
- 列表/批量能力依赖同步：见 [同步说明](../guides/sync.md)

## 本地开发运行（可选）

```bash
corepack enable
pnpm --dir frontend install --frozen-lockfile
pnpm --dir frontend run build
dotnet run --project src/TelegramPanel.Web
```
