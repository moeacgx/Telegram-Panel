from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
PYTHON = getattr(sys, "_base_executable", sys.executable)
sys.path.insert(0, str(ROOT))
import warpctl as ctl


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.fake = str(ROOT / "tests" / "fake_cli.py")
        self.store = ctl.Store(self.root / "data", [PYTHON, "-S", self.fake, "wgcf"],
                               [PYTHON, "-S", self.fake, "wireproxy"], timeout=5)

    def tearDown(self):
        self.temp.cleanup()

    def prepared(self, name="account-1", port=18081):
        self.store.register(name, True)
        self.store.generate(name, port)
        self.store.desired(name, True)
        return self.store.profile(name)

    def mode(self, value):
        path = self.store.profile("account-1", True)
        ctl.atomic_write(path / "fake-mode.json", value)
        return path


class ToolTests(Fixture):
    def provision(self, name="web-1", display_name=None, accept_tos=True):
        self.assertTrue(callable(getattr(self.store, "provision", None)), "缺少网页幂等创建入口")
        return self.store.provision(name, accept_tos, display_name)

    def test_web_provision_reuses_identity_port_password_and_revision(self):
        first = self.provision(display_name="网页出口")
        path = self.store.profile("web-1")
        original = (path / "wireproxy.conf").read_text()
        second = self.provision(display_name="网页出口")
        self.assertEqual(first["port"], second["port"])
        self.assertEqual(first["revision"], second["revision"])
        self.assertEqual("1", (path / "registrations.txt").read_text())
        self.assertEqual(original, (path / "wireproxy.conf").read_text())
        self.assertIn(f"BindAddress = 127.0.0.1:{first['port']}", original)
        self.assertTrue(second["desired"])
        self.assertFalse(second["egressVerified"])
        self.assertEqual("web-1", second["name"])
        self.assertEqual("网页出口", second["displayName"])

    def test_repeated_provision_preserves_explicit_stop_and_materials(self):
        self.provision(display_name="网页出口")
        path = self.store.profile("web-1")
        materials = {name: (path / name).read_bytes()
                     for name in ("wireproxy.conf", "proxy-auth.json", "wgcf-account.toml")}
        stopped = self.store.desired("web-1", False)
        repeated = self.provision(display_name="网页出口")
        self.assertFalse(repeated["desired"])
        self.assertEqual(stopped["revision"], repeated["revision"])
        self.assertEqual("1", (path / "registrations.txt").read_text())
        for name, original in materials.items():
            self.assertEqual(original, (path / name).read_bytes())
        started = self.store.desired("web-1", True)
        self.assertTrue(started["desired"])
        self.assertNotEqual(stopped["revision"], started["revision"])

    def test_web_provision_requires_terms_before_materials_exist(self):
        with self.assertRaises(ctl.Failure):
            self.provision(accept_tos=False)
        self.assertFalse((self.store.root / "web-1").exists())

    def test_web_display_name_validation_precedes_registration(self):
        for value in ("a" * 81, "换行\n名称", "\x00", " \t "):
            with self.assertRaises(ctl.Failure):
                self.provision(display_name=value)
        self.assertFalse((self.store.root / "web-1").exists())

    def test_web_provision_skips_reserved_and_listening_ports(self):
        self.prepared("reserved", 21000)
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 21001))
            listener.listen()
            result = self.provision()
        self.assertGreaterEqual(result["port"], 21002)
        self.assertLessEqual(result["port"], 29999)
        other = self.provision("web-2")
        self.assertNotEqual(result["port"], other["port"])

    def test_web_provision_recovers_complete_partial_registration(self):
        path = self.store.profile("web-1", True)
        ctl.atomic_write(path / "fake-mode.json", {"partial": True})
        with self.assertRaises(ctl.Failure):
            self.provision()
        ctl.atomic_write(path / "fake-mode.json", {})
        result = self.provision()
        self.assertTrue(result["generated"])
        self.assertEqual("1", (path / "registrations.txt").read_text())

    def test_web_provision_unknown_registration_does_not_retry(self):
        path = self.store.profile("web-1", True)
        ctl.atomic_write(path / "meta.json", {"registrationAttempted": True})
        with self.assertRaises(ctl.Failure):
            self.provision()
        self.assertFalse((path / "registrations.txt").exists())

    def test_web_provision_does_not_reuse_external_listening_configuration(self):
        self.prepared("web-1", 18081)
        original = (self.store.profile("web-1") / "wireproxy.conf").read_text()
        with self.assertRaises(ctl.Failure):
            self.provision()
        self.assertEqual(original, (self.store.profile("web-1") / "wireproxy.conf").read_text())

    def test_web_list_whitelists_materials_and_isolates_corrupt_profile(self):
        self.provision(display_name="出口 A")
        bad = self.store.profile("bad", True)
        (bad / "meta.json").write_text("{invalid")
        self.assertTrue(callable(getattr(self.store, "list_profiles", None)), "缺少网页档案列表入口")
        items = self.store.list_profiles()
        self.assertEqual(["bad", "web-1"], [item["profile"] for item in items])
        self.assertEqual("failed", items[0]["runtime"])
        report = json.dumps(items)
        self.assertNotIn("FAKE_SECRET_TOKEN", report)
        self.assertNotIn(ctl.read_json(self.store.profile("web-1") / "proxy-auth.json")["password"], report)

    def test_runtime_freshness_and_revision_are_reported(self):
        path = self.prepared()
        meta = ctl.read_json(path / "meta.json")
        ctl.atomic_write(path / "runtime.json", {"state": "listening", "at": time.time(), "revision": "older"})
        result = self.store.status("account-1")
        self.assertIn("runtimeFresh", result, "缺少运行状态时效字段")
        self.assertTrue(result["runtimeFresh"])
        self.assertEqual("pending", result["runtime"])
        self.assertEqual(meta["revision"], result["revision"])
        self.assertEqual("older", result["runtimeRevision"])
        ctl.atomic_write(path / "runtime.json", {"state": "listening", "at": time.time() - 11,
                                               "revision": meta["revision"]})
        result = self.store.status("account-1")
        self.assertFalse(result["runtimeFresh"])
        self.assertEqual("unknown", result["runtime"])

    def test_generate_rejects_arbitrary_bind_address_before_cli_runs(self):
        self.store.register("account-1", True)
        self.assertIn("bind_address", ctl.Store.generate.__code__.co_varnames, "缺少监听地址参数")
        for value in ("192.0.2.1", "127.0.0.1\nPassword = injected", "::1"):
            with self.assertRaises(ctl.Failure):
                self.store.generate("account-1", 18081, bind_address=value)
        self.assertFalse((self.store.profile("account-1") / "generated.conf").exists())

    def test_explicit_terms_required_without_registration(self):
        with self.assertRaises(ctl.Failure):
            self.store.register("account-1", False)
        self.assertFalse((self.store.root / "account-1").exists())

    def test_partial_registration_recovers_without_second_registration(self):
        path = self.mode({"partial": True})
        with self.assertRaises(ctl.Failure) as error:
            self.store.register("account-1", True)
        self.assertNotIn("FAKE_SECRET_TOKEN", str(error.exception))
        self.assertTrue(self.store.register("account-1", True)["recovered"])
        self.assertEqual("1", (path / "registrations.txt").read_text())
        self.store.generate("account-1", 18081)

    def test_timeout_with_unknown_result_never_registers_again(self):
        path = self.mode({"timeout": True})
        self.store.timeout = 0.2
        start = time.monotonic()
        with self.assertRaises(ctl.Failure):
            self.store.register("account-1", True)
        # Windows 进程创建/回收受本机安全软件影响；仍须远小于替身的 10 秒休眠。
        self.assertLess(time.monotonic() - start, 5)
        with self.assertRaises(ctl.Failure):
            self.store.register("account-1", True)
        self.assertEqual("1", (path / "registrations.txt").read_text())

    def test_generation_rejects_hooks_and_invalidates_previous_generation(self):
        path = self.prepared()
        self.store.desired("account-1", False)
        ctl.reconcile_profiles(self.store, {}, {})
        ctl.atomic_write(path / "fake-mode.json", {"injection": True})
        with self.assertRaises(ctl.Failure):
            self.store.generate("account-1", 18081)
        self.assertFalse(self.store.status("account-1")["generated"])
        with self.assertRaises(ctl.Failure):
            self.store.desired("account-1", True)

    def test_independent_proxy_passwords_and_exclusive_ports(self):
        first = self.prepared()
        second = self.prepared("account-2", 18082)
        self.assertNotEqual(ctl.read_json(first / "proxy-auth.json"), ctl.read_json(second / "proxy-auth.json"))
        self.store.desired("account-2", False)
        with self.assertRaises(ctl.Failure):
            self.store.generate("account-2", 18081)
        text = (first / "wireproxy.conf").read_text()
        self.assertIn("PrivateKey = ", text)
        self.assertNotIn("TunnelDomains", text)

    def test_status_contains_no_secrets_and_no_egress_claim(self):
        path = self.prepared()
        report = json.dumps(self.store.status("account-1"))
        self.assertNotIn("FAKE_SECRET_TOKEN", report)
        self.assertNotIn(ctl.read_json(path / "proxy-auth.json")["password"], report)
        self.assertFalse(self.store.status("account-1")["egressVerified"])
        if os.name != "nt":
            self.assertEqual(0o600, stat.S_IMODE((path / "proxy-auth.json").stat().st_mode))

    def test_path_traversal_and_symlinks_rejected(self):
        with self.assertRaises(ctl.Failure):
            self.store.profile("../outside", True)
        if os.name != "nt":
            (self.store.root / "linked").symlink_to(self.root, target_is_directory=True)
            with self.assertRaises(ctl.Failure):
                self.store.profile("linked")

    def test_corrupt_profile_does_not_stop_another_running_child(self):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        self.prepared(port=port)
        bad = self.store.profile("bad", True)
        (bad / "meta.json").write_text("{invalid")
        children, retries = {}, {}
        try:
            ctl.reconcile_profiles(self.store, children, retries)
            self.assertIn("account-1", children)
            self.assertIsNone(children["account-1"].poll())
            self.assertEqual("failed", ctl.read_json(bad / "runtime.json")["state"])
        finally:
            for child in children.values():
                ctl.stop_process(child)

    def test_spawn_failure_becomes_safe_runtime_status(self):
        path = self.prepared()
        with patch.object(ctl.subprocess, "Popen", side_effect=OSError("FAKE_SECRET_TOKEN")):
            ctl.reconcile_profiles(self.store, {}, {})
        runtime = (path / "runtime.json").read_text()
        self.assertIn("failed", runtime)
        self.assertNotIn("FAKE_SECRET_TOKEN", runtime)

    def test_generate_waits_for_current_stop_acknowledgement(self):
        path = self.prepared()
        original = (path / "wireproxy.conf").read_text()
        self.store.desired("account-1", False)
        ctl.atomic_write(path / "runtime.json", {"state": "stopped", "at": time.time(), "revision": "older-request"})
        with self.assertRaises(ctl.Failure):
            self.store.generate("account-1", 18082)
        self.assertEqual(original, (path / "wireproxy.conf").read_text())
        ctl.reconcile_profiles(self.store, {}, {})
        self.store.generate("account-1", 18082)
        self.assertNotEqual(original, (path / "wireproxy.conf").read_text())

    def test_stop_generate_start_replaces_actual_child_after_acknowledgement(self):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        path = self.prepared(port=port)
        children, retries = {}, {}
        try:
            ctl.reconcile_profiles(self.store, children, retries)
            old_child = children["account-1"]
            self.store.desired("account-1", False)
            with self.assertRaises(ctl.Failure):
                self.store.generate("account-1", port)
            ctl.reconcile_profiles(self.store, children, retries)
            self.assertIsNotNone(old_child.poll())
            previous_auth = (path / "proxy-auth.json").read_text()
            self.store.generate("account-1", port)
            self.store.desired("account-1", True)
            ctl.reconcile_profiles(self.store, children, retries)
            self.assertNotEqual(old_child.pid, children["account-1"].pid)
            self.assertNotEqual(previous_auth, (path / "proxy-auth.json").read_text())
        finally:
            for child in children.values():
                ctl.stop_process(child)

    @unittest.skipIf(os.name == "nt", "运行器只支持 Linux；Windows 无 POSIX 信号，CI 执行此测试")
    def test_supervisor_start_stop_and_restart_restore_desired(self):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        self.prepared(port=port)
        runner = self.root / "runner.py"
        runner.write_text("import sys\nsys.path.insert(0," + repr(str(ROOT)) + ")\nimport warpctl\n"
                          + "warpctl.supervise(warpctl.Store(" + repr(str(self.store.root))
                          + ", wireproxy=" + repr(self.store.wireproxy) + "))\n")
        process = subprocess.Popen([sys.executable, str(runner)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            self.wait_state("listening")
            ctl.stop_process(process)
            process = subprocess.Popen([sys.executable, str(runner)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            time.sleep(0.7)
            self.wait_state("listening")
            self.store.desired("account-1", False)
            self.wait_state("stopped")
            with self.assertRaises(OSError):
                socket.create_connection(("127.0.0.1", port), timeout=0.2)
        finally:
            ctl.stop_process(process)

    def wait_state(self, expected):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if self.store.status("account-1")["runtime"] == expected:
                return
            time.sleep(0.05)
        self.fail("共享运行器没有进入预期状态")


class PanelTests(Fixture):
    def setUp(self):
        super().setUp()
        self.requests = []
        self.proxies = []
        self.account = {"id": 1, "proxy": {"id": 7, "kind": "manual"}, "useGlobalProxy": False}
        self.test_ok = True
        self.fail_bind = False
        self.lose_create_response = False
        self.redirect = False
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_GET(self):
                self.handle_request()

            def do_POST(self):
                self.handle_request()

            def handle_request(self):
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length)) if length else None
                owner.requests.append((self.command, self.path, body))
                if self.headers.get("Cookie") != "TelegramPanel.Auth=FAKE_COOKIE":
                    self.send_error(401)
                    return
                if owner.redirect:
                    self.send_response(302)
                    self.send_header("Location", "http://example.invalid/secret")
                    self.end_headers()
                    return
                if self.path.startswith("/api/panel/accounts?"):
                    value = {"items": [owner.account], "total": 1}
                elif self.path == "/api/panel/proxies" and self.command == "GET":
                    value = owner.proxies
                elif self.path == "/api/panel/proxies":
                    value = dict(body, id=9, accountCount=0)
                    owner.proxies.append(value)
                    if owner.lose_create_response:
                        owner.lose_create_response = False
                        self.send_error(503)
                        return
                elif self.path == "/api/panel/proxies/9/test":
                    # 复现真实 TestAsync 合同：不加载 Accounts，且 DTO 默认 isGlobal=false。
                    value = dict(owner.proxies[0], testStatus="ok" if owner.test_ok else "failed",
                                 egressIp="192.0.2.1", accountCount=0, isGlobal=False)
                elif self.path == "/api/panel/accounts/1/proxy":
                    if owner.fail_bind:
                        value = {"success": 0, "failed": 1}
                    else:
                        owner.account["proxy"] = {"id": 9, "kind": "wireguard_warp"}
                        owner.account["useGlobalProxy"] = False
                        owner.proxies[0]["accountCount"] = 1
                        value = {"success": 1, "failed": 0}
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps(value).encode())

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=lambda: self.server.serve_forever(poll_interval=0.05), daemon=True)
        self.thread.start()
        self.url = f"http://127.0.0.1:{self.server.server_port}"
        self.credentials = self.root / "panel-auth.json"
        ctl.atomic_write(self.credentials, {"panelUrl": self.url, "cookie": "TelegramPanel.Auth=FAKE_COOKIE"})
        self.panel = ctl.Panel(self.url, self.credentials, allow_http=True)

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        super().tearDown()

    def test_failed_probe_preserves_old_binding_and_recovers_same_proxy(self):
        path = self.prepared()
        self.test_ok = False
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.assertEqual(7, self.account["proxy"]["id"])
        self.assertFalse(any(p.endswith("/1/proxy") for _, p, _ in self.requests))
        self.test_ok = True
        ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.assertEqual(1, len(self.proxies))
        self.assertEqual(9, self.account["proxy"]["id"])
        self.assertEqual(7, ctl.read_json(path / "meta.json")["binding"]["previousProxyId"])
        self.assertEqual(7, next(b["expectedProxyId"] for _, p, b in self.requests if p.endswith("/1/proxy")))

    def test_lost_create_response_recovers_without_duplicate(self):
        self.prepared()
        self.lose_create_response = True
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.assertEqual(1, len(self.proxies))

    def test_binding_failure_is_not_reported_success(self):
        self.prepared()
        self.fail_bind = True
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.assertEqual(7, self.account["proxy"]["id"])
        self.assertFalse(self.store.status("account-1")["bound"])

    def test_operator_binding_change_is_never_overwritten(self):
        self.prepared()
        self.test_ok = False
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.account["proxy"]["id"] = 11
        self.test_ok = True
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.assertEqual(11, self.account["proxy"]["id"])

    def test_managed_warp_switch_rejected_before_creating_anything(self):
        self.prepared()
        self.account["proxy"]["kind"] = "warp"
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.assertEqual([], self.proxies)

    def test_redirect_and_wrong_credential_target_blocked(self):
        with self.assertRaises(ctl.Failure):
            ctl.Panel("https://example.invalid", self.credentials)
        with self.assertRaises(ctl.Failure):
            ctl.Panel(self.url, self.credentials)
        self.redirect = True
        with self.assertRaises(ctl.Failure):
            self.panel.request("GET", "/proxies")

    def test_profile_cannot_bind_second_account(self):
        self.prepared()
        ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.account["id"] = 2
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 2, "tg-wgcf-warp")

    def test_test_dto_default_counts_cannot_hide_real_account_or_global_usage(self):
        self.prepared()
        self.test_ok = False
        with self.assertRaises(ctl.Failure):
            ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.test_ok = True
        for account_count, is_global in ((1, False), (0, True)):
            self.proxies[0].update(accountCount=account_count, isGlobal=is_global)
            with self.assertRaises(ctl.Failure):
                ctl.bind(self.store, "account-1", self.panel, 1, "tg-wgcf-warp")
        self.assertEqual(7, self.account["proxy"]["id"])
        self.assertFalse(any(p.endswith("/1/proxy") for _, p, _ in self.requests))


if __name__ == "__main__":
    unittest.main()
