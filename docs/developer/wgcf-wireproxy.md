# wgcf 共享运行器合同

适用于计划 v1.31.79 的独立 `tools/wgcf-warp`；部署和成功判据见
[运维步骤](../deployment/wgcf-wireproxy.md)。宿主 .NET、Vue、数据库和模块 ABI 不变。

## 生命周期与安全边界

- `warpctl.py` 只依赖 Python 3.11+ 标准库。Linux 容器为生产运行目标，Windows 仅运行
  不涉及 POSIX 子进程信号的测试。所有上游 CLI 使用参数数组启动，禁用 shell 和标准输入。
- `register` 仅在显式 `--accept-tos` 时发起注册。首次调用先持久化尝试标志；中断后存在完整
  TOML 则复用，否则进入人工核对状态。每 profile 独立 CLI 进程，不把 wgcf 的全局认证客户端
  当成多账户 SDK。上游进程单次最多 45 秒，超时杀死并回收子进程。
- `generate` 只接受 Interface/Peer 白名单字段，要求注册私钥一致和 IPv4/IPv6 全路由，
  使用固定 wireproxy `-n` 检查。禁止配置钩子、WGConfig、TunnelDomains 和环境变量插值。
- 目录 0700，文件 0600，进程 umask 0077；同目录 fsync + replace 原子落盘，拒绝符号链接
  和路径穿越。共享数据卷只允许本工具访问，不视为恶意本机管理员间的隔离边界。
- CLI 操作互斥；supervisor 使用独立进程锁，读取原子元数据，不受注册网络等待阻塞。
  每个 profile 一个 wireproxy 子进程，停止和退出时 terminate/wait/kill；异常退出冷却 30 秒。
  单份元数据损坏或进程无法创建只标记该档案 `failed`，不终止其它正常出口。
  进程日志不持久化，不把配置、Token、Cookie、私钥或上游错误文本转发到 API/终端。
- 用户态 WireGuard 只在进程内部处理流量。共享容器不持有 NET_ADMIN、TUN 或 Docker
  Socket，不建立宿主接口；共享容器故障会影响其全部出口，不能宣称进程级高可用。

## 面板集成与恢复

凭据 JSON 包含精确 `panelUrl` 和管理员 Cookie，Linux 限制为 0400/0600。拒绝来源重定向，
默认 HTTPS；明文内网需显式选择。每个请求设置 15 秒连接/读超时和总响应期限，最后一次
阻塞读取可能再占用最多 15 秒；响应限制 4 MiB，不保存原始响应。

绑定流程：分页读取账号列表 → 记录旧代理 ID/原全局模式 → 持久化唯一代理名称和创建意图 →
创建 `wireguard_warp/socks5` → 面板出口检测 → CAS 绑定 → 回读账号代理。API 返回 HTTP 200
仍需检查 `success=1/failed=0`，不得仅靠状态码宣布成功。网络超时可能发生在提交之后，
重试通过唯一名称和当前账号绑定恢复；找不到已尝试创建的代理时停止，避免重复资源。

旧受管 WARP 会触发宿主自动清理，因此工具拒绝自动迁移这类账号。新出口已有其它账号或
全局用途时拒绝绑定。已有宿主 API 只对 `ExpectedProxyId` 比较，不区分并发从直连到全局
（二者 ID 都为 0）的瞬间切换；运维者应避免同时编辑待迁移账号。跨工具/面板 UI 的并发
一对一占用不是数据库唯一约束，本工具不宣称提供这种保证。

档案保留旧代理引用用于回滚；不自动删除面板代理、WARP 注册或密钥。上游注册失败之后的
清理语义不是事务回滚，缺少材料时必须人工核对，不允许自动循环创建。

## 验证

```bash
timeout 60s python -m unittest discover -s tools/wgcf-warp/tests -v
mkdocs build --strict
```

`.github/workflows/wgcf-tool.yml` 在 Linux 验证注册部分成功、超时、配置注入、端口冲突、
独立密码、敏感输出、子进程启停/重启恢复，以及本地模拟面板的出口失败、绑定失败、丢失
创建响应、人工换绑和重定向。镜像构建校验固定二进制 SHA256，再执行真实 wireproxy
`configtest`，不注册或建立真实 WireGuard 隧道。

云端验收必须额外执行运维文档中的小样本注册和 Telegram 路径；未执行时 PR/Release
明确写“未实网注册验收”，不能据此关闭完整实网功能诉求。没有数据库迁移，回滚停止独立
容器即可；先恢复账号路由并保留卷。
