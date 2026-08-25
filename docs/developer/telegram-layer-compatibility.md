# WTelegram Layer 兼容性

适用范围：使用 `WTelegramClient 4.4.8`（Telegram Layer 228）的面板版本。

## 背景

Telegram 可能向部分既有会话返回相邻 Layer 的对象构造器。Issue #162 中的
`user#31774388` 和 `channel#1c32b11c` 属于 Layer 227；Layer 228 的 WTelegram
类型表默认只登记 `user#b1b8cc83` 与 `channel#d49f34c6`，因此会抛出
`Cannot find type for ctor`。

## 实现约束

`TelegramLayerCompatibility` 在 Web 启动入口执行，并由客户端池再次兜底，向
`TL.Layer.Table` 注册两条别名：

- `0x31774388` 映射到当前 `TL.User`；
- `0x1C32B11C` 映射到当前 `TL.Channel`。

这两个 Layer 的字段顺序一致，Layer 228 只在尾部新增受 flag 保护的可选字段，因此旧负载
可以由当前类型安全读取。不得将未知构造器、非相邻 Layer 或字段布局未经核对的构造器加入
该表；也不得为此降级 `WTelegramClient`。

启动入口在任何账号导入、登录、Session 转换和导出等直接创建 Telegram 客户端的路径前完成注册。
注册仅修改进程内类型表，不修改数据库、账号配置或 Session 文件。

## 验收与排查

升级并重启后，使用曾出现该错误的账号重新运行数据同步。成功判据是频道/群组读取完成，
日志和任务失败详情中不再出现这两个构造器的 `Cannot find type for ctor`。

若仍出现其他构造器编号，保留完整编号、WTelegram 版本和触发接口后反馈，不要泛化注册别名。
若同时出现 `AUTH_KEY_UNREGISTERED`、`SESSION_REVOKED` 等明确 Session 错误，按账号重新登录流程
处理，而不是将其视为 Layer 兼容问题。

## 回滚

回滚到不含该兼容层的应用版本只会移除进程内别名；数据库和 Session 文件均无需迁移或恢复。
