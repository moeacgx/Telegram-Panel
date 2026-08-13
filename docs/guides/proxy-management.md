# 代理管理与账号出口

Telegram Panel 按账号管理 Telegram 连接出口。导入、登录，以及后台任务和模块对账号
执行的 Telegram 操作都会复用账号当前路由，避免同一个账号先直连、后切换代理。

## 先区分面板出口和账号出口

代理管理页顶部显示的是**面板服务自身的公网出口**。代理列表和账号详情显示的是
对应代理或账号的出口，两者互不等价。

- 顶部显示“未使用 WARP”：只表示面板服务自身没有通过 Cloudflare WARP。
- WARP 代理行显示“WARP 已连接”：表示该独立受管 WARP 容器的出口检测成功。
- 出口地址包含冒号时通常是 IPv6。IPv6 同样是有效公网出口。
- 当前出口检测先使用 Cloudflare Trace 验证公网 IP，再按 IP 补充国家/地区、城市和 ISP。
  地理服务临时不可用时仍会保留已验证的 IP 和国家码，不会把代理误判为失败。

## 选择账号使用的出口

账号支持以下路由：

- **明确直连**：绕过账号代理和全局代理。
- **全局代理**：继承 `Telegram:Proxy` 配置。
- **已有代理**：绑定代理管理中的 HTTP、SOCKS5、MTProxy、Resin 或外部 WireGuard WARP。
- **外部 WireGuard WARP**：运营方在面板外运行 WireGuard/gost 等轻量出口，面板只保存
  它暴露出的 HTTP/SOCKS5 监听并绑定账号。
- **独立 WARP**：账号管理可按需创建并绑定受管 WARP 容器；账号导入和手动登录只会自动分配
  已有 WARP，不会按账号或登录会话创建新容器。

导入账号、手机号登录和二维码登录都会在第一条 Telegram 请求前要求选择路由。
选定后，验证码发送、二维码轮询、2FA 验证和 Session 建立会使用同一出口；失败时不会
静默回退到面板直连。

切换已入库账号的代理时，宿主会先停用账号并严格断开旧客户端，再提交新路由并重建
连接。模块和后台任务下一次按账号执行操作时会自动使用新出口。

账号列表的“代理”列以 `#代理编号` 作为主要标识，避免自动生成的 WARP 名称占用过多
空间；鼠标悬停在编号上仍可查看完整代理名称，下一行继续显示已检测到的出口 IP。编号
与“代理管理”列表中代理名称下方的 `#ID` 一致，可用于快速核对账号实际绑定的代理。

### 导入后的绑定规则

- 导入或登录选择**已有代理**后，账号会保存该代理的 `ProxyId`，并关闭全局继承；以后
  仍会长期使用这条专属代理，直到在账号管理中手动切换或解除。
- 选择**全局设置**时，账号保存为 `ProxyId=null`、`UseGlobalProxy=true`，以后会跟随
  全局代理的修改；选择**直连**则保存为 `ProxyId=null`、`UseGlobalProxy=false`。
- 账号有专属 `ProxyId` 时始终优先于全局代理。全局代理选择已有代理时只保存代理 ID，
  运行时从代理表读取最新的 WARP/Resin 参数，不复制过期凭据快照。

## 配置全局代理

在 **代理管理 → 全局代理** 中可以直接启用 HTTP、SOCKS5 或 MTProxy，也可以从已有的
普通代理、外部 WireGuard WARP、Resin 或受管 WARP 中选择。选择已有代理时保存的是代理引用，后续编辑该代理会对
继承全局的账号生效。
保存后面板会立即重载配置并清理 Telegram 客户端缓存；继承“全局设置”的账号会在下一次
连接时使用新出口，账号已绑定的独立代理和明确直连不受全局代理覆盖。配置缺失或无效时
会在连接 Telegram 前失败，不会回退为面板直连。

已保存的密码和 MTProxy Secret 不会回显；编辑时留空会保持原值，HTTP / SOCKS5 密码可
通过“清除已保存的密码”显式删除。停用只关闭全局代理开关并保留连接参数，方便稍后恢复。

## 添加和检测普通代理

在 **代理管理** 中添加代理，然后执行出口检测。支持：

- HTTP
- SOCKS5
- MTProxy
- 外部 WireGuard WARP HTTP 或 SOCKS5 监听
- Resin HTTP 或 SOCKS5 数据面

HTTP 和 SOCKS5 可以通过 Cloudflare Trace 检测公网出口。外部 WireGuard WARP 也使用
Cloudflare Trace，但必须返回 `warp=on` 或 `warp=plus` 才算检测成功。MTProxy 只服务 Telegram
MTProto，不能通过普通 HTTP 请求检测公网 IP。

代理列表支持按“使用中/未使用”和分类筛选；勾选多个代理后可以批量设置分类或删除。
批量删除会逐项执行并列出失败原因；仍被账号或全局代理使用的项目会保留，
其它可删除项目不受影响。使用中包括直接绑定账号，以及被全局代理引用的代理。

从 v1.31.44 起，启用的普通代理、外部 WireGuard WARP 和 Resin 默认每 5 分钟自动做一次轻量
健康巡检。巡检请求使用 `Proxy:Egress:ProbeUrl`（Docker 环境变量 `TP_PROXY_EGRESS_PROBE_URL`），
默认是 `https://208.67.222.222/`；它只确认代理 HTTP/SOCKS 链路能发起出站请求，不调用
Cloudflare Trace，也不会刷新 IP、国家/地区、城市或 ISP 快照。需要查看或更新出口元数据时，
继续使用页面上的手动“检测出口”，该操作才会访问 `Proxy:Egress:MetadataUrl`（默认
`https://cloudflare.com/cdn-cgi/trace`），外部 WireGuard WARP 仍要求 Trace 返回 `warp=on` 或
`warp=plus`。

可通过 `Proxy__Egress__Maintenance__Enabled=false` 回滚到仅手动检测；
`Proxy__Egress__Maintenance__IntervalMinutes` 可调整周期（默认 5 分钟）。成功判据是服务日志
不再每 5 分钟出现 Cloudflare Trace 请求，代理行“最近检测”持续更新，手动检测仍能刷新出口 IP
和 WARP 状态。失败时查看服务日志中的 `Proxy egress maintenance`，并先确认轻量探针 URL、代理地址、
认证、外部 WireGuard 监听和 Resin 控制面可达；若自定义探针不可用，改回默认 ProbeUrl 或关闭巡检。


## 外部 WireGuard WARP 多出口

这是当前版本支持的轻量多出口路径：面板不尝试在宿主机创建 WireGuard 接口、不写入
`wg-quick` 配置、不复制或改写 Cloudflare WARP 私钥，也不验证“只改 PrivateKey 就能生成
新 Cloudflare peer”这类假设。Cloudflare 官方 Linux 文档只说明 WARP 客户端可以把隧道协议
切换为 WireGuard，并未把复制配置改 key 声明为可由面板安全托管的生命周期接口；因此宿主
网络和 WARP 注册仍由运营方在面板外管理。

推荐拓扑是每个外部出口在宿主机或旁路容器中自行运行：

1. 运营方准备独立、有效的 WARP/WireGuard 出口，并为每个出口启动本地 HTTP 或 SOCKS5
   监听（例如 `127.0.0.1:1080`、`10.0.0.5:1081`）。
2. 确认监听地址能被 Telegram Panel 容器访问；容器内的 `127.0.0.1` 指向面板容器自身，
   访问宿主机监听时通常要使用 Docker 网络地址或 `host.docker.internal`。
3. 在 **代理管理 → 新增代理 → 外部 WireGuard WARP** 中填写协议、主机、端口和可选认证，
   或批量导入：

```text
wg-warp+socks5://user:password@host.docker.internal:1080
wg-warp+http://10.0.0.5:8080
```

保存后建议保持“保存后检测出口”。检测成功的判据是：代理行显示可用、存在公网出口 IP，
且 Cloudflare Trace 报告 WARP 已启用；未检测成功的外部 WireGuard WARP 端点不能绑定账号
或作为全局已有代理生效。成功后它会作为普通已有代理参与账号绑定、导入首次连接、账号列表
出口展示、分类筛选和每 5 分钟出口巡检，不会创建 Docker WARP 容器或数据卷。

故障排查按边界分工处理：面板只负责保存连接参数、发起 HTTP/SOCKS 握手、验证 WARP 出口和
绑定账号；`wg` 接口、路由表、gost/3proxy 进程、WARP 注册与私钥轮换由运营方负责。若检测失败，
先在面板容器内确认能连到监听地址，再检查外部代理是否真的经 WARP 出口访问
`https://www.cloudflare.com/cdn-cgi/trace`。需要回滚时，把账号切换到其它已有代理/全局设置/直连，
或删除对应外部 WireGuard WARP 代理记录；面板不会停止外部 WireGuard 或代理进程。

### 不支持的托管模式

当前服务边界只安全托管 Docker WARP 容器。直接管理宿主 WireGuard 需要 root 级网络权限、
路由表和防火墙改写，以及对 WARP 注册材料的生命周期保证；这些都超出当前面板服务权限，
所以不会实现为“复制配置并改 key”的一键托管功能。

## 启用独立 WARP

普通代理和 Resin 不需要 Docker Socket。只有需要面板创建独立 WARP 容器时，才叠加
受管 WARP 配置：

```bash
docker compose -f docker-compose.yml -f docker-compose.warp.yml up -d
```

`docker-compose.warp.yml` 会把 `/var/run/docker.sock` 挂入面板容器。该权限接近宿主机
`root`，只应在可信主机启用。

可以在 `.env` 设置：

```dotenv
# Compose 项目名不是 telegram-panel 时，改为面板所在的实际 Docker 网络
TP_WARP_DOCKER_NETWORK=telegram-panel_default

# 自动创建 WARP 的默认连接协议：http 或 socks5
TP_WARP_PROXY_PROTOCOL=http

# 受管 WARP 最大数量和单容器 Docker 创建模板；0 表示不设置，保持旧安装行为
TP_WARP_MAX_MANAGED_PROXY_COUNT=0
TP_WARP_CONTAINER_MEMORY_LIMIT_BYTES=0
TP_WARP_CONTAINER_CPU_LIMIT=0
TP_WARP_CONTAINER_PIDS_LIMIT=0
```

WARP 镜像中的 GOST 端口同时支持 HTTP 和 SOCKS5。默认协议决定登录和批量绑定自动创建
WARP 时宿主使用哪种握手；代理管理中的一键创建弹窗可以覆盖单次创建协议。账号导入
自动分配已有 WARP 时沿用代理记录自身的协议，不读取该创建默认值。

每个 WARP 都对应一个独立 Docker 容器和数据卷，并持续占用一定的服务器内存与 CPU。
`TP_WARP_MAX_MANAGED_PROXY_COUNT` 可限制面板可创建的受管 WARP 数量，达到上限时会在创建
Docker 卷或容器前失败。`TP_WARP_CONTAINER_MEMORY_LIMIT_BYTES`、`TP_WARP_CONTAINER_CPU_LIMIT`
（例如 `0.5`）和 `TP_WARP_CONTAINER_PIDS_LIMIT` 会映射到 Docker HostConfig 的 `Memory`、
`NanoCpus` 和 `PidsLimit`，只影响后续新建容器；值为 `0` 或留空时不写对应限制。

账号导入的“自动分配已有 WARP”只复用这些现有容器，并优先选择绑定账号较少的 WARP；
批量导入不会再按账号数量创建容器。没有健康候选项时导入会停止，需先在本页准备 WARP
或改选其他出口。成功标准是新建容器 inspect 能看到配置的资源限制，或达到数量上限时没有
新增代理记录和 Docker 资源；失败时检查 `.env` 是否由 Compose 注入、数值是否为正数，以及
宿主 Docker 版本是否支持对应 HostConfig 字段。回滚时把模板值改回 `0` 并重建面板容器；
已创建容器如需移除限制，需删除后重新创建。

默认 `container` 模式由 Docker 网络按容器名访问，不占用宿主机代理端口。若在其他
拓扑中把 `Proxy:Warp:ProxyHostMode` 配为 `published`，面板会从
`Proxy:Warp:HostPortStart`（默认 `42080`）开始递增寻找空闲端口。若检测通过后端口又在
Docker 创建或启动时被抢占，面板会删除失败的容器壳、保留数据卷，并继续尝试下一端口。

## 自动巡检与故障恢复

Docker 的 `unless-stopped` 只能处理容器进程退出，不能处理“容器仍显示 running，
但 WARP 隧道或 GOST 已经卡死”。面板因此还会执行出口级自动维护：

- 默认每 5 分钟检测所有期望启用的受管 WARP。
- 连续失败 2 次后重启原容器，保留 WARP 数据卷，并最多复测 6 次。
- 恢复失败后进入 30 分钟冷却，避免检测源抖动造成重启风暴。
- 重启前后释放绑定账号的 Telegram 客户端；客户端只能沿原 WARP 路由重建，代理不可用时
  会失败，不会回退为面板直连。
- 正在用于账号导入、手机号登录或二维码登录的 WARP（包括已有 WARP 和一键新建 WARP）
  会保持首次出口冻结；后台巡检、手动刷新、修改和删除都不会打断首次连接。
- 代理页每 30 秒更新维护状态，也可手动刷新单个或全部 WARP。

参考 tokens-pro 的“720 分钟定时刷新”也可以开启：

```dotenv
TP_WARP_AUTO_RECOVERY_ENABLED=true
TP_WARP_HEALTH_CHECK_INTERVAL_MINUTES=5
TP_WARP_FAILURE_THRESHOLD=2
TP_WARP_RECOVERY_COOLDOWN_MINUTES=30
TP_WARP_SCHEDULED_REFRESH_ENABLED=false
TP_WARP_SCHEDULED_REFRESH_INTERVAL_MINUTES=720
```

故障自愈默认开启；健康出口的定时强制重启默认关闭，因为重启可能更换账号出口 IP。
只有确实需要周期轮换时才把 `TP_WARP_SCHEDULED_REFRESH_ENABLED` 改为 `true`。

参考项目界面中的 `WARP_SLEEP=2` 是 WARP 镜像内部启动等待参数，`GOST_ARGS=-L :1080`
是代理监听参数；它们本身都不等于定时健康巡检。

修改 `.env` 后重新创建面板容器：

```bash
docker compose -f docker-compose.yml -f docker-compose.warp.yml up -d --force-recreate
```

## 对接 Resin 动态代理

先按 [Resin 中文文档](https://github.com/Resinat/Resin/blob/master/README.zh-CN.md)
部署网关，再在 **代理管理** 中新增 `Resin`：

- 主机和端口：Resin HTTP 或 SOCKS5 数据面地址。
- Proxy Token：保存到代理密码字段，只用于数据面认证。
- Platform：例如 `Default`。
- 管理地址和 Admin Token：用于检查控制面并回收粘性租约。

面板会为账号生成稳定身份。导入阶段使用临时身份验证出口，入库后通过
`inherit-lease` 把租约继承给正式账号身份。继承失败时账号会保持停用，避免正式连接
改用未经确认的出口。

Resin 提供粘性租约，但不保证节点故障后 IP 永远不变。页面展示的是最近一次成功检测
得到的出口快照。

## 模块不重复管理账号代理

模块对已入库账号执行 Telegram 操作时，应把 `accountId` 交给宿主账号服务。宿主客户端池
会自动解析账号路由并应用代理，模块不应再保存代理凭据或自行创建 `WTelegram.Client`。

模块自己的 `HttpClient`、第三方 API 或其它网络连接不会自动继承账号代理。如果这类请求
确实需要代理，应作为模块自己的独立网络能力设计。完整边界见
[模块开发文档](../developer/modules.md)。
