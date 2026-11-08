# 发布说明生成器

适用于统一生成流程合入后的发布工作区，也可用于整理历史 Release 正文。模板以 v1.31.90 为基准；使用 Python 3.12 或更新版本与 Git，无额外 Python 依赖。仓库必须包含目标 tag 及完整历史；浅克隆需先补齐历史。脚本只读取 Git 并写入指定输出文件，不会自动修改 GitHub。

## 命令接口

在仓库根目录运行：

```bash
python tools/release_notes.py --tag v1.31.90 --repository moeacgx/Telegram-Panel --output release-notes.md
```

| 参数 | 含义 |
| --- | --- |
| `--tag` | 目标版本 tag，使用 `vX.Y.Z` 或带 SemVer 预发布后缀的形式 |
| `--repository` | GitHub `owner/repo`，用于 compare 链接与镜像名；镜像路径统一小写 |
| `--previous-tag` | 可选；必须比目标版本低且是目标提交的可达标签，稳定目标不接受预发布基线 |
| `--summary-file` | 可选 UTF-8 摘要文件，覆盖从提交读取的摘要，可含“本次版本主要更新”二级标题或直接写三级分类 |
| `--upgrade-notes-file` | 可选 UTF-8 升级提示，保留在固定部署段中，不替换部署模板 |
| `--output` | 完整 Markdown 正文的输出路径 |

未指定上一版时自动选取可达的较低版本；首版没有 compare 链接。发布摘要的优先级和分类规则见[开发发布流程](../developer/release-process.md)。内部实现不提供应用 HTTP API，不改变数据库或用户配置。

## 修订已有正文

先备份线上标题和正文，人工核对摘要的完整性和版本适用范围。摘要中的验证描述必须有真实依据，不能把未执行的测试写成通过。更新摘要示例：

```markdown
## 本次版本主要更新

### 修复问题

- 修复旧 WARP 记录已完成迁移后仍无法勾选删除的问题。

### 验证

- 填写本版已经完成的验证及仍未验证的范围。
```

升级提醒单独写入 `upgrade-notes.md`，例如需要先切换账号和全局代理引用再删除旧记录。生成、预览并写回：

```bash
gh release view v1.31.89 --repo moeacgx/Telegram-Panel --json name,body > release-before.json
python tools/release_notes.py --tag v1.31.89 --repository moeacgx/Telegram-Panel --summary-file summary.md --upgrade-notes-file upgrade-notes.md --output release-notes.md
gh release edit v1.31.89 --repo moeacgx/Telegram-Panel --title v1.31.89 --notes-file release-notes.md
gh release view v1.31.89 --repo moeacgx/Telegram-Panel --json name,body
```

成功判据是线上标题等于 tag，正文与生成文件一致，且原有业务说明、适用的升级提醒、资产名称均保留。仅整理说明时不得修改 tag、安装包或镜像。

## 失败排查与回滚

- tag 无效或不可读取：确认完整历史、tag 名称及工作目录。
- 上一版不合法：检查版本顺序和提交祖先关系，不要使用其他分支上不可达的版本。
- 摘要为空：补充真实更新条目，不用空分类或版本号充当说明。
- 线上正文与输出不同：比较 `release-notes` 构建产物和发布后的编辑记录；GitHub 默认日志配置不控制本脚本输出。
- 修订需回滚：从 `release-before.json` 提取原始 `body` 到 UTF-8 文件，使用原始 `name` 和 `gh release edit --notes-file` 恢复，再读取确认；不重发版本。
