#!/usr/bin/env python3
"""统一生成自动发布与发布后修订使用的中文 Release 正文。"""

import argparse
import re
import subprocess
import sys
import unicodedata
from pathlib import Path


SUMMARY_HEADING = "## 本次版本主要更新"
SECTION_ICONS = {
    "新增功能": "✨", "修复问题": "🐛", "性能优化": "⚡",
    "文档更新": "📚", "代码重构": "🧹", "测试改进": "✅",
    "构建发布": "📦", "CI/CD": "🔧", "版本发布": "🚀",
    "其他变更": "🧩", "版本与文档": "📚", "验证": "✅",
    "验证范围": "✅", "安全相关": "🔐",
}
COMMIT_SECTIONS = {
    "feat": "新增功能", "fix": "修复问题", "perf": "性能优化",
    "docs": "文档更新", "refactor": "代码重构", "test": "测试改进",
    "build": "构建发布", "ci": "CI/CD", "release": "版本发布",
    "chore": "其他变更", "other": "其他变更",
}
TAG_PATTERN = re.compile(
    r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\Z"
)


def _version(tag):
    match = TAG_PATTERN.fullmatch(tag)
    if not match:
        raise ValueError(f"版本标签必须为 vX.Y.Z 或合法预发布版本：{tag!r}")
    major, minor, patch, prerelease = match.groups()
    identifiers = []
    for identifier in prerelease.split(".") if prerelease else []:
        if identifier.isdigit():
            if len(identifier) > 1 and identifier.startswith("0"):
                raise ValueError(f"预发布版本数字标识不能有前导零：{tag!r}")
            identifiers.append((0, int(identifier)))
        else:
            identifiers.append((1, identifier))
    return (int(major), int(minor), int(patch), prerelease is None, tuple(identifiers))


def _git(*arguments, repo_dir="."):
    result = subprocess.run(
        ["git", *arguments], cwd=repo_dir, text=True, encoding="utf-8",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30, check=False,
    )
    if result.returncode:
        raise ValueError(f"Git 命令失败：{result.stderr.strip()}")
    return result.stdout.strip()


def _tag_commit(tag, repo_dir):
    _version(tag)
    return _git("rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}", repo_dir=repo_dir)


def find_previous_tag(tag, repo_dir="."):
    """选择目标祖先中版本号最高的较早标签，重跑旧版本时排除未来标签。"""
    target_version = _version(tag)
    target_commit = _tag_commit(tag, repo_dir)
    candidates = []
    for candidate in _git("tag", "--merged", target_commit, repo_dir=repo_dir).splitlines():
        try:
            candidate_version = _version(candidate)
        except ValueError:
            continue
        if candidate_version >= target_version:
            continue
        if target_version[3] and not candidate_version[3]:
            continue
        candidates.append((candidate_version, candidate))
    return max(candidates)[1] if candidates else None


def _validate_previous(tag, previous_tag, repo_dir):
    current_version, previous_version = _version(tag), _version(previous_tag)
    if previous_version >= current_version:
        raise ValueError("上一版本必须小于目标版本")
    if current_version[3] and not previous_version[3]:
        raise ValueError("稳定版本的上一版本必须是稳定版本")
    current_commit = _tag_commit(tag, repo_dir)
    previous_commit = _tag_commit(previous_tag, repo_dir)
    ancestor = _git("merge-base", current_commit, previous_commit, repo_dir=repo_dir)
    if ancestor != previous_commit:
        raise ValueError("上一版本必须是目标版本的祖先")


def _next_fence(line, fence):
    """标题提取与列表去重共用围栏规则，状态保留字符及起始长度。"""
    marker = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", line)
    if fence:
        if (marker and marker.group(1)[0] == fence[0]
                and len(marker.group(1)) >= fence[1]
                and re.fullmatch(r"[ \t]*", marker.group(2))):
            return None
        return fence
    if marker and not (marker.group(1)[0] == "`" and "`" in marker.group(2)):
        return (marker.group(1)[0], len(marker.group(1)))
    return None


def _markdown_lines(text):
    """识别围栏之外的 ATX 标题，遵循 Markdown 的缩进与结尾井号规则。"""
    fence = None
    for line in text.splitlines():
        next_fence = _next_fence(line, fence)
        if fence or next_fence:
            fence = next_fence
            yield line, None
            continue
        match = re.match(r"^ {0,3}(#{1,6})(?:[ \t]+(.*)|[ \t]*)$", line)
        heading = None
        if match:
            title = (match.group(2) or "").strip()
            # 只有以空白分隔的结尾井号才是语法；C# 等正文字符必须保留。
            title = re.sub(r"[ \t]+#+[ \t]*$", "", title)
            if re.fullmatch(r"#+", title):
                title = ""
            heading = (len(match.group(1)), title)
        yield line, heading


def extract_summary(message):
    """仅提取主要更新小节，不把后续内部验证或发布决定带入正文。"""
    picked = []
    active = False
    for line, heading in _markdown_lines(message):
        if active and heading and heading[0] == 2:
            break
        if heading == (2, "本次版本主要更新"):
            active = True
            continue
        if active:
            picked.append(line)
    return "\n".join(picked).strip()


def _heading(title):
    title = title.strip()
    plain = title
    while plain and (
        plain[0].isspace() or unicodedata.category(plain[0]) in {"So", "Sk", "Mn"}
        or plain[0] == "\u200d"
    ):
        plain = plain[1:]
    # 已有图标保留；统一用去除图标后的标题识别同名分类。
    decorated = title if plain != title else f"{SECTION_ICONS.get(plain, '🧩')} {title}"
    return plain, decorated


def _meaningful(text):
    return any(
        not line.lstrip().startswith(("#", "```", "~~~", "<!--"))
        and any(character.isalnum() for character in line)
        for line in text.splitlines()
    )


def _deduplicate_items(lines):
    """以完整顶层列表项去重，保留续行、嵌套列表和普通段落。"""
    blocks = []
    current = []
    is_item = False
    fence = None
    for line in lines:
        next_fence = _next_fence(line, fence)
        starts_item = fence is None and next_fence is None and re.match(r"^(?:[-*+] |[0-9]+[.)] )", line)
        if starts_item:
            if current:
                blocks.append((is_item, "\n".join(current).strip("\n")))
            current, is_item = [line], True
        elif is_item and line and not line[0].isspace() and fence is None:
            # 顶层起始围栏属于独立正文；闭合行仍属于代码，不能在其前插入分段。
            blocks.append((True, "\n".join(current).strip("\n")))
            current, is_item = [line], False
        else:
            current.append(line)
        fence = next_fence
    if current:
        blocks.append((is_item, "\n".join(current).strip("\n")))
    seen = set()
    output = []
    previous_item = False
    for item, block in blocks:
        if not block.strip():
            continue
        # 内联代码中的连续空格、换行及 Markdown 硬换行可能有意义，不能折叠。
        key = block
        if item and key in seen:
            continue
        if item:
            seen.add(key)
        if output:
            output.append("\n" if item and previous_item else "\n\n")
        output.append(block)
        previous_item = item
    return "".join(output)


def normalize_summary(text):
    """统一分类图标、合并同名分类并去重，空分类不输出。"""
    sections = {"": ("", [])}
    current = ""
    for line, heading in _markdown_lines(text):
        if heading and heading[0] == 3:
            current, decorated = _heading(heading[1])
            sections.setdefault(current, (decorated, []))
        else:
            sections[current][1].append(line)
    output = []
    for title, lines in sections.values():
        body = _deduplicate_items(lines)
        if _meaningful(body):
            output.append(f"### {title}\n\n{body}" if title else body)
    return "\n\n".join(output)


def _commit_fallback(range_ref, repo_dir):
    buckets = {section: [] for section in dict.fromkeys(COMMIT_SECTIONS.values())}
    commits = _git(
        "log", "--no-merges", "--format=%s%x09%h", range_ref, repo_dir=repo_dir,
    )
    for line in commits.splitlines():
        subject, sha = line.rsplit("\t", 1)
        match = re.match(r"^([A-Za-z]+)(?:\([^)]*\))?!?:\s*(.+)$", subject)
        kind, title = (match.group(1).lower(), match.group(2)) if match else ("other", subject)
        buckets[COMMIT_SECTIONS.get(kind, "其他变更")].append(f"- {title.strip()} (`{sha}`)")
    return "\n\n".join(
        f"### {section}\n\n" + "\n".join(items)
        for section, items in buckets.items() if items
    )


def generate_notes(tag, repository, previous_tag=None, summary=None, upgrade_notes=None, repo_dir="."):
    """生成完整 Release 正文；人工摘要与自动摘要共享相同模板。"""
    target_commit = _tag_commit(tag, repo_dir)
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("仓库必须为 owner/repository 格式")
    if previous_tag is None:
        previous_tag = find_previous_tag(tag, repo_dir)
    else:
        _validate_previous(tag, previous_tag, repo_dir)
    range_ref = f"refs/tags/{previous_tag}..{target_commit}" if previous_tag else target_commit

    if summary is not None:
        extracted = extract_summary(summary)
        headings = [heading for _, heading in _markdown_lines(summary) if heading]
        if (2, "本次版本主要更新") in headings:
            summary = extracted
        elif any(heading[0] == 2 for heading in headings):
            raise ValueError("摘要文件应为分类正文，或包含“本次版本主要更新”小节")
    else:
        summary = extract_summary(_git("show", "-s", "--format=%B", target_commit, repo_dir=repo_dir))
        if not normalize_summary(summary):
            messages = _git("log", "--format=%B%x00", range_ref, repo_dir=repo_dir).split("\0")
            summary = "\n\n".join(extract_summary(message) for message in messages)
        if not normalize_summary(summary):
            summary = _commit_fallback(range_ref, repo_dir)
    summary = normalize_summary(summary)
    if not summary:
        raise ValueError("发布说明没有实质更新内容，请提供非空摘要")

    compare = (
        f"🔗 完整变更记录： https://github.com/{repository}/compare/{previous_tag}...{tag}\n\n"
        if previous_tag else ""
    )
    upgrade = f"### ⚠️ 升级注意事项\n\n{upgrade_notes.strip()}\n\n" if upgrade_notes and upgrade_notes.strip() else ""
    return f"""{SUMMARY_HEADING}

{summary}

{compare}> [!WARNING]
> 如果当前账号列表已在旧版一键更新后变空，请勿直接再次点击一键更新。
> 先停止容器并备份宿主机 `docker-data` 与容器内 `/app`；本版可以恢复仍存在的旧库，
> 但无法重新生成已经被旧更新器或人工操作删除的文件。

## 🚀 部署与更新

{upgrade}### 🐳 1. Docker 镜像
拉取对应 tag 的镜像：

```bash
docker pull ghcr.io/{repository.lower()}:{tag}
```

### 🔄 2. 面板内一键更新（Docker）
路径：`左上角版本号 -> 版本信息弹窗 -> 一键更新并重启`

### 🖥️ 3. Windows 桌面版
下载 `telegram-panel-{tag}-windows-x64-setup.exe` 安装包。

## 📦 应用内更新资产

- `telegram-panel-{tag}-linux-x64.zip`
- `telegram-panel-{tag}-linux-arm64.zip`
- `telegram-panel-{tag}-windows-x64-setup.exe`

> 程序会自动匹配容器架构下载更新包，并部署到 `/data/app-current`。
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--previous-tag")
    parser.add_argument("--summary-file", type=Path)
    parser.add_argument("--upgrade-notes-file", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        notes = generate_notes(
            arguments.tag, arguments.repository, arguments.previous_tag,
            arguments.summary_file.read_text(encoding="utf-8-sig") if arguments.summary_file else None,
            arguments.upgrade_notes_file.read_text(encoding="utf-8-sig") if arguments.upgrade_notes_file else None,
        )
        arguments.output.write_text(notes, encoding="utf-8", newline="\n")
    except (ValueError, OSError, subprocess.TimeoutExpired) as error:
        print(f"生成发布说明失败：{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
