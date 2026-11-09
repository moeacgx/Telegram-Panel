"""离线资源合同；只运行 Python 替身，不注册或连接 Cloudflare。"""
import contextlib
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import unittest
from unittest.mock import patch

from test_warpctl import Fixture, PYTHON, ROOT, ctl


class ResourceTests(Fixture):
    def test_disabled_preserves_parent_environment_and_limits(self):
        with patch.dict(os.environ, {"GOMAXPROCS": "3", "GOMEMLIMIT": "96MiB",
                                     "RESOURCE_TEST_INHERITED": "retained"}):
            self.prepared()
        observed = ctl.read_json(self.store.profile("account-1") / "resources-check.json")
        self.assertEqual("3", observed["gomaxprocs"])
        self.assertEqual("96MiB", observed["gomemlimit"])
        self.assertEqual("retained", observed["inherited"])
        if sys.platform == "linux":
            import resource
            self.assertEqual(list(resource.getrlimit(resource.RLIMIT_NOFILE)), observed["nofile"])

    def test_two_profiles_apply_overrides_at_config_check_and_every_restart(self):
        self.store.resources = ctl.WireproxyResources("2", "64", "64" if sys.platform == "linux" else None)
        children, retries = {}, {}
        try:
            with patch.dict(os.environ, {"GOMAXPROCS": "7", "GOMEMLIMIT": "99MiB",
                                         "RESOURCE_TEST_INHERITED": "retained"}):
                for name in ("account-1", "account-2"):
                    with socket.socket() as probe:
                        probe.bind(("127.0.0.1", 0))
                        self.prepared(name, probe.getsockname()[1])
                for _ in range(2):
                    ctl.reconcile_profiles(self.store, children, retries)
                    for name in ("account-1", "account-2"):
                        observed = self.wait_observation(name, children[name].pid)
                        for filename in ("resources-check.json", "resources-run.json"):
                            item = ctl.read_json(self.store.profile(name) / filename)
                            self.assertEqual("2", item["gomaxprocs"])
                            self.assertEqual("64MiB", item["gomemlimit"])
                            self.assertEqual("retained", item["inherited"])
                            if sys.platform == "linux":
                                self.assertEqual([64, 64], item["nofile"])
                        self.assertEqual(observed["pid"], children[name].pid)
                    for child in children.values():
                        ctl.stop_process(child)
                    ctl.reconcile_profiles(self.store, children, retries)
                    self.assertFalse(children)
                    retries.clear()  # 绕过生产 30 秒冷却，仅验证重启重新应用。
                self.assertEqual("7", os.environ["GOMAXPROCS"])
                self.assertEqual("99MiB", os.environ["GOMEMLIMIT"])
        finally:
            for child in children.values():
                ctl.stop_process(child)

    def wait_observation(self, name, pid):
        path = self.store.profile(name) / "resources-run.json"
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                item = ctl.read_json(path, {})
                if item.get("pid") == pid:
                    return item
            except ValueError:
                pass
            time.sleep(0.02)
        self.fail("资源替身未报告启动结果")

    def test_invalid_values_fail_before_any_registration_or_start(self):
        for option in ("--wireproxy-gomaxprocs", "--wireproxy-gomemlimit-mib", "--wireproxy-nofile"):
            for value in ("0", "-1", "1.5", " 1", "2147483648", "FAKE_SECRET_TOKEN"):
                for command in (["register", "new", "--accept-tos"],
                                ["provision", "new", "--accept-tos"],
                                ["generate", "new", "--port", "18081"],
                                ["start", "new"], ["serve"], ["check-resources"]):
                    with self.subTest(option=option, value=value, command=command):
                        error = io.StringIO()
                        argv = ["warpctl", "--data", str(self.store.root), option + "=" + value] + command
                        with patch.object(sys, "argv", argv), contextlib.redirect_stderr(error), \
                                patch.object(ctl, "run_private") as upstream:
                            self.assertEqual(1, ctl.main())
                        upstream.assert_not_called()
                        self.assertNotIn("FAKE_SECRET_TOKEN", error.getvalue())
                        self.assertIn(option[2:], error.getvalue())
                        self.assertFalse((self.store.root / "new").exists())

    def test_invalid_template_does_not_block_status_stop_list_delete(self):
        self.prepared()
        for command in (["status", "account-1"], ["stop", "account-1"], ["list"]):
            argv = ["warpctl", "--data", str(self.store.root), "--wireproxy-nofile=bad"] + command
            with patch.object(sys, "argv", argv), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, ctl.main())
        ctl.reconcile_profiles(self.store, {}, {})
        with patch.object(sys, "argv", ["warpctl", "--data", str(self.store.root),
                                       "--wireproxy-nofile=bad", "delete", "account-1"]), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, ctl.main())

    def test_spawn_subprocess_error_isolated_and_retry_can_recover(self):
        first = self.prepared("account-1", 18181)
        second = self.prepared("account-2", 18182)
        children, retries = {}, {}
        real_popen = subprocess.Popen

        def spawn(command, **kwargs):
            if str(first / "wireproxy.conf") in command:
                raise subprocess.SubprocessError("FAKE_SECRET_TOKEN")
            return real_popen(command, **kwargs)

        try:
            with patch.object(ctl.subprocess, "Popen", side_effect=spawn):
                ctl.reconcile_profiles(self.store, children, retries)
            self.assertEqual("failed", ctl.read_json(first / "runtime.json")["state"])
            self.assertNotIn("FAKE_SECRET_TOKEN", (first / "runtime.json").read_text())
            self.assertIn("account-2", children)
            self.wait_observation("account-2", children["account-2"].pid)
            retries.clear()
            ctl.reconcile_profiles(self.store, children, retries)
            self.wait_observation("account-1", children["account-1"].pid)
        finally:
            for child in children.values():
                ctl.stop_process(child)

    def test_capacity_boundary_preserves_existing_and_recovery(self):
        first = self.store.provision("web-1", True)
        for i in range(2, 101):
            path = self.store.profile(f"web-{i}", True)
            ctl.atomic_write(path / "meta.json", {"registered": False})
        with self.assertRaises(ctl.Failure):
            self.store.register("new", True)
        with self.assertRaises(ctl.Failure):
            self.store.provision("new", True)
        self.store = ctl.Store(self.store.root, self.store.wgcf, self.store.wireproxy, timeout=5)
        self.assertEqual(first["revision"], self.store.provision("web-1", True)["revision"])
        self.assertTrue(self.store.register("web-2", True)["registered"])
        self.assertEqual(100, len(self.store.list_profiles()))
        self.store.desired("web-1", False)
        ctl.reconcile_profiles(self.store, {}, {})
        self.store.delete("web-1")
        self.assertTrue(self.store.register("new", True)["registered"])

    @unittest.skipUnless(sys.platform == "linux", "RLIMIT_NOFILE 仅在 Linux 验证")
    def test_linux_fd_exhaustion_recovery_and_parent_unchanged(self):
        import resource
        before = resource.getrlimit(resource.RLIMIT_NOFILE)
        self.store.resources = ctl.WireproxyResources("1", "32", "64")
        for name in ("account-1", "account-2"):
            path = self.prepared(name, 18281 if name == "account-1" else 18282)
            result = ctl.read_json(path / "resources-check.json")
            self.assertEqual([64, 64], result["nofile"])
        probe = ROOT / "tests" / "fd_probe.py"
        command, options = self.store.resources.launch([PYTHON, "-S", str(probe)])
        result = subprocess.run(command, **options, capture_output=True, text=True, timeout=5, check=True)
        observed = json.loads(result.stdout)
        self.assertTrue(observed["exhausted"])
        self.assertTrue(observed["recovered"])
        self.assertEqual([64, 64], observed["nofile"])
        self.assertEqual(before, resource.getrlimit(resource.RLIMIT_NOFILE))

    @unittest.skipUnless(sys.platform == "linux", "父 hard limit 仅在 Linux 验证")
    def test_nofile_above_parent_hard_limit_rejected(self):
        import resource
        with patch.object(resource, "getrlimit", return_value=(32, 64)):
            with self.assertRaisesRegex(ctl.Failure, "hard limit"):
                ctl.WireproxyResources(nofile="65").validate()

    @unittest.skipIf(sys.platform == "linux", "非 Linux 拒绝显式 NOFILE")
    def test_nofile_unsupported_platform_fails_explicitly(self):
        with self.assertRaisesRegex(ctl.Failure, "Linux"):
            ctl.WireproxyResources(nofile="64").validate()
