# 隔离验收部署

适用版本：v1.31.91 的 Bot 首次启动修复及其验收配置。本文是可审阅的执行方案，
不代表容器、登录或 Telegram 场景已经验收。发布顺序见[开发发布流程](../developer/release-process.md)。

## 环境与授权前置条件

- 明确授权的隔离 Linux Docker 主机或引擎，至少 2 vCPU、2 GiB 内存和 10 GiB 可写空间。
  10 GiB 是预留容量，不是配置实施的磁盘配额。
- Docker Compose v2，支持长格式 bind 的 `create_host_path: false`。
- 数据目录固定为 `/srv/telegram-panel-acceptance/issue190-v1.31.91/docker-data`。
  在授权主机上由操作者创建专属空目录；不得复制生产数据库、Session、`.env` 或管理员文件。
- 独立管理员凭据通过此实例的初始化流程配置，保存在隔离数据目录。
  按[后台认证安全](../developer/admin-auth-security.md)完成初始化和登录验证。
- 测试账号、Bot、无历史深链目标、普通群和屏蔽/解除屏蔽动作均需维护者明确授权。
  不从生产复用账号或会话，不自动创建 Bot，不向未授权目标发送消息。

现有 `Deploy Telegram Panel` workflow 使用生产 secrets，会修改生产目录。
即使选择 dev 分支，也不能作为本方案的执行入口。缺少上述资源时记录
`runtime_acceptance_blocked` 及最小缺口，保留已完成的代码和本地验证。

## 配置与启动

独立配置在仓库 `docker/acceptance/issue190.compose.yml`，证据模板在同目录的
`issue190.evidence-template.json`。获取待验收 dev 的准确 SHA、成功的 Docker run ID 和
该 run 输出的镜像摘要；不要把标签当前指向作为历史 SHA 的证据。

将独立 Compose 文件放到隔离主机的专属验收目录，按已核对的摘要设置进程环境变量
`TP_ACCEPTANCE_IMAGE`，格式为 `ghcr.io/moeacgx/telegram-panel@sha256:` 加 64 位摘要。
先运行只读解析并逐项检查，符合要求后才执行 pull 和 up：

```bash
docker compose --env-file /dev/null -f issue190.compose.yml config --format json
docker compose --env-file /dev/null -f issue190.compose.yml pull
docker compose --env-file /dev/null -f issue190.compose.yml up -d
docker compose --env-file /dev/null -f issue190.compose.yml ps
```

不要增加其他 `-f` 文件，不读取生产 `.env`。解析结果应只有一个服务，容器名与项目名均为
`telegram-panel-acceptance-issue190-1-31-91`；端口仅 `127.0.0.1:18091:5000`，
只有上述专属目录挂载到 `/data`，不挂载 Docker Socket，重启策略为 `no`。
配置限制 2 CPU、2 GiB 内存，日志按 10 MiB × 5 滚动，并关闭 Webhook 和出口巡检。

首次启动前确认目录为空；复用时检查 `/data/update-mode.txt` 不存在或内容为 `image`。
该文件优先于环境变量。核对容器实际镜像摘要、启动日志中的程序目录、镜像
`/app/version.txt` 和认证接口实际版本，排除旧 `/data/app-current` 遮蔽镜像。
配置模式不等于运行程序来源证明。

浏览器通过已授权的安全隧道访问回环端口，不为验收开放公网监听或扩大生产反代权限。
登录密码、Cookie、Telegram Session 和 Token 不得进入证据或报告；日志保存前先脱敏。

## 验收与成功判据

复制证据模板到本次运行的记录目录，填写准确 SHA、镜像摘要、环境身份和授权引用。
每个场景分别写 `passed`、`failed`、`not_run` 或 `blocked` 并关联脱敏证据。
不能执行的场景保留未验证状态；所有适用门禁通过后才将总状态记为 `passed`。

1. 记录容器状态与启动日志，检查 `/healthz`、`/ui/dashboard` 以及页面引用的 JS/CSS。
   可使用仓库 `tools/check-spa-assets.py`；不能只看页面 HTTP 200。
2. 未登录的 `/api/panel/auth/me` 应为 `authenticated=false`、`authEnabled=true`；
   独立管理员登录后应为 `authenticated=true`，并核对实际版本与镜像一致。
3. 对无私聊历史的 Bot 执行 `user_chat_active`，确认先成功启动一次，再发送正文。
   同一执行及新执行再次发送均不重复启动；重启实例后已有历史目标也不重复启动。
4. 使用另一组无历史账号/Bot 验证 `?start=...` 参数被保留，正文仅在启动成功后发送。
   已有历史目标不会再次启动，不能用它证明深链启动参数生效。
5. 在授权范围内验证历史检查或启动失败会阻止正文；明确屏蔽错误会暂停当前执行
   对该账号/Bot 的后续发送，不自动解除屏蔽。手动解除屏蔽后的新执行重新按历史判断。
   网络超时等不确定 RPC 结果不能当作服务端没有启动的证明。
6. 两个并发任务对同一无历史账号/Bot 不重复启动；不同账号/Bot 独立判断。
   普通群组沿原有发送路径，不触发 Bot 启动。只对已授权目标运行这些有实际副作用的检查。

详细行为边界见[模块开发](../developer/modules.md)中的持续活跃任务合同和
[API 参考](../reference/api.md)。若无法稳定触发历史失败或启动失败，分别记录
`not_run` 或 `blocked`，不能从无错误日志推定通过。

## 失败排查与回滚

- `config` 因变量缺失失败：填入已经核对的镜像摘要，不回退浮动标签。
- bind 目录不存在：核对目标主机与授权，再创建专属目录；不要改挂生产目录。
- 容器无法启动：检查资源、端口占用、目录权限和脱敏日志，不通过扩大宿主权限排障。
- 版本不一致：核对镜像摘要、启动程序目录与 `update-mode.txt`；保留证据，不覆盖旧数据。
- 没有隔离 Docker 引擎：静态解析只能证明配置可解析，不能替代容器、认证和业务验收。

回滚时先对本独立文件执行 `docker compose --env-file /dev/null -f issue190.compose.yml stop`，
保留数据和脱敏日志；将 `TP_ACCEPTANCE_IMAGE` 改为上一已验收镜像的固定摘要后按需重建。
v1.31.90 仅是候选基线，不代表已在该环境验收。记录回滚后的实际版本及检查结果。
不执行 `down -v`、不删除测试目录，不触碰生产数据。配置和文档本身不触发部署，
撤回本次配置提交不会停止已经运行的容器，运行实例仍须按上述步骤处理。
