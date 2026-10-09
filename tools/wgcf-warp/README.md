# 共享 WireGuard 运维工具

一个无网络特权的共享容器运行多个 userspace wireproxy 进程，独立生成 WARP 注册材料，
通过面板现有外部 WireGuard WARP API 检测并绑定账号。

- [部署、条款选择、验收与回滚](../../docs/deployment/wgcf-wireproxy.md)
- [生命周期、API 与失败恢复合同](../../docs/developer/wgcf-wireproxy.md)
- `python warpctl.py --help` 查看命令；生产运行目标为 Linux Compose。
- `python -m unittest discover -s tools/wgcf-warp/tests -v` 从仓库根目录运行测试，
  设置外层最长 60 秒。测试仅使用替身和本地模拟 API。

默认不注册、不同意第三方条款、不开放公网端口、不接管宿主路由、不修改面板主容器依赖。
注册需运营者明确传入 `--accept-tos`。首次运行前阅读完整部署文档。
