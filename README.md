# Telegram Panel - 多账户管理面板

基于 **WTelegramClient** 的 Telegram 多账户管理面板（.NET 8 / Blazor Server），用于批量管理账号、统计与管理频道/群组、执行批量任务。

## 社区

- TG 频道：https://t.me/zhanzhangck
- 站长交流群：https://t.me/vpsbbq

## 功能亮点

- 📥 **多账号批量导入/登录**：支持 Session/压缩包导入；支持手机号验证码登录与 2FA 密码
- 🔁 **账号维度一键切换操作**：选择不同账号创建频道/群组、查看与管理账号创建的数据
- 👥 **批量运营能力**：批量邀请成员/机器人、批量设置管理员、导出链接等高频操作
- 🧩 **模块化扩展能力**：任务 / API / UI 可安装扩展（内置示例：外部 API 踢人/封禁；更多示例见 `docs/modules.md`）
- 🔐 **二级密码（2FA）与找回邮箱**：支持单个/批量修改二级密码；支持绑定/换绑 2FA 找回邮箱（验证码确认）
- 🧯 **忘记二级密码可申请重置**：支持单个或批量向 Telegram 提交“忘记密码重置”申请（通常等待 7 天后可重新设置）
- 🪪 **账号资料管理**：支持单账号编辑昵称/Bio/用户名/头像；支持批量修改昵称（自动追加手机号后 4 位便于区分）与批量修改 Bio
- 🤖 **Bot 频道管理**：用于管理“频道创建人不在系统中”的频道（把 Bot 设为管理员即可纳入管理），支持批量导出链接、批量设置管理员（踢人能力可扩展）


## 🧊 防冻结指南（新号必看）

Telegram 对新号/风控号的限制比较敏感；本项目提供了很多“批量/高频”能力，请务必谨慎使用。

- ⚠️ **新号切记：登录面板后不要进行任何操作！！！至少养 24 小时再创建频道/群、批量邀请/加管理员等敏感操作**
- ✅ 养号完成后：先从少量、低频操作开始，逐步增加频率（宁可慢点，也别一上来就批量）
- ⛔ 如果账号出现限制/冻结迹象：建议先停用该账号，等待恢复后再继续操作
- 📧 **重要账号务必绑定 2FA 找回邮箱**：尤其是接码手机号的账号，存在“官方客户端也可能突然掉登录”的情况；若未绑定邮箱，掉线后可能无法找回（账号就丢了）。建议准备一个稳定的域名邮箱，把重要账号都绑定到同一域名邮箱体系，便于后续通过邮箱验证码重新登录。非重要账号可酌情忽略。

## 截图

> 仓库自带后台截图：`screenshot/`

<details>
<summary>点击展开/收起截图</summary>

| | | |
|---|---|---|
| <img src="screenshot/Dashboard.png" width="300" /> | <img src="screenshot/account.png" width="300" /> | <img src="screenshot/equipment.png" width="300" /> |
| <img src="screenshot/Import account.png" width="300" /> | <img src="screenshot/Login with mobile phone number.png" width="300" /> | <img src="screenshot/System notification.png" width="300" /> |
| <img src="screenshot/Create channel.png" width="300" /> | <img src="screenshot/Invite users in batches.png" width="300" /> | <img src="screenshot/Set up administrators in batches.png" width="300" /> |

</details>

## 🐳 Docker 一键部署（推荐）

面向小白：**`git clone` → `docker compose up` → 浏览器打开 → 登录改密码 → 配置 ApiId/ApiHash**。

### 环境要求

- Docker（Windows 推荐 Docker Desktop + WSL2；Linux 直接装 Docker Engine）

### 启动

```bash
git clone https://github.com/moeacgx/Telegram-Panel
cd Telegram-Panel
docker compose up -d --build
```

启动后访问：`http://localhost:5000`

### 默认后台账号（首次登录）

- 用户名：`admin`
- 密码：`admin123`

登录后到「修改密码」页面改掉即可。

### 必做配置：Telegram API 凭据

到 https://my.telegram.org/apps **用任意一个 Telegram 账号申请一次** `api_id` / `api_hash`，然后在面板「系统设置」里保存即可。

> 说明：**不需要每个账号都申请**，全站共用这一对 `api_id` / `api_hash` 就能工作。

### 数据持久化（别乱删）

容器内所有持久化数据统一挂载到宿主机 `./docker-data`：

- 数据库：`./docker-data/telegram-panel.db`
- Sessions：`./docker-data/sessions/`
- 系统设置本地覆盖：`./docker-data/appsettings.local.json`
- 后台登录凭据文件：`./docker-data/admin_auth.json`

### 更新升级

#### 🔄 正常更新流程

当仓库有新功能或 Bug 修复时，执行以下命令更新：

```bash
# 进入项目目录
cd Telegram-Panel

# 拉取最新代码
git pull origin main

# 重新构建并启动
docker compose down
docker compose up -d --build
```

#### ⚠️ 遇到更新冲突怎么办？

如果执行 `git pull` 时提示冲突或报错，说明本地有未提交的修改与远程代码产生了冲突。

**方案一：保留本地修改（推荐）**

```bash
# 暂存本地修改
git stash

# 拉取最新代码
git pull origin main

# 恢复本地修改（可能需要手动解决冲突）
git stash pop
```

**方案二：放弃本地修改，强制同步远程（适合小白）**

```bash
# 备份重要数据（数据库、Session 文件等在 docker-data/ 目录，不会被删除）
# 如果本地有未提交的代码修改，执行以下命令会丢失！

# 获取远程最新代码
git fetch origin

# 强制重置到远程版本
git reset --hard origin/main

# 重新构建
docker compose down
docker compose up -d --build
```

> **注意**：`git reset --hard` 会**丢弃所有本地未提交的代码修改**，但不会影响 `docker-data/` 目录里的数据库和 Session 文件，这些数据依然安全保留。

#### 🆘 Docker 编译报错？

如果更新后 Docker 构建失败，提示找不到某个类或文件：

```bash
# 确认代码版本是否正确
git log --oneline -1

# 应该显示最新的提交记录，如果不是，执行强制更新：
git fetch origin
git reset --hard origin/main

# 清理 Docker 缓存后重新构建
docker compose down
docker system prune -af
docker compose build --no-cache
docker compose up -d
```

## 🌐 反向代理一条龙（可选）

Blazor Server 需要 WebSocket（`/_blazor`），反代必须支持 `Upgrade`。

说明：项目已兼容部分“默认反代不透传 Host/Proto”导致的登录跳转问题（不会再跳到 `http://localhost/...`），但 WebSocket 与部分场景仍建议把 `X-Forwarded-*` 头透传完整。

Nginx 示例（完整说明见 `docs/reverse-proxy.md`）：

```nginx
location / {
  proxy_pass http://127.0.0.1:5000;
  proxy_http_version 1.1;
  proxy_set_header Upgrade $http_upgrade;
  proxy_set_header Connection "Upgrade";
  proxy_set_header Host $host;
  proxy_set_header X-Forwarded-Host $host;
  proxy_set_header X-Forwarded-Proto $scheme;
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
}
```

## 本地开发运行（可选）

```bash
dotnet run --project src/TelegramPanel.Web
```

## 🧩 模块扩展（可选）

项目已支持“可安装/可卸载”的模块扩展（任务 / API / UI），用于后续按需扩展能力、按场景自由定制开发自己的模块，而不是把功能写死在核心里。

面板入口：

- 「模块管理」：安装/启用/停用/卸载模块（通常需重启生效）
- 「API 管理」：基于已启用的模块，创建对应的外部 API 配置项（`X-API-Key` 鉴权）
- 「任务中心」：基于已启用的模块，动态展示任务类型与分类

内置扩展只是示例（可按需启用/停用，也可自行开发/上传模块）：

- **外部 API：踢人/封禁**：`POST /api/kick`（配置入口：面板左侧菜单「API 管理」）
- **模块打包脚本（可选）**：`powershell tools/package-module.ps1 -Project <csproj> -Manifest <manifest.json>`，产物默认输出到 `artifacts/modules/`

更多说明见：`docs/modules.md`（模块目录结构、manifest、任务/API/UI 扩展点）。

## 详细文档

- `docs/README.md`（索引）
- `docs/import.md`（压缩包批量导入结构）
- `docs/sync.md`（同步说明 + 自动同步）
- `docs/reverse-proxy.md`（Nginx/Caddy 反代，含 WebSocket）
- `docs/api.md`（接口速查）
- `docs/database.md`（数据库/表结构说明）
- `docs/advanced.md`（配置项/数据目录/后台任务等）
- `docs/modules.md`（模块系统：可安装/可卸载/依赖/回滚）
