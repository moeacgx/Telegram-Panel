# Telegram Panel

[English](README.md) | [中文](README.zh-CN.md)

<img src="./docs/images/telegram-panel-banner.jpg" alt="Telegram Panel 多账号统一运营" width="100%" />

基于 **WTelegramClient** 的 Telegram 多账户管理面板，使用 **.NET 8 后端** 与 **Vue 3 管理后台** 构建。

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8.0">
  <img src="https://img.shields.io/badge/Vue-3-42B883?style=for-the-badge&logo=vuedotjs&logoColor=white" alt="Vue 3">
  <img src="https://img.shields.io/badge/Docker-Compose-2496ED?style=for-the-badge&logo=docker&logoColor=white" alt="Docker Compose">
  <img src="https://img.shields.io/badge/Powered%20by-WTelegramClient-333333?style=for-the-badge" alt="Powered by WTelegramClient">
</p>

<p align="center">
  <a href="https://t.me/zhanzhangck"><img src="https://img.shields.io/badge/Telegram-站长仓库-blue?logo=telegram" alt="Telegram 站长仓库"></a>
  <a href="https://t.me/vpsbbq"><img src="https://img.shields.io/badge/Telegram-NexHub_AI社区-blue?logo=telegram" alt="Telegram NexHub AI社区"></a>
</p>

<p align="center">
  📚 <b><a href="https://moeacgx.github.io/Telegram-Panel/">文档站</a></b> |
  🖼️ <b><a href="screenshot/">截图</a></b>
</p>

## 项目简介

Telegram Panel 用于在单个 Web 面板中统一管理和运营多个 Telegram 账号，重点覆盖账号生命周期管理、批量运营、频道/群组管理、自动化任务以及模块扩展能力。

## ✨ 当前功能

- 📥 **账号接入**：支持 Telethon / TData / StringSession 导入，Telethon / TData 导出，手机号验证码、二维码和 2FA 登录；导出独立 Session 会沿用当前 Telegram 授权的设备指纹。
- 🌐 **账号级代理**：支持 HTTP、SOCKS5、MTProxy、Resin 和受管 WARP，覆盖账号绑定、批量绑定、分类、使用状态筛选，以及出口 IP、地区、城市和 ISP 检测。
- 🔒 **安全首连**：导入和登录会在第一条 Telegram 请求前选择并冻结出口，避免先直连再切换 IP。
- 🛡️ **账号维护**：支持状态检测、瞬时连接恢复、安全废号清理、查看在线设备、按精确授权哈希踢出设备、踢出其他设备、二级密码与找回邮箱管理。
- 👥 **频道、群组和 Bot**：支持创建、同步、分类、邀请、管理员设置、公开化、退出、解散和链接导出。
- 🤖 **自动化**：支持立即 / 定时任务、暂停、编辑、重跑、数据字典、模板变量，以及可选的 OpenAI 兼容验证辅助。
- 🧩 **模块与 API**：支持安装 `.tpm` / `.zip` 扩展任务、API 和管理页面，并保留旧 Razor 页面兼容入口。

## 🐳 Docker 安装

环境要求：Docker Engine，或 Windows 上的 Docker Desktop + WSL2。

```bash
git clone https://github.com/moeacgx/Telegram-Panel
cd Telegram-Panel
cp .env.example .env
docker compose pull
docker compose up -d
```

访问 <http://localhost:5000>，使用初始账号登录：

- 用户名：`tgpanel`
- 密码：`tgpanel123`

首次登录后请修改初始密码。持久化数据位于 `./docker-data`，升级或迁移前请备份该目录。

代理、WARP 与 Resin 的配置细节不放在 README，见[代理管理与账号出口](docs/guides/proxy-management.md)和[账号导入](docs/guides/account-import.md)。

## 下载与更新

- Docker 镜像：`ghcr.io/moeacgx/telegram-panel:latest`
- Windows 安装包与 Linux 更新包：[最新 GitHub Release](https://github.com/moeacgx/Telegram-Panel/releases/latest)
- Docker 场景面板内更新：**左上角版本号 → 版本信息 → 检查更新 → 一键更新并重启**

更新现有部署前，请先阅读[更新升级](docs/getting-started/update.md)。

## 本地开发

安装 .NET 8 SDK，并使用仓库锁定的 pnpm 版本：

```bash
corepack enable
pnpm --dir frontend install --frozen-lockfile
pnpm --dir frontend run build
dotnet run --project src/TelegramPanel.Web
```

访问 <http://localhost:5000>。

## 文档入口

- [安装部署](docs/getting-started/installation.md)
- [账号导入](docs/guides/account-import.md)
- [代理管理与账号出口](docs/guides/proxy-management.md)
- [配置与数据目录](docs/reference/configuration.md)
- [模块开发](docs/developer/modules.md)
- [接口速查](docs/reference/api.md)
- [文档站](https://moeacgx.github.io/Telegram-Panel/)

## 截图

更多截图见：`screenshot/`

| | | |
|---|---|---|
| <img src="screenshot/Dashboard.png" width="300" /> | <img src="screenshot/account.png" width="300" /> | <img src="screenshot/Import account.png" width="300" /> |

## ⭐ Star History

[![Star History Chart](https://star-history.dera.page/svg?repos=moeacgx/Telegram-Panel&type=Date)](https://star-history.dera.page/#moeacgx/Telegram-Panel&Date)
