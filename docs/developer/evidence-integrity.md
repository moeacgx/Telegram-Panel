# 工程证据完整性

工程报告能被 UTF-8 解码，不代表中文、SHA 或状态字段保存正确。生成者必须把本轮新增内容与可信原文、结构化源数据逐项核对，保留失败证据后再更新状态。

本规范由根目录 `LESSONS.md` 中的 TP-RETRO-001 路由到此。原始证据由维护方私有账本按经验 ID 追溯，公开文档只保留规则与脱敏结论。

## 适用范围与前置条件

- **适用版本**：流程规范，从包含本规范的 `dev` 提交起生效，与应用版本号无关。
- **适用产物**：Agent 和维护者生成的报告、账本、状态 JSON、PR/Issue/Release 正文，以及跨 shell 传递的中文内容。
- **前置条件**：Python 3.8+、可写的任务留证目录、可信原文字符串、机器字段对应的结构化 JSON 源。文档构建依赖见仓库 `requirements-docs.txt`；涉及三方合并守卫时需要 Git 2.40+。
- **权限边界**：沿用现有共享锁、反馈期限、生产授权和[开发发布流程](release-process.md)。本规范不修复采集器、不改变应用功能、API、配置或数据库格式，也不替代既有事项的人工决定。

只验证本轮新增或明确更正的内容，不批量重写历史。原报告损坏时新增更正记录，关联旧记录与可信源；不得根据上下文猜测缺失 SHA、覆盖原件或宣布根因已经消除。

## 写入与传输规则

1. 中文正文优先直接写入 UTF-8 文件，再交给 `--body-file`、`--content-file` 等文件参数；有结构化参数的工具直接使用结构化参数。Python 文本读写显式使用 `encoding="utf-8"`，二进制回读必须与 `text.encode("utf-8")` 逐字节相等。
2. 含反引号、反斜杠、美元符号的正文不放进 PowerShell 双引号字符串或可展开 here-string；使用不展开的单引号 here-string、带引号的 heredoc（`<<'EOF'`），或程序直接写文件。正文不能成为 `printf` 的格式串，也不能交给 `echo -e`、`$'…'` 或未经审查的反转义处理。Windows PowerShell 的管道默认编码尤其需要核对，不能用控制台显示正常证明落盘正常。
3. 可信原文必须来自损坏前的生成内容或已验证数据源；“与已经变成问号的字符串一致”不算成功。至少提供一个预期必然出现、非空且含汉字的哨兵。哨兵缺失时停止，哨兵为空或不含汉字属于调用合同错误。
4. 除制表、换行、回车外，拒绝 U+0000–001F、U+007F–009F 和 U+FFFD。源码中的故障示例使用 `chr(7)` 等写法，不在文档中放入真实控制字符。
5. 脱敏与内容完整性分别核验：正确编码的私有目录、附件地址、账户、主机或凭据仍不能进入公开报告。内部证据留在授权的任务附件中，公开内容只保留必要结论与可公开的代码引用。

例如，程序中的原始字符串 `r"C:\new\tab 与 \a151"` 包含三个字面反斜杠；保存后仍须是三个反斜杠，不能变成换行、制表或 BEL。SHA 的 Markdown 反引号也必须保留，不能被 shell 当作转义前缀。

## 机器字段合同

由 JSON 源生成机器字段块，格式为每行 ``- <label>：`<value>` ``，label 使用 ASCII 小写标识。调用方提供 `{label: (JSON 路径, 格式)}`，字段映射不能为空。不要手工抄写字段值。

| 格式 | 源值要求 |
| --- | --- |
| `sha` | 严格匹配 40 位小写十六进制；缺位、大写或控制字符均拒绝 |
| `run_id` | 正整数十进制，示例限制为 1–20 位 |
| `utc_time` | `YYYY-MM-DDTHH:MM:SSZ`，且能解析为真实日期时间 |
| `status` | GitHub conclusion 枚举：`success`、`failure`、`cancelled`、`skipped`、`timed_out`、`action_required`、`neutral`、`stale` |

源字段缺失、类型不是字符串或整数、格式不合法都不能继续。报告字段按 label 与源值逐一精确比较；缺失、重复、未映射字段均拒绝。正确 SHA 出现在其他字段或正文中，不能补偿 `head` 字段错误。

正文中任何完整 40 位十六进制串也必须来自映射的 SHA 源值。**残缺的 39 位串无法由这项正文扫描识别，因此机器字段块是唯一权威来源**；不要在自由正文另写机器结论。校验通过不证明 JSON 源本身真实，源的来源与抓取时间仍须可追溯。

## 持锁与原子更新

修改已有状态文件时，调用方先按维护流程的既有规则取得共享锁：原子创建锁目录并写本轮 owner，占用则记录 `skipped_busy`，不抢锁。必须在同一持锁区间内读取旧值、计算 `expected_old_sha256`、生成候选、调用写入函数并核验结果。函数只核对 owner，不申请、不释放锁；释放也只能由持有者按既有规则执行。

新建独占、唯一名称的报告可传 `expected_old_sha256=None`、`lock=None`。覆盖已有历史报告不属于该接口的正常用法；既有状态文件更新和新增更正报告要分别处理。

每次调用按以下顺序留证和更新：

1. 独占创建 `<target>.attempts/<UTC微秒时间>-<pid>-<随机值>/`，先保存候选原字节与源 JSON。非字符串输入保存其表示形式；检查失败也能追溯输入。
2. 若调用生成命令，硬限时 60 秒，保存原始 stdout、stderr 和退出码；非零、超时或非 UTF-8 输出都拒绝。
3. 生成完成后检查锁与目标当前哈希，校验源字段、中文哨兵、控制字符和字段映射。
4. 同目录独占创建临时文件，写入、`flush`、`fsync`，按字节回读并与可信原文比较。
5. 替换前再次检查锁与目标哈希，然后 `os.replace`；替换后再次读目标并比较。失败临时文件保留为 `rejected-candidate.bin`，不能混入已成功状态。
6. 写 `result.json`，包含阶段、原因、退出码、owner、预期旧哈希与目标前后哈希。成功为 `saved`/0；规则拒绝为 2，非预期异常为 3 并留 traceback，目标被其他写入者改动或替换后被覆盖为 4。

原子替换前的检查与替换之间仍有短暂窗口，**正确性依赖全部写入者遵守同一把锁**。哈希核对不能阻止绕过锁的写入者；发现此类写入时不得报 saved，不强行恢复旧值覆盖他人内容，应保留双方证据并交维护负责人处理。磁盘、权限故障导致留证无法写入时，调用本身失败，不能伪造完整的 `result.json`。

## 可执行参考示例

以下代码仅作为文档示例，不是已部署的采集器。将这一完整 Python 代码块保存为 `evidence_write.py` 即可导入；验收必须从最终提交的文档提取，不能另写一个相似实现代替。`encoding` 的非 UTF-8 值、`_replace`、`_before_publish` 只供矩阵注入故障，实际调用不得传入；正常写入使用默认 UTF-8。

<!-- evidence-write:start -->
```python
# 原型 v3：仅用于验证计划合同可执行，不进入仓库
import datetime, hashlib, json, os, re, subprocess, sys, traceback, uuid

CJK = re.compile(r"[㐀-䶿一-鿿豈-﫿]")
# 报告中的“机器字段”块：每行 `- <label>：`<value>``，label 为 ASCII 小写
FIELD_LINE = re.compile(r"^- ([a-z][a-z0-9_]*)：`([^`\n]*)`$", re.M)
SHA_TOKEN = re.compile(r"(?<![0-9A-Fa-f])[0-9A-Fa-f]{40}(?![0-9A-Fa-f])")
FORMATS = {
    "sha": re.compile(r"[0-9a-f]{40}"),
    "run_id": re.compile(r"[1-9][0-9]{0,19}"),
    "utc_time": re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z"),
    "status": re.compile(r"success|failure|cancelled|skipped|timed_out|action_required|neutral|stale"),
}


class Rejected(Exception):
    def __init__(self, stage, reason):
        super().__init__(f"{stage}: {reason}")
        self.stage, self.reason = stage, reason


def bad_ctrl(t):
    return [hex(ord(c)) for c in t if (ord(c) < 32 and c not in "\t\n\r") or 0x7F <= ord(c) <= 0x9F]


def json_get(obj, path):
    for k in path.split("."):
        if not isinstance(obj, dict) or k not in obj:
            raise Rejected("source", f"源 JSON 缺少字段 {path}")
        obj = obj[k]
    return obj


def check_source(source, field_map):
    """field_map: {报告 label: (源 JSON 路径, 格式)}，返回 {label: 期望字符串}。"""
    if not field_map:
        raise Rejected("contract", "字段映射为空")
    want = {}
    for label, (path, fmt) in field_map.items():
        v = json_get(source, path)
        if isinstance(v, bool) or not isinstance(v, (str, int)):
            raise Rejected("source", f"源字段 {path} 类型不是 str/int: {v!r}")
        v = str(v)
        if not FORMATS[fmt].fullmatch(v):
            raise Rejected("source", f"源字段 {path} 不符合 {fmt}: {v!r}")
        if fmt == "utc_time":
            datetime.datetime.strptime(v, "%Y-%m-%dT%H:%M:%SZ")
        want[label] = v
    return want


def check_text(text, sentinels, want, sha_labels):
    if not isinstance(text, str):
        raise Rejected("input", "正文不是 str")
    if not sentinels or any(not CJK.search(s) for s in sentinels):
        raise Rejected("contract", "哨兵为空或不含汉字")
    miss = [s for s in sentinels if s not in text]
    if miss:
        raise Rejected("sentinel", f"缺少中文哨兵 {miss}")
    c = bad_ctrl(text)
    if c:
        raise Rejected("control", f"意外控制字符 {c}")
    if "\ufffd" in text:
        raise Rejected("control", "含 U+FFFD")
    got = {}
    for label, value in FIELD_LINE.findall(text):
        if label in got:
            raise Rejected("field", f"机器字段 {label} 重复")
        got[label] = value
    for label, v in want.items():
        if label not in got:
            raise Rejected("field", f"机器字段 {label} 缺失")
        if got[label] != v:
            raise Rejected("field", f"机器字段 {label} 与源值不一致: {got[label]!r} != {v!r}")
    extra = sorted(set(got) - set(want))
    if extra:
        raise Rejected("field", f"机器字段块含未映射字段 {extra}")
    allowed = {want[l] for l in sha_labels}
    stray = sorted({t for t in SHA_TOKEN.findall(text) if t not in allowed})
    if stray:
        raise Rejected("field", f"正文含不来自源字段的 40 位十六进制串 {stray}")


def sha256_or_none(path):
    try:
        with open(path, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()
    except FileNotFoundError:
        return None


def new_evidence_dir(target):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    d = f"{target}.attempts{os.sep}{stamp}-{os.getpid()}-{uuid.uuid4().hex[:8]}"
    os.makedirs(d)  # 目录名唯一，exist_ok=False：从不覆盖既有留证
    return d


def put(d, name, data):
    with open(os.path.join(d, name), "xb") as f:  # x：拒绝覆盖
        f.write(data if isinstance(data, bytes) else data.encode("utf-8"))
        f.flush(); os.fsync(f.fileno())


def check_lock(lock):
    """沿用既有共享锁：调用方已用 os.mkdir 取得 active.lock 并写入 owner.json。
    这里只核对锁仍由本轮持有，不申请、不释放、不抢锁。"""
    lock_dir, owner = lock
    try:
        with open(os.path.join(lock_dir, "owner.json"), encoding="utf-8") as f:
            current = json.load(f)
    except FileNotFoundError:
        raise Rejected("lock", "未持有共享锁：owner.json 不存在")
    if current != owner:
        raise Rejected("lock", f"共享锁不属于本轮：{current!r}")


def check_target(target, expected_old_sha256, when):
    now = sha256_or_none(target)
    if now != expected_old_sha256:
        raise Rejected("concurrent", f"{when}目标 sha256 {now} 不等于预期 {expected_old_sha256}")


def guarded_write(target, text, sentinels, source, field_map, expected_old_sha256, lock,
                  encoding="utf-8", producer=None, _replace=os.replace, _before_publish=None):
    """先建留证目录并落盘输入，再做任何检查；任何失败都保留证据且目标不变。
    lock=(active.lock 目录, 本轮 owner dict)；只有新建文件（expected_old_sha256 为 None）可传 None。"""
    before = sha256_or_none(target)
    ev = new_evidence_dir(target)
    put(ev, "source.json", json.dumps(source, ensure_ascii=False, indent=2) if source is not None else "null")
    if isinstance(text, str):
        put(ev, "candidate.txt", text.encode("utf-8", "surrogatepass"))
    else:
        put(ev, "candidate.repr", repr(text))
    result = {"ok": False, "stage": None, "reason": None, "target_sha256_before": before,
              "expected_old_sha256": expected_old_sha256, "lock_owner": lock[1] if lock else None}
    tmp = None
    try:
        if producer is not None:  # 生成命令：原样保存 stdout/stderr/退出码后再判断
            try:
                p = subprocess.run(producer, capture_output=True, timeout=60)
                put(ev, "producer.stdout", p.stdout); put(ev, "producer.stderr", p.stderr)
                result["producer_exit"] = p.returncode
            except subprocess.TimeoutExpired as e:
                put(ev, "producer.stdout", e.stdout or b""); put(ev, "producer.stderr", e.stderr or b"")
                raise Rejected("producer", "生成命令超时 60 秒")
            if p.returncode != 0:
                raise Rejected("producer", f"生成命令退出码 {p.returncode}")
            text = p.stdout.decode("utf-8")  # 严格解码，失败进入 except
            put(ev, "candidate-from-producer.txt", p.stdout)
        if lock is None and expected_old_sha256 is not None:
            raise Rejected("contract", "修改已有文件必须持有共享锁")
        if lock is not None:
            check_lock(lock)
        check_target(target, expected_old_sha256, "生成后")  # 生成命令期间目标被改也在这里拦下
        want = check_source(source, field_map)
        check_text(text, sentinels, want, [l for l, (_, f) in field_map.items() if f == "sha"])
        tdir = os.path.dirname(os.path.abspath(target))
        tmp = os.path.join(tdir, f".tmp-{uuid.uuid4().hex}")
        with open(tmp, "xb") as f:
            f.write(text.encode(encoding)); f.flush(); os.fsync(f.fileno())
        with open(tmp, "rb") as f:
            back = f.read()
        if back != text.encode("utf-8"):
            raise Rejected("readback", "回读字节与可信原文 UTF-8 字节不一致")
        if _before_publish:  # 仅供矩阵模拟替换前的并发写入/失锁
            _before_publish()
        if lock is not None:
            check_lock(lock)  # 替换前再核对一次：锁仍属本轮、目标仍是读取时的版本
        check_target(target, expected_old_sha256, "替换前")
        try:
            _replace(tmp, target)
        except OSError as e:
            raise Rejected("publish", f"原子替换失败: {e!r}")
        tmp = None
        with open(target, "rb") as f:
            if f.read() != text.encode("utf-8"):  # 替换后被他人改写：不报 saved
                raise Rejected("postcheck", "替换后目标内容不等于候选，可能被未持锁写入者覆盖")
        result.update(ok=True, stage="saved")
    except Rejected as e:
        result.update(stage=e.stage, reason=e.reason)
    except Exception as e:  # 非预期错误同样留证，不吞异常
        result.update(stage="internal", reason=repr(e))
        put(ev, "traceback.txt", traceback.format_exc())
    finally:
        if tmp and os.path.exists(tmp):
            os.replace(tmp, os.path.join(ev, "rejected-candidate.bin"))
        after = sha256_or_none(target)
        result["target_sha256_after"] = after
        result["exit_code"] = 0 if result["ok"] else (3 if result["stage"] == "internal" else 2)
        if not result["ok"] and (after != before or result["stage"] == "postcheck"):
            result.update(exit_code=4, reason=f"{result['reason']}；且目标已变化，需人工处理")
        put(ev, "result.json", json.dumps(result, ensure_ascii=False, indent=2))
    return result, ev
```
<!-- evidence-write:end -->

调用方先验证源，再由同一映射渲染字段；下面使用合成源值演示新建唯一报告。执行前把上述代码块保存到同一目录，每次选择新的报告路径。

```python
import json
import uuid
from pathlib import Path
from evidence_write import check_source, guarded_write

source = {"head": "a" * 40, "ci": {"run": 123,
          "time": "2026-11-08T00:00:00Z", "conclusion": "success"}}
mapping = {"head": ("head", "sha"), "ci_run": ("ci.run", "run_id"),
           "ci_time": ("ci.time", "utc_time"), "ci_status": ("ci.conclusion", "status")}
fields = check_source(source, mapping)
text = "# 自动维护记录\n\n" + r"路径样例 C:\new\tab 与 \a151 保持字面。" + "\n\n"
text += "".join(f"- {label}：`{value}`\n" for label, value in fields.items())
target = Path("report-" + uuid.uuid4().hex + ".md")
result, evidence = guarded_write(str(target), text, ["自动维护记录"], source,
                                mapping, expected_old_sha256=None, lock=None)
print(json.dumps({"result": result, "evidence": evidence}, ensure_ascii=False))
raise SystemExit(result["exit_code"])
```

## 示例验收矩阵与成功判据

每个场景必须在隔离目录运行，单个验证子进程硬限时 60 秒；保留 stdout、stderr、退出码和尝试目录。以下 33 个场景约束最终代码块，正常与故障输入必须一起验证。`C1` 为唯一保存场景；29 个普通拒绝场景要求旧账本不变，`E6`–`E8` 要求拒绝且保留他人写入内容、退出码为 4。每例均检查候选输入、结果与所需故障证据，无残留临时文件。

| ID | 输入或故障 | 期望结果阶段 |
| --- | --- | --- |
| C1 | 中文、反引号、三个字面反斜杠、完整机器字段 | `saved`；逐字节相等 |
| C2 | 汉字经 ASCII 降级成问号 | `sentinel` |
| C3 | SHA 前反引号被解释为 U+0007 | `control` |
| C3b | 删除控制字符后 SHA 只剩 39 位 | `field` |
| C4 | 字面反斜杠序列被解释为 BEL | `control` |
| C5 / C5b | 源 SHA 为 39 位 / 大写 | `source` |
| C6 | 所谓可信原文已损坏、缺中文哨兵 | `sentinel` |
| C7 / C7b | 空哨兵 / 哨兵不含汉字 | `contract` |
| C8 | 用 GBK 写入 | `readback`，保留候选文件 |
| F1 / F1b | head 错，正确 SHA 仅在其他字段 / 正文出现 | `field` |
| F2 / F3 / F4 | CI run / 时间 / 状态错误 | `field` |
| F5 / F6 | 缺失字段 / 重复字段 | `field` |
| F7 / F8 | 源缺字段 / 源时间格式非法 | `source` |
| F9 / F9b | 字段正确但正文另写错误 / 大写 SHA | `field` |
| E1 | 正文不是字符串 | `input`，保留 `candidate.repr` |
| E2 | 目标哈希与预期旧值不同 | `concurrent` |
| E3 | 原子替换权限失败 | `publish`，保留候选文件 |
| E4 | 生成命令退出码为 5 | `producer`，保留 stdout/stderr |
| E5 | 生成命令输出 GBK | `internal`/3，保留 stdout/stderr/traceback |
| E6 | 生成期间他人改写目标 | `concurrent`/4，保留他人内容 |
| E7 | 替换前他人改写目标 | `concurrent`/4，保留他人内容和候选文件 |
| E8 | 替换后他人覆盖目标 | `postcheck`/4，不报 saved |
| E9 | 修改已有文件但没有锁 | `contract` |
| E10 | 锁 owner 不是本轮 | `lock` |
| E11 | 替换前锁被接管 | `lock`，保留候选文件 |

公开文档还须通过：

- 新增内容显式 UTF-8 回读、与可信原文逐字比较、非空中文哨兵、无意外控制字符和私有信息。
- `mkdocs build --strict` 与 `git diff --check`，每个子进程最大 60 秒；超时或依赖缺失应如实记录。
- 文档提交与最新 dev 的关系、准确 PR head、四文件范围及独立审查记录。代码块、正文或 head 变更后，旧结果不能直接复用。

本次仅涉及根目录 `LESSONS.md`、`AGENTS.md` 阅读路由、本页和 `mkdocs.yml` 导航。无 API、配置、环境变量或应用持久化格式变化，因此 `docs/reference/` 豁免；无用户功能、部署、升级步骤变化，因此用户文档与 README 豁免。未修改前端或 .NET 代码，相应构建和测试不适用。一般文档要求仍见[文档维护](documentation.md)。

## 命令日志与重试

命令失败也要保留原始 stdout、stderr 和退出码。一次失败后重试、dev 前进后的再次验证，都应独占创建新的尝试目录，记录步骤、UTC 时间和当时 HEAD；已有目录和文件不得覆盖或清理。对整套旧日志生成 SHA-256 清单，再次调用后逐项比较，不能只检查某一个守卫日志。

使用按尝试目录留证的 `run_logged` 包装器时，它的 stdout/stderr/exit 文件通过存在性检查及 `set -C`（noclobber）拒绝同名覆盖。验证步骤的 `verify.log` 依靠**独占的新尝试目录与存在性检查**防止正常重入覆盖，其重定向没有 `set -C`；`verify.exit` 才显式使用 noclobber。不得泛称所有文件都在 noclobber 下创建。外部进程直接删改留证目录不在该机制保证范围内，交付时仍须附哈希清单。

不要把“命令退出 0”单独当作业务通过：阴性对照的 0 可能表示成功复现缺陷。报告应写明命令、输入版本、预期、实际判定与限制。文档构建或镜像构建成功也不能替代独立审查、实际业务验收和发布门禁。

## 失败排查与回滚

| 症状或阶段 | 下一步 |
| --- | --- |
| 汉字变为问号 | 检查管道、控制台与写文件编码；回到损坏前的可信源重新生成 |
| SHA 首字符缺失，附近有 U+0007/U+0008/U+0000 | 检查 PowerShell 反引号转义与可展开字符串，不靠猜测补齐 |
| 字面反斜杠变成控制字符 | 检查 `echo -e`、格式串与反转义处理 |
| `readback` | 检查实际写入编码、BOM 与换行转换，保留被拒候选 |
| `source` / `field` | 核对源 JSON、字段映射与逐字段渲染，拒绝用正文其他位置的正确值替代 |
| `contract` / `lock` | 核对哨兵、映射和持锁区间；锁失效时停止，不抢锁 |
| `concurrent` / `postcheck`、退出码 4 | 核查绕过锁的写入者并保留双方证据，重新持锁读源，不强写 |
| 命令超时、权限或磁盘错误 | 保留原输出和失败阶段，说明未验证部分；修复环境后创建新的尝试 |

本规范是文档变更，回滚通过向 dev 提交对应文档提交的 revert 或修订 PR 完成，仍遵守既有审查与发布流程；不需要应用、数据库或部署迁移。历史报告、失败日志与哈希清单保留，不能随文档回滚删除。对错误状态的更正应基于可信源，在既有锁规则下新增更正记录并重新核验后原子更新。
