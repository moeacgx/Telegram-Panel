# 代理管理与账号出口

Telegram Panel 按账号管理 Telegram 连接出口。导入、登录，以及后台任务和模块对账号
执行的 Telegram 操作都会复用账号当前路由，避免同一个账号先直连、后切换代理。

## 先区分面板出口和账号出口

代理管理页顶部显示的是**面板服务自身的公网出口**。代理列表和账号详情显示的是
对应代理或账号的出口，两者互不等价。

- 顶部显示“未使用 WARP”：只表示面板服务自身没有通过 Cloudflare WARP。
- WARP 代理行显示“WARP 已连接”：表示该轻量出口的 WARP 检测成功。
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
- **独立 WARP**：v1.31.81 起，一键创建、登录、导入及逐账号绑定统一使用主容器内的轻量
  wgcf＋wireproxy。选择“创建一对一 WARP”并勾选条款后，在首次验证前创建独占出口。

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
普通代理、外部 WireGuard WARP 或 Resin 中选择。内置轻量 WARP 是单账号专属出口，不能
作为全局代理；旧容器 WARP 在 v1.31.82 起不再支持。选择已有代理时保存的是代理引用，后续编辑该代理会对
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

计划 v1.31.79 附带可选的 [共享 WireGuard 运维工具](../deployment/wgcf-wireproxy.md)，
运营者可在明确接受 Cloudflare 条款后，通过命令注册独立 WARP 材料、在一个共享容器
运行多个用户态 WireGuard 出口，并调用既有面板 API 检测和绑定。该工具仍属于面板外
运维通道，没有新增面板内注册页面；真实注册和 Telegram 路径需要另行验收。

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

当前服务托管用户态 wireproxy 进程，已移除旧 Docker WARP 容器管理。直接管理宿主 WireGuard 需要 root 级网络权限、
路由表和防火墙改写，以及对 WARP 注册材料的生命周期保证；这些都超出当前面板服务权限，
所以不会实现为“复制配置并改 key”的一键托管功能。

## 在统一列表管理 WARP（v1.31.82）

升级 Linux Docker 镜像后，“一键创建 WARP”直接创建轻量出口。登录、导入及账号绑定的
“创建一对一 WARP”也使用同一运行器，固定 SOCKS5，无需为每个出口创建 Docker 容器。
每个出口仍有独立注册材料、配置、端口与进程，独立注册不保证不同公网 IP。
创建前明确勾选条款，成功判据为本地监听、WARP 检测成功与出口 IP；账号绑定后还需真实
Telegram 验证。失败先核对镜像依赖和网络并保留注册材料。
完整步骤见 [轻量 WARP 部署](../deployment/wgcf-wireproxy.md)。

新建后直接查看下方代理列表，不再显示单独的轻量 WARP 列表。创建中和失败档案即使尚未
生成代理编号也会显示，完成后合并到同一行。该行提供启动、停止、恢复、检测和账号绑定；
期望启停、监听状态及 WARP 检测分别展示，监听成功不代表公网出口检测已经通过。

每份轻量出口最多绑定一个账号。导入的“自动分配已有 WARP”只选择已就绪、检测成功且
无人使用的轻量出口，池为空时会明确失败。已绑定出口不可直接停止；先为账号切换其它
可用路由，再停止原出口。普通代理的编辑、删除和批量操作不会绕过轻量运行器的保护。

## 旧容器升级与故障处理

v1.31.82 已删除旧 WARP 状态、刷新和自动维护入口，也不再加载旧 WARP Compose 配置或
Docker Socket。历史 `kind=warp` 记录保留用于迁移识别，但不能继续承载账号或全局路由；
残留引用会阻止连接，不会自动直连。

仍使用旧容器时，先在升级前为每个账号建立独立轻量出口并验证 Telegram 只读资料获取。
确认全部账号已迁移、旧全局引用和直接绑定均清空后，再删除旧代理与容器。备份、逐账号
验收和回滚步骤见 [轻量 WARP 部署](../deployment/wgcf-wireproxy.md)。回滚面板至 v1.31.81
应保留新路由和注册材料，不能恢复指向已删除容器的旧数据库。

轻量出口失效时，在对应代理行执行“检测”确认出口状态，必要时使用“恢复”。先检查
运行器依赖、持久目录权限、UDP/DNS 网络与上游状态；不要删除注册材料或反复创建新出口。
进程异常退出由运行器处理，不能把旧容器的定时重启或巡检参数套用到轻量出口。

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
