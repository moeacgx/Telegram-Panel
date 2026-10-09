# 共享 wgcf / WireGuard 运维工具

适用于计划随 v1.31.79 交付的 `tools/wgcf-warp`。这是面板外的可选运维工具：
一个共享容器运行多个 userspace wireproxy 进程，每份配置使用独立 WARP 注册材料和
SOCKS5 监听。面板通过已有 `wireguard_warp` 代理 API 检测出口并逐账号绑定。
它没有新增面板内的注册按钮，不替换已有官方 WARP 容器，也没有完成真实 Cloudflare
注册和 Telegram 业务验收；不能将模拟测试解释为可承载 100 个账号的证明。

## 前置条件

- Linux Docker Compose，amd64 或 arm64，面板已有外部 WireGuard WARP API（v1.31.78 基线）。
- 工具容器可直连 Cloudflare 注册 HTTPS API、WireGuard UDP 端点和 DNS。
- 面板与工具加入同一受信任 Docker 网络；工具不发布任何宿主机端口。
- 运营者自行阅读并接受 [Cloudflare 应用条款](https://www.cloudflare.com/application/terms/)。
  工具不会默认接受，以下部署和验证命令均不会自动注册。
- 若要自动绑定，准备当前面板管理员 Cookie。它只在内存中用于 API 请求，不写入配置档案。

wgcf 为非官方 CLI，注册可因上游协议、限流或地区网络变化失败。工具固定 wgcf 2.3.0 和
wireproxy 1.1.3，构建时按 `dependencies.json` 的 SHA256 校验官方发布资产。WARP+
订阅设备上限不能解释为普通 WARP 的注册配额；本工具不绑定 WARP+ license。

## 部署共享容器

在仓库根目录执行。若 Compose 网络名不同，先设置 `TP_WGCF_NETWORK`。

```bash
export TP_WGCF_NETWORK=telegram-panel_default
docker compose -f tools/wgcf-warp/compose.yml build
docker compose -f tools/wgcf-warp/compose.yml up -d
docker compose -f tools/wgcf-warp/compose.yml ps
```

容器名和网络别名均为 `tg-wgcf-warp`。镜像以 UID/GID 10001 运行，删除全部 capability、
启用只读根文件系统；不挂载 Docker Socket、TUN，不授予 NET_ADMIN，不改宿主路由。
持久卷只保存本工具档案，默认共享上限 512 MiB 内存、1 CPU、256 PIDs，可按实测调整
Compose。100 份配置是防误操作的硬上限，不是支持容量；一个进程仍消耗 CPU、内存和 UDP 状态。

## 注册、生成、启停

配置名按账号**数据库 ID**命名，例如账号 ID 123 对应 `account-123`，不是显示序号。
运营者接受条款后自行输入带 `--accept-tos` 的注册命令；不接受就不执行此步骤。

```bash
# 仅在运营者已阅读并接受条款后执行；每次只注册一个独立账号。
docker compose -f tools/wgcf-warp/compose.yml exec wgcf-warp python /app/warpctl.py register account-123 --accept-tos
docker compose -f tools/wgcf-warp/compose.yml exec wgcf-warp python /app/warpctl.py generate account-123 --port 18081
docker compose -f tools/wgcf-warp/compose.yml exec wgcf-warp python /app/warpctl.py start account-123
docker compose -f tools/wgcf-warp/compose.yml exec wgcf-warp python /app/warpctl.py status account-123
```

每份配置必须使用不同端口。`start` / `stop` 保存期望状态，由运行器异步执行；`status`
的 `runtime=listening` 仅表示本地进程和 TCP 监听，不代表 Cloudflare/WARP 已连通。
`egressVerified` 固定为 false，真实出口必须通过下面的面板检测。容器重启后恢复已启动档案。
配置生成会拒绝钩子、外部配置和按域名直连选项，所有目的流量均交由 WireGuard。
已启动过的档案需要先 `stop`，等待运行器确认本次请求并显示 `stopped` 才能重新生成；
快速连续执行 stop/generate/start 时，未确认的生成操作会被拒绝。

## 面板检测并逐账号绑定

在仓库外保存 JSON，例如 `/srv/tg-wgcf/panel-auth.json`：

```json
{"panelUrl":"https://panel.example.com","cookie":"TelegramPanel.Auth=替换为当前管理员Cookie"}
```

文件的 `panelUrl` 必须与命令完全一致。设置该文件属主为 UID 10001、权限 0400，父目录
避免公开读取。不要在命令参数、Git、工单或日志中粘贴 Cookie。叠加只读凭据挂载：

```bash
export TP_WGCF_PANEL_CREDENTIALS=/srv/tg-wgcf/panel-auth.json
docker compose -f tools/wgcf-warp/compose.yml -f tools/wgcf-warp/compose.panel.yml up -d
docker compose -f tools/wgcf-warp/compose.yml -f tools/wgcf-warp/compose.panel.yml exec wgcf-warp \
  python /app/warpctl.py bind --accounts 123 --panel-url https://panel.example.com \
  --credentials /run/secrets/panel-auth.json --proxy-host tg-wgcf-warp
```

如果只在可信 Docker 网络使用 `http://telegram-panel:5000` 等地址，必须显式加
`--allow-http`，且凭据文件也填写该地址；Cookie 会以明文经过该网络。端口以实际面板容器
配置为准。工具拒绝 HTTP 重定向，不读取系统代理环境变量，不把 Cookie 转发到其它来源。

`--accounts 123,124` 按顺序处理，单次最多 10 个；对应档案必须预先注册、生成和启动。
每项成功输出账号 ID 和代理 ID。后续项失败时非零退出，之前成功项保留，可原参数重试。
工具先读取账号真实 ID 和旧路由，创建外部 SOCKS5 代理，再调用面板出口检测；只有
`testStatus=ok`、有效出口 IP 和启用状态才通过绑定 API 提交，并回读确认。
原代理编号作为 `expectedProxyId` 传入，发现人工换绑后拒绝覆盖。

旧显式代理为受管 `kind=warp` 时工具拒绝迁移，因为面板当前绑定 API 可能自动清理被替换
且无人使用的旧 WARP。请先在面板中单独安排旧容器的迁移和备份。普通旧代理及本工具档案
不会自动删除，失败也不会改成直连。面板仍允许管理员手动把同一代理分给多个账号，工具
无法在 API 之外禁止这种操作；绑定前会拒绝发现已共享或已用作全局代理的新出口。

## 成功判据与未验证范围

首次只验收 1～3 个档案：分别确认注册身份不同、面板出口检测 WARP 开启、账号代理 ID 与
档案一致、Telegram 实际操作成功；再验证容器重启恢复、停止单档案时请求失败且不转直连，
记录宿主配置、内存、CPU、带宽和错误率后逐步增加。独立注册不保证不同公网 IP。

仓库自动测试使用 fake wgcf/wireproxy 和本地 HTTP 模拟服务器；镜像测试只校验真实
wireproxy 配置解析、不建隧道。真实注册、上游限流、外部 WARP Trace 和 Telegram 业务
验证需运营者接受条款后另行完成。只有这些通过后才能判定相应环境可用。

## 故障排查与回滚

- 注册失败但材料完整：原命令重试会复用材料，再执行 `generate`，不会再次注册。
- 注册超时/失败且材料不完整：工具标记结果不明确并拒绝重复注册；保留整个档案，人工核对
  上游设备。不要靠删文件无限重试，工具不声称能撤销上游注册。
- `generate` 失败：档案标记未生成，不允许启动；检查固定版本程序、网络和配置字段。
- `runtime=unknown`：检查共享容器；`backoff` 表示子进程退出，30 秒后重试。
  `failed` 表示该档案元数据无效或子进程无法创建，其它档案继续运行。
  `listening` 但面板检测失败时检查 UDP、DNS、Docker 网络和上游可用性。
- API 401/403：更换有效 Cookie；敏感原始响应和上游日志有意不输出。
- 代理创建响应丢失：原参数重试按持久化的唯一名称恢复已有记录；仍找不到时停止并人工核对，
  不自动重复创建。账号绑定响应丢失则回读当前绑定后恢复。
- 回滚先在面板把账号切回档案 `meta.json` 中 `previousProxyId` 对应代理或原全局模式；
  再执行下列停止命令。不要在账号仍绑定时删除持久卷。

```bash
docker compose -f tools/wgcf-warp/compose.yml exec wgcf-warp python /app/warpctl.py stop account-123
docker compose -f tools/wgcf-warp/compose.yml exec wgcf-warp python /app/warpctl.py status account-123
# 整体停用时保留持久卷和账户材料。
docker compose -f tools/wgcf-warp/compose.yml down
```

停止不会自动解除面板绑定。面板会继续指向停止的代理并报错，不回退直连。工具没有自动
删除远端 WARP 设备或清除私钥的命令；退出使用后由运营者按自己的留存和注销规则处理。
