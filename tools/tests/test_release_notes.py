"""验证发布摘要读取、版本范围与正文模板，避免不同发布路径生成不同格式。"""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import release_notes  # noqa: E402


class SummaryTests(unittest.TestCase):
    def test_extract_stops_before_internal_second_level_section(self):
        message = (
            "release: 发布修复\n\n## 本次版本主要更新\n\n"
            "### 修复问题\n\n- 允许清理旧记录。\n\n"
            "## 验证\n\n- 内部构建记录。\n\n## 发布决定\n内部决定。"
        )
        summary = release_notes.extract_summary(message)
        self.assertIn("允许清理旧记录", summary)
        self.assertNotIn("内部", summary)
        self.assertNotIn("## 本次版本主要更新", summary)

    def test_missing_summary_is_empty(self):
        self.assertEqual("", release_notes.extract_summary("fix: 修复出口冲突"))

    def test_legal_indented_second_level_headings_stop_internal_sections(self):
        for indent in range(4):
            for closing in ("", " ##"):
                with self.subTest(indent=indent, closing=closing):
                    message = (
                        "## 本次版本主要更新\n\n### 修复问题\n- 用户可见修复。\n\n"
                        + " " * indent + "## 内部验证" + closing + "\n- 不得公开的内部记录。"
                    )
                    summary = release_notes.extract_summary(message)
                    self.assertIn("用户可见修复", summary)
                    self.assertNotIn("内部", summary)

    def test_summary_and_category_headings_accept_indentation_and_closing_hashes(self):
        for indent in range(4):
            with self.subTest(indent=indent):
                message = (
                    " " * indent + "## 本次版本主要更新 ##\n\n"
                    + " " * indent + "### 修复问题 ###\n- 用户可见修复。\n\n"
                    "## 内部验证\n- 不得公开的内部记录。"
                )
                summary = release_notes.normalize_summary(release_notes.extract_summary(message))
                self.assertIn("### 🐛 修复问题", summary)
                self.assertIn("用户可见修复", summary)
                self.assertNotIn("内部", summary)

    def test_deduplication_preserves_distinct_inline_code_whitespace(self):
        summary = release_notes.normalize_summary(
            "### 修复问题\n- 支持配置值 `A  B`。\n- 支持配置值 `A B`。\n"
            "- 支持配置值 `A  B`。"
        )
        self.assertEqual(1, summary.count("- 支持配置值 `A  B`。"))
        self.assertEqual(1, summary.count("- 支持配置值 `A B`。"))

    def test_normalization_merges_categories_and_deduplicates_items(self):
        summary = release_notes.normalize_summary(
            "### 修复问题\n\n- 修复出口冲突。\n\n"
            "### 🐛 修复问题\n\n- 修复出口冲突。\n- 保留占用提示。\n\n"
            "### 新增功能\n\n"
        )
        self.assertEqual(1, summary.count("### 🐛 修复问题"))
        self.assertEqual(1, summary.count("- 修复出口冲突。"))
        self.assertIn("- 保留占用提示。", summary)
        self.assertNotIn("新增功能", summary)

    def test_known_unknown_and_existing_emoji_headings(self):
        summary = release_notes.normalize_summary(
            "### 版本与文档\n- 同步说明。\n\n"
            "### 验证范围\n- 已完成本地测试。\n\n"
            "### 操作提示\n- 升级后检查代理。\n\n"
            "### 🔐 安全提醒\n- 保留原有提醒。"
        )
        self.assertIn("### 📚 版本与文档", summary)
        self.assertIn("### ✅ 验证范围", summary)
        self.assertIn("### 🧩 操作提示", summary)
        self.assertIn("### 🔐 安全提醒", summary)
        self.assertNotIn("🧩 🔐", summary)

    def test_code_block_headings_do_not_split_release_summary(self):
        message = (
            "## 本次版本主要更新\n\n### 修复问题\n"
            "- 修复说明文件读取，示例内容如下。\n\n```markdown\n"
            "## 示例二级标题\n### 示例三级标题\n```\n\n"
            "- 保留代码块之后的业务说明。\n\n## 验证\n内部记录。"
        )
        summary = release_notes.normalize_summary(release_notes.extract_summary(message))
        self.assertIn("## 示例二级标题\n### 示例三级标题", summary)
        self.assertIn("保留代码块之后的业务说明", summary)
        self.assertNotIn("🧩 示例三级标题", summary)
        self.assertNotIn("内部记录", summary)

    def test_four_backtick_fence_preserves_repeated_code_and_spacing(self):
        block = (
            "````markdown\n```\n"
            + "- 示例重复行\n" * 4
            + "\n  保留缩进与尾部空格。  \n\n```\n````"
        )
        summary = release_notes.normalize_summary(
            "### 修复问题\n- 支持 Markdown 示例。\n\n" + block
            + "\n\n- 保留后续说明。\n- 保留后续说明。"
        )
        self.assertEqual(
            "### 🐛 修复问题\n\n- 支持 Markdown 示例。\n\n" + block
            + "\n\n- 保留后续说明。", summary,
        )

    def test_invalid_closers_keep_code_and_headings_inside_fence(self):
        for marker in ("`", "~"):
            for invalid in (
                marker * 3, marker * 4 + "markdown", marker * 5 + " info",
                ("~" if marker == "`" else "`") * 4,
                "    " + marker * 4, "\t" + marker * 4,
                marker * 4 + "\u00a0",
            ):
                with self.subTest(marker=marker, invalid=invalid):
                    block = (
                        marker * 4 + "markdown\n" + invalid + "\n"
                        + "- 重复代码行\n" * 4
                        + "## 示例二级标题\n### 示例三级标题\n" + marker * 4
                    )
                    message = (
                        "## 本次版本主要更新\n\n### 修复问题\n" + block
                        + "\n\n- 保留后续说明。\n- 保留后续说明。"
                        + "\n\n## 内部验证\n不得公开。"
                    )
                    summary = release_notes.normalize_summary(release_notes.extract_summary(message))
                    self.assertEqual(
                        "### 🐛 修复问题\n\n" + block + "\n\n- 保留后续说明。", summary,
                    )

    def test_legal_closers_allow_longer_fences_indentation_and_spaces(self):
        for marker in ("`", "~"):
            for indent in range(4):
                for closing_length in (4, 6):
                    with self.subTest(marker=marker, indent=indent, length=closing_length):
                        block = (
                            " " * indent + marker * 4 + "markdown\n"
                            + "- 重复代码行\n" * 4
                            + " " * indent + marker * closing_length + " \t"
                        )
                        message = (
                            "## 本次版本主要更新\n### 修复问题\n" + block
                            + "\n\n- 普通条目。\n- 普通条目。\n\n## 内部验证\n不得公开。"
                        )
                        summary = release_notes.normalize_summary(release_notes.extract_summary(message))
                        self.assertEqual("### 🐛 修复问题\n\n" + block + "\n\n- 普通条目。", summary)

    def test_invalid_openers_do_not_disable_normal_list_deduplication(self):
        for opener in ("```bad`info", "    ```", "\t~~~"):
            with self.subTest(opener=opener):
                summary = release_notes.normalize_summary(
                    "### 修复问题\n" + opener + "\n\n- 普通条目。\n- 普通条目。"
                )
                self.assertEqual(1, summary.count("- 普通条目。"))

    def test_repeated_top_level_fences_are_not_deduplicated_with_list_items(self):
        block = "```text\n- 重复代码行\n- 重复代码行\n```"
        summary = release_notes.normalize_summary(
            "### 修复问题\n" + ("- 普通条目。\n\n" + block + "\n\n") * 2
        )
        self.assertEqual("### 🐛 修复问题\n\n- 普通条目。\n\n" + block + "\n\n" + block, summary)


class RepositoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="telegram-panel-release-tests-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name).resolve()
        self.git("init", "--quiet", "--initial-branch=main")
        self.git("config", "user.name", "发布测试")
        self.git("config", "user.email", "release-tests@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "tag.gpgsign", "false")
        self.git("config", "core.hooksPath", str(self.repo / "no-hooks"))
        self.commit("chore: 初始版本", "v1.0.0")

    def git(self, *args):
        return subprocess.check_output(
            ["git", *args], cwd=self.repo, encoding="utf-8", stderr=subprocess.PIPE,
            timeout=10,
        ).strip()

    def commit(self, message, tag=None):
        self.git("commit", "--quiet", "--allow-empty", "-m", message)
        if tag:
            self.git("tag", tag)

    def generate(self, tag="v1.0.1", **kwargs):
        return release_notes.generate_notes(
            tag, "moeacgx/Telegram-Panel", repo_dir=self.repo, **kwargs
        )

    def test_merge_commit_summary_wins_over_child_commit(self):
        self.git("checkout", "--quiet", "-b", "feature")
        self.commit(
            "fix: 修复出口\n\n## 本次版本主要更新\n\n"
            "### 修复问题\n- 子提交的旧描述。"
        )
        self.git("checkout", "--quiet", "main")
        self.git(
            "merge", "--no-ff", "feature", "-m",
            "release: 发布\n\n## 本次版本主要更新\n\n"
            "### 修复问题\n- 同名账号现在绑定独立出口。\n\n"
            "## 验证\n- 内部验证记录。",
        )
        self.git("tag", "v1.0.1")
        notes = self.generate()
        self.assertIn("### 🐛 修复问题", notes)
        self.assertIn("同名账号现在绑定独立出口", notes)
        self.assertNotIn("子提交的旧描述", notes)
        self.assertNotIn("内部验证记录", notes)

    def test_range_collects_merge_summary_when_target_has_none(self):
        self.git("checkout", "--quiet", "-b", "feature")
        self.commit("fix: 修复导入")
        self.git("checkout", "--quiet", "main")
        self.git(
            "merge", "--no-ff", "feature", "-m",
            "合并修复\n\n## 本次版本主要更新\n\n"
            "### 修复问题\n- 保留合并提交中的业务说明。",
        )
        self.commit("release: 准备版本", "v1.0.1")
        self.assertIn("保留合并提交中的业务说明", self.generate())

    def test_range_summaries_are_merged_and_deduplicated(self):
        summary = "## 本次版本主要更新\n\n### 修复问题\n- 修复重复绑定。"
        self.commit("fix: 修复导入\n\n" + summary)
        self.commit("fix: 补充修复\n\n" + summary + "\n- 显示清晰提示。")
        self.commit("release: 准备版本", "v1.0.1")
        notes = self.generate()
        self.assertEqual(1, notes.count("### 🐛 修复问题"))
        self.assertEqual(1, notes.count("- 修复重复绑定。"))
        self.assertIn("显示清晰提示", notes)

    def test_subject_fallback_has_categories_and_readable_titles(self):
        self.commit("feat(accounts): 新增分类选择")
        self.commit("fix: 修复出口冲突", "v1.0.1")
        notes = self.generate()
        self.assertIn("### ✨ 新增功能", notes)
        self.assertIn("### 🐛 修复问题", notes)
        self.assertIn("新增分类选择", notes)
        self.assertIn("修复出口冲突", notes)
        self.assertNotIn("feat(accounts):", notes)
        self.assertNotIn("fix:", notes)

    def test_empty_extracted_summary_falls_back_to_commit_title(self):
        self.commit("fix: 保留实际修复\n\n## 本次版本主要更新\n\n### 修复问题", "v1.0.1")
        notes = self.generate()
        self.assertIn("保留实际修复", notes)

    def test_explicit_empty_summary_is_rejected(self):
        self.commit("fix: 修复出口", "v1.0.1")
        with self.assertRaises((ValueError, RuntimeError)):
            self.generate(summary="### 修复问题\n\n")

    def test_old_release_rerun_does_not_use_newer_tag(self):
        self.commit("fix: 旧版本修复", "v1.0.1")
        self.commit("feat: 后续新功能", "v1.0.2")
        self.assertEqual("v1.0.0", release_notes.find_previous_tag("v1.0.1", self.repo))
        notes = self.generate()
        self.assertIn("compare/v1.0.0...v1.0.1", notes)
        self.assertNotIn("后续新功能", notes)

    def test_unreachable_version_is_not_previous(self):
        self.git("checkout", "--quiet", "-b", "other")
        self.commit("fix: 另一条分支", "v1.9.0")
        self.git("checkout", "--quiet", "main")
        self.commit("fix: 当前分支", "v2.0.0")
        self.assertEqual("v1.0.0", release_notes.find_previous_tag("v2.0.0", self.repo))

    def test_stable_release_does_not_choose_prerelease(self):
        self.commit("fix: 稳定修复", "v1.1.0")
        self.commit("feat: 预发布内容", "v2.0.0-rc.1")
        self.commit("release: 正式版本", "v2.0.0")
        self.assertEqual("v1.1.0", release_notes.find_previous_tag("v2.0.0", self.repo))

    def test_prerelease_numeric_identifiers_follow_semver(self):
        self.commit("feat: 候选版本二", "v2.0.0-rc.2")
        self.commit("fix: 候选版本十", "v2.0.0-rc.10")
        self.commit("fix: 候选版本十一", "v2.0.0-rc.11")
        self.assertEqual(
            "v2.0.0-rc.10", release_notes.find_previous_tag("v2.0.0-rc.11", self.repo)
        )

    def test_manual_previous_must_be_older(self):
        self.commit("fix: 第一个修复", "v1.0.1")
        self.commit("fix: 后续修复", "v1.0.2")
        for previous in ("v1.0.1", "v1.0.2"):
            with self.subTest(previous=previous):
                with self.assertRaises((ValueError, RuntimeError)):
                    self.generate(previous_tag=previous)

    def test_manual_previous_must_be_reachable(self):
        self.git("checkout", "--quiet", "-b", "other")
        self.commit("fix: 另一分支修复", "v1.0.1")
        self.git("checkout", "--quiet", "main")
        self.commit("fix: 当前分支修复", "v1.0.2")
        with self.assertRaises((ValueError, RuntimeError)):
            self.generate("v1.0.2", previous_tag="v1.0.1")

    def test_invalid_target_tag_is_rejected(self):
        for tag in ("main", "--all", "v01.0.0", "v1.0", "v1.0.0-01"):
            with self.subTest(tag=tag):
                with self.assertRaises((ValueError, RuntimeError)):
                    self.generate(tag)

    def test_first_release_has_no_compare_link(self):
        notes = self.generate("v1.0.0")
        self.assertNotIn("/compare/", notes)
        self.assertIn("## 本次版本主要更新", notes)

    def test_template_retains_upgrade_advice_and_uses_lowercase_image(self):
        self.commit("fix: 修复旧记录删除", "v1.0.1")
        notes = self.generate(
            summary="### 修复问题\n- 旧记录解除引用后允许删除。\n\n"
            "### 验证范围\n- 已完成本地测试；真实业务仍需确认。",
            upgrade_notes="升级后先切换账号引用，再勾选旧记录删除；不要手动修改数据库。",
        )
        self.assertTrue(notes.startswith("## 本次版本主要更新"))
        for heading in (
            "### 🐛 修复问题", "### ✅ 验证范围", "🔗 完整变更记录",
            "## 🚀 部署与更新", "### 🐳 1. Docker 镜像",
            "### 🔄 2. 面板内一键更新", "### 🖥️ 3. Windows 桌面版",
            "## 📦 应用内更新资产",
        ):
            self.assertIn(heading, notes)
        self.assertIn("ghcr.io/moeacgx/telegram-panel:v1.0.1", notes)
        self.assertIn("真实业务仍需确认", notes)
        self.assertIn("不要手动修改数据库", notes)
        self.assertLess(notes.index("## 本次版本主要更新"), notes.index("## 🚀 部署与更新"))
        for suffix in ("linux-x64.zip", "linux-arm64.zip", "windows-x64-setup.exe"):
            self.assertIn("telegram-panel-v1.0.1-" + suffix, notes)

    def test_cli_reads_utf8_summary_and_writes_complete_body(self):
        self.commit("fix: 修复旧记录删除", "v1.0.1")
        summary_file = self.repo / "摘要.md"
        summary_file.write_text("### 修复问题\n- 修复旧记录无法删除。", encoding="utf-8")
        upgrade_file = self.repo / "升级说明.md"
        upgrade_file.write_text("升级后先切换账号引用。", encoding="utf-8")
        output = self.repo / "发布正文.md"
        result = subprocess.run(
            [sys.executable, str(TOOLS / "release_notes.py"), "--tag", "v1.0.1",
             "--repository", "moeacgx/Telegram-Panel", "--previous-tag", "v1.0.0",
             "--summary-file", str(summary_file), "--upgrade-notes-file", str(upgrade_file),
             "--output", str(output)],
            cwd=self.repo, capture_output=True, encoding="utf-8", timeout=15,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        notes = output.read_text(encoding="utf-8")
        self.assertIn("### 🐛 修复问题", notes)
        self.assertIn("修复旧记录无法删除", notes)
        self.assertIn("升级后先切换账号引用", notes)
        self.assertIn("compare/v1.0.0...v1.0.1", notes)

    def test_cli_preserves_four_backtick_example_verbatim(self):
        block = "````markdown\n```\n" + "- 示例重复行\n" * 4 + "```\n````"
        summary_file = self.repo / "围栏摘要.md"
        summary_file.write_text(
            "### 修复问题\n- 支持 Markdown 示例。\n\n" + block + "\n\n- 保留后续说明。\n",
            encoding="utf-8",
        )
        output = self.repo / "发布正文.md"
        result = subprocess.run(
            [sys.executable, str(TOOLS / "release_notes.py"), "--tag", "v1.0.0",
             "--repository", "moeacgx/Telegram-Panel", "--summary-file", str(summary_file),
             "--output", str(output)],
            cwd=self.repo, capture_output=True, encoding="utf-8", timeout=15,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        notes = output.read_text(encoding="utf-8")
        actual = notes.split("## 本次版本主要更新\n\n", 1)[1].split("\n\n> [!WARNING]", 1)[0]
        self.assertEqual(
            "### 🐛 修复问题\n\n- 支持 Markdown 示例。\n\n" + block + "\n\n- 保留后续说明。",
            actual,
        )

    def test_cli_invalid_tag_does_not_overwrite_existing_output(self):
        output = self.repo / "发布正文.md"
        output.write_text("已有发布正文，不应被错误调用覆盖。", encoding="utf-8")
        result = subprocess.run(
            [sys.executable, str(TOOLS / "release_notes.py"), "--tag", "main",
             "--repository", "moeacgx/Telegram-Panel", "--output", str(output)],
            cwd=self.repo, capture_output=True, encoding="utf-8", timeout=15,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("已有发布正文，不应被错误调用覆盖。", output.read_text(encoding="utf-8"))

    def test_cli_rejects_unwrapped_indented_second_level_sections(self):
        self.commit("fix: 修复导入", "v1.0.1")
        summary_file = self.repo / "摘要.md"
        output = self.repo / "发布正文.md"
        for indent in range(4):
            with self.subTest(indent=indent):
                summary_file.write_text(
                    "### 修复问题\n- 用户可见修复。\n\n"
                    + " " * indent + "## 内部验证 ##\n- 不得公开的内部记录。",
                    encoding="utf-8",
                )
                result = subprocess.run(
                    [sys.executable, str(TOOLS / "release_notes.py"), "--tag", "v1.0.1",
                     "--repository", "moeacgx/Telegram-Panel", "--summary-file", str(summary_file),
                     "--output", str(output)],
                    cwd=self.repo, capture_output=True, encoding="utf-8", timeout=15,
                )
                self.assertNotEqual(0, result.returncode)

    def test_manual_summary_code_fence_heading_is_not_an_internal_section(self):
        self.commit("fix: 修复说明", "v1.0.1")
        notes = self.generate(summary=(
            "### 修复问题\n- 支持以下格式的说明：\n\n```markdown\n"
            "## 示例二级标题\n```\n\n- 保留后续业务说明。"
        ))
        self.assertIn("## 示例二级标题", notes)
        self.assertIn("保留后续业务说明", notes)


if __name__ == "__main__":
    unittest.main()
