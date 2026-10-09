#!/usr/bin/env python3
"""共享容器中的 WARP 运维工具；只输出白名单状态，不输出上游日志或凭据。"""
import argparse
import base64
import configparser
import contextlib
import json
import os
from pathlib import Path
import re
import secrets
import signal
import socket
import stat
import subprocess
import sys
import time
import tomllib
import urllib.error
import urllib.parse
import urllib.request


class Failure(Exception):
    pass


def read_json(path, default=None):
    if not path.exists():
        return default
    return json.loads(path.read_text(encoding="utf-8"))


def atomic_write(path, value):
    # 同目录原子替换；即使进程中断，也不会留下半份可执行配置。
    target = path.with_name(path.name + ".tmp")
    if path.is_symlink() or target.is_symlink():
        raise Failure("拒绝符号链接文件")
    fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
        os.chmod(target, 0o600)
        stream.write(value if isinstance(value, str) else json.dumps(value, ensure_ascii=False))
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(target, path)


@contextlib.contextmanager
def lock(path):
    with open(path, "a+b") as stream:
        if os.name == "nt":
            import msvcrt
            stream.seek(0)
            stream.write(b"0")
            stream.flush()
            stream.seek(0)
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError:
                raise Failure("另一操作正在执行，请稍后重试") from None
        else:
            import fcntl
            try:
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise Failure("另一操作正在执行，请稍后重试") from None
        try:
            yield
        finally:
            if os.name == "nt":
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def run_private(command, timeout, cwd=None):
    # 上游错误可能包含账户材料，因此 stdout/stderr 不进入日志、异常或面板。
    try:
        result = subprocess.run(command, cwd=cwd, stdin=subprocess.DEVNULL,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        raise Failure("上游命令超时；已停止进程，保留账户材料供恢复") from None
    except OSError:
        raise Failure("上游程序无法启动，请检查镜像和可执行文件") from None
    if result.returncode:
        raise Failure("上游命令失败；保留账户材料，先核对网络与上游状态后重试")


def key_valid(value):
    try:
        return len(base64.b64decode(value, validate=True)) == 32
    except (ValueError, TypeError):
        return False


def valid_host(value):
    return bool(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.\-]{0,252}", value or ""))


class Store:
    def __init__(self, root, wgcf=None, wireproxy=None, timeout=45):
        self.root = Path(root).absolute()
        if self.root.is_symlink():
            raise Failure("数据目录不能为符号链接")
        self.root.mkdir(mode=0o700, parents=True, exist_ok=True)
        os.chmod(self.root, 0o700)
        self.wgcf = wgcf or ["wgcf"]
        self.wireproxy = wireproxy or ["wireproxy"]
        self.timeout = timeout

    def profile(self, name, create=False):
        if not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,47}", name):
            raise Failure("配置名只允许小写字母、数字、下划线和短横线，最长 48 字符")
        path = self.root / name
        if path.is_symlink():
            raise Failure("配置目录不能为符号链接")
        if create:
            if not path.exists() and len(list(self.root.glob("*/meta.json"))) >= 100:
                raise Failure("达到工具的 100 份配置保护上限；这不是容量承诺")
            path.mkdir(mode=0o700, exist_ok=True)
        if not path.is_dir():
            raise Failure("配置不存在")
        if any(p.is_symlink() for p in path.iterdir()):
            raise Failure("配置目录内不能有符号链接")
        return path

    def account(self, path):
        file = path / "wgcf-account.toml"
        if not file.exists():
            return None
        try:
            account = tomllib.loads(file.read_text(encoding="utf-8"))
            if all(account.get(k) for k in ("device_id", "access_token", "private_key")) and key_valid(account["private_key"]):
                os.chmod(file, 0o600)
                return account
        except (ValueError, OSError):
            pass
        return None

    def register(self, name, accept_tos):
        if not accept_tos:
            raise Failure("注册要求管理员阅读 Cloudflare 条款后显式传入 --accept-tos")
        path = self.profile(name, True)
        meta = read_json(path / "meta.json", {})
        if self.account(path):
            meta["registered"] = True
            atomic_write(path / "meta.json", meta)
            return {"profile": name, "registered": True, "recovered": True}
        if meta.get("registrationAttempted") or (path / "wgcf-account.toml").exists():
            raise Failure("先前注册结果不明确，禁止自动重复注册；请保留目录并人工核对上游设备")
        meta.update(registrationAttempted=True, termsAcceptedAt=int(time.time()))
        atomic_write(path / "meta.json", meta)
        try:
            run_private(self.wgcf + ["--config", str(path / "wgcf-account.toml"),
                                    "register", "--accept-tos"], self.timeout, path)
        finally:
            if (path / "wgcf-account.toml").exists():
                os.chmod(path / "wgcf-account.toml", 0o600)
        if not self.account(path):
            raise Failure("注册命令未生成完整账户材料，保留现场并停止")
        meta["registered"] = True
        atomic_write(path / "meta.json", meta)
        return {"profile": name, "registered": True}

    def generate(self, name, port):
        path = self.profile(name)
        account = self.account(path)
        if not account:
            raise Failure("账户材料不完整，请先注册或恢复")
        meta = read_json(path / "meta.json", {})
        if meta.get("desired") or meta.get("binding"):
            raise Failure("启动或已关联面板的配置不能重新生成；请使用新配置名")
        if not 1024 <= port <= 65535:
            raise Failure("监听端口必须介于 1024 和 65535")
        for other in self.root.glob("*/meta.json"):
            if other.parent != path and read_json(other, {}).get("port") == port:
                raise Failure("监听端口已被其它配置占用")
        meta["generated"] = False
        atomic_write(path / "meta.json", meta)
        generated = path / "generated.conf"
        run_private(self.wgcf + ["--config", str(path / "wgcf-account.toml"),
                                "generate", "--profile", str(generated)], self.timeout, path)
        parser = configparser.ConfigParser(interpolation=None, strict=True)
        parser.read_string(generated.read_text(encoding="utf-8"))
        if set(parser.sections()) != {"Interface", "Peer"}:
            raise Failure("上游配置节不符合预期")
        private_key = parser.get("Interface", "PrivateKey")
        public_key = parser.get("Peer", "PublicKey")
        if private_key != account["private_key"] or not key_valid(public_key):
            raise Failure("生成配置与注册身份不匹配")
        # 只复制上游预期字段；禁止执行钩子、外部配置、按域名直连回退。
        allowed = {"Interface": {"address", "privatekey", "dns", "mtu"},
                   "Peer": {"publickey", "allowedips", "endpoint", "persistentkeepalive"}}
        for section in parser.sections():
            if set(parser[section]) - allowed[section]:
                raise Failure("上游配置包含不支持的字段")
            if any("\n" in value or "$" in value for value in parser[section].values()):
                raise Failure("配置值包含不允许的插值或换行")
        if parser.get("Peer", "AllowedIPs", fallback="").replace(" ", "") != "0.0.0.0/0,::/0":
            raise Failure("必须通过 WireGuard 路由全部 IPv4/IPv6 目标")
        auth = {"username": "tp_" + secrets.token_hex(6), "password": secrets.token_hex(24)}
        names = {key.lower(): key for key in ("Address", "PrivateKey", "DNS", "MTU", "PublicKey",
                                               "AllowedIPs", "Endpoint", "PersistentKeepalive")}
        body = "\n".join("[" + section + "]\n" + "\n".join(f"{names[k]} = {v}" for k, v in parser[section].items())
                         for section in ("Interface", "Peer"))
        body += f"\n[Socks5]\nBindAddress = 0.0.0.0:{port}\nUsername = {auth['username']}\nPassword = {auth['password']}\n"
        atomic_write(path / "wireproxy.conf", body)
        atomic_write(path / "proxy-auth.json", auth)
        run_private(self.wireproxy + ["-n", "-c", str(path / "wireproxy.conf")], self.timeout, path)
        meta.update(port=port, generated=True, desired=False)
        atomic_write(path / "meta.json", meta)
        return {"profile": name, "generated": True, "port": port}

    def desired(self, name, enabled):
        path = self.profile(name)
        meta = read_json(path / "meta.json", {})
        if enabled and not meta.get("generated"):
            raise Failure("请先生成并验证 WireGuard 配置")
        meta["desired"] = enabled
        atomic_write(path / "meta.json", meta)
        return self.status(name)

    def status(self, name):
        path = self.profile(name)
        meta = read_json(path / "meta.json", {})
        runtime = read_json(path / "runtime.json", {})
        fresh = time.time() - runtime.get("at", 0) < 10
        return {"profile": name, "registered": bool(self.account(path)),
                "generated": bool(meta.get("generated")), "port": meta.get("port"),
                "desired": bool(meta.get("desired")),
                "runtime": runtime.get("state", "unknown") if fresh else "unknown",
                "proxyId": meta.get("binding", {}).get("proxyId"),
                "accountId": meta.get("binding", {}).get("accountId"),
                "bound": bool(meta.get("binding", {}).get("bound")),
                "egressVerified": False}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise Failure("面板 API 重定向被拒绝；请使用最终地址，避免凭据跨站")


class Panel:
    def __init__(self, url, credentials, allow_http=False):
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username
                or parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/")):
            raise Failure("面板地址必须是不含凭据和路径的 HTTP(S) 来源地址")
        if parsed.scheme == "http" and not allow_http:
            raise Failure("明文内网地址需要显式 --allow-http；公网使用 HTTPS")
        self.url = url.rstrip("/")
        credentials = Path(credentials)
        if credentials.is_symlink() or not credentials.is_file():
            raise Failure("凭据必须为普通文件")
        if os.name != "nt" and stat.S_IMODE(credentials.stat().st_mode) & 0o077:
            raise Failure("凭据文件权限必须为 0600 或 0400")
        auth = read_json(credentials)
        if auth.get("panelUrl", "").rstrip("/") != self.url:
            raise Failure("凭据文件的 panelUrl 与目标地址不匹配")
        self.cookie = auth.get("cookie", "")
        if not self.cookie or any(c in self.cookie for c in "\r\n"):
            raise Failure("凭据文件中缺少有效管理员 Cookie")
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def request(self, method, path, payload=None):
        body = None if payload is None else json.dumps(payload).encode()
        request = urllib.request.Request(self.url + "/api/panel" + path, data=body, method=method,
                                         headers={"Cookie": self.cookie, "Content-Type": "application/json"})
        try:
            deadline = time.monotonic() + 15
            with self.opener.open(request, timeout=15) as response:
                raw = bytearray()
                while True:
                    if time.monotonic() >= deadline:
                        raise Failure("面板 API 响应超时；请回读状态后恢复")
                    chunk = response.read1(64 * 1024)
                    if not chunk:
                        break
                    raw.extend(chunk)
                    if len(raw) > 4 * 1024 * 1024:
                        raise Failure("面板 API 响应超过大小上限")
                return json.loads(raw)
        except urllib.error.HTTPError as error:
            raise Failure(f"面板 API HTTP {error.code}；原始响应已隐藏") from None
        except (urllib.error.URLError, TimeoutError, ValueError):
            raise Failure("面板 API 网络或响应异常；变更结果可能不明确，请原参数重试") from None

    def account(self, account_id):
        # 列表 DTO 才包含当前路由；详情 DTO 包含敏感信息且不提供代理。
        for page in range(1, 501):
            result = self.request("GET", f"/accounts?page={page}&pageSize=200")
            for item in result["items"]:
                if item["id"] == account_id:
                    return item
            if page * 200 >= result["total"]:
                break
        raise Failure("账号 ID 不存在或超过分页保护上限")


def bind(store, name, panel, account_id, host):
    if not valid_host(host) or account_id <= 0:
        raise Failure("请提供有效内部代理主机名和账号数据库 ID")
    path = store.profile(name)
    meta = read_json(path / "meta.json", {})
    if not meta.get("generated") or not meta.get("desired"):
        raise Failure("请先生成并启动配置")
    account = panel.account(account_id)
    old = account.get("proxy")
    if old and old.get("kind") == "warp":
        raise Failure("面板会自动清理被替换的受管 WARP；请先在面板人工安排迁移，本工具不自动切换")
    state = meta.get("binding")
    if state and (state["panelUrl"] != panel.url or state["accountId"] != account_id or state["host"] != host):
        raise Failure("配置已经关联其它面板、账号或主机，禁止复用身份")
    if not state:
        state = {"panelUrl": panel.url, "accountId": account_id, "host": host,
                 "name": "wgcf-" + name + "-" + secrets.token_hex(6),
                 "previousProxyId": old["id"] if old else 0,
                 "previousUseGlobalProxy": bool(account.get("useGlobalProxy")), "bound": False}
        meta["binding"] = state
        atomic_write(path / "meta.json", meta)
    # 创建超时后通过随机唯一名称恢复，禁止盲目重试 POST 产生重复代理。
    proxies = panel.request("GET", "/proxies")
    matches = [p for p in proxies if p["name"] == state["name"]]
    if len(matches) > 1:
        raise Failure("发现重复恢复标识，请人工核对代理")
    auth = read_json(path / "proxy-auth.json")
    if matches:
        proxy = matches[0]
        if (proxy["kind"], proxy["protocol"], proxy["host"], proxy["port"], proxy.get("username")) != (
                "wireguard_warp", "socks5", host, meta["port"], auth["username"]):
            raise Failure("现有代理与本地档案不一致，停止恢复")
    else:
        if state.get("createAttempted"):
            raise Failure("先前创建结果不明确且未查到代理；禁止自动重复创建，请人工核对")
        state["createAttempted"] = True
        atomic_write(path / "meta.json", meta)
        proxy = panel.request("POST", "/proxies", {"name": state["name"], "kind": "wireguard_warp",
            "protocol": "socks5", "host": host, "port": meta["port"], "username": auth["username"],
            "password": auth["password"], "isEnabled": True, "testAfterSave": False})
    state["proxyId"] = proxy["id"]
    atomic_write(path / "meta.json", meta)
    proxy = panel.request("POST", f"/proxies/{proxy['id']}/test", {})
    if proxy.get("testStatus") != "ok" or not proxy.get("egressIp") or not proxy.get("isEnabled"):
        raise Failure("新出口未通过面板 WARP 检测，未修改账号绑定")
    if proxy.get("isGlobal") or proxy.get("accountCount", 0) > (1 if old and old["id"] == proxy["id"] else 0):
        raise Failure("代理被全局或其它账号使用，不能作为一对一出口")
    current = panel.account(account_id)
    current_id = (current.get("proxy") or {}).get("id", 0)
    if current_id == proxy["id"]:
        state["bound"] = True
    else:
        if current_id != state["previousProxyId"] or bool(current.get("useGlobalProxy")) != state["previousUseGlobalProxy"]:
            raise Failure("账号原绑定已变化，未覆盖人工修改")
        result = panel.request("POST", f"/accounts/{account_id}/proxy",
                               {"strategy": "existing", "proxyId": proxy["id"], "expectedProxyId": current_id})
        if result.get("failed") != 0 or result.get("success") != 1:
            raise Failure("面板未确认绑定成功，保留原档案供核对或重试")
        confirmed = panel.account(account_id)
        if (confirmed.get("proxy") or {}).get("id") != proxy["id"]:
            raise Failure("绑定回读不一致，请人工核对")
        state["bound"] = True
    atomic_write(path / "meta.json", meta)
    observed = next((p for p in panel.request("GET", "/proxies") if p["id"] == proxy["id"]), None)
    if not observed or observed.get("isGlobal") or observed.get("accountCount") != 1:
        raise Failure("绑定已提交，但出口共享状态回读异常；请人工核对，未自动回滚")
    return {"profile": name, "accountId": account_id, "proxyId": proxy["id"], "bound": True}


def reconcile_profiles(store, children, retries):
    for file in store.root.glob("*/meta.json"):
        name = file.parent.name
        path = None
        try:
            path = store.profile(name)
            meta = read_json(file, {})
            process = children.get(name)
            if process and process.poll() is not None:
                children.pop(name)
                retries[name] = time.monotonic() + 30
                process = None
            if process and not meta.get("desired"):
                stop_process(process)
                children.pop(name)
                process = None
            state = "stopped"
            if meta.get("desired") and meta.get("generated"):
                port = meta["port"]
                if not isinstance(port, int) or not 1024 <= port <= 65535:
                    raise Failure("档案端口无效")
                if not process and time.monotonic() >= retries.get(name, 0):
                    process = subprocess.Popen(store.wireproxy + ["-s", "-c", str(path / "wireproxy.conf")],
                        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    children[name] = process
                state = "backoff" if not process else "starting"
                if process and process.poll() is None:
                    try:
                        with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                            state = "listening"
                    except OSError:
                        pass
            atomic_write(path / "runtime.json", {"state": state, "at": time.time()})
        except (Failure, OSError, ValueError, KeyError, TypeError, AttributeError):
            # 一份档案损坏或无法启动不能终止其它出口；原始异常可能包含凭据，不记录。
            process = children.pop(name, None)
            if process:
                with contextlib.suppress(OSError, subprocess.TimeoutExpired):
                    stop_process(process)
            retries[name] = time.monotonic() + 30
            if path:
                with contextlib.suppress(OSError, Failure):
                    atomic_write(path / "runtime.json", {"state": "failed", "at": time.time()})


def supervise(store):
    children, retries = {}, {}
    stopping = False

    def stop_signal(_signum, _frame):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop_signal)
    signal.signal(signal.SIGINT, stop_signal)
    with lock(store.root / ".supervisor.lock"):
        try:
            while not stopping:
                reconcile_profiles(store, children, retries)
                time.sleep(0.5)
        finally:
            for process in children.values():
                stop_process(process)


def stop_process(process):
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", default="/data")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("serve")
    for verb in ("register", "generate", "start", "status", "stop"):
        command = commands.add_parser(verb)
        command.add_argument("profile")
        if verb == "register":
            command.add_argument("--accept-tos", action="store_true")
        if verb == "generate":
            command.add_argument("--port", type=int, required=True)
    command = commands.add_parser("bind")
    command.add_argument("--accounts", required=True, help="数据库账号 ID，以逗号分隔；自动对应 account-ID 配置")
    command.add_argument("--panel-url", required=True)
    command.add_argument("--credentials", required=True)
    command.add_argument("--proxy-host", default="tg-wgcf-warp")
    command.add_argument("--allow-http", action="store_true")
    args = parser.parse_args()
    try:
        store = Store(args.data)
        if args.command == "serve":
            supervise(store)
            return 0
        with lock(store.root / ".operations.lock"):
            if args.command == "register":
                result = store.register(args.profile, args.accept_tos)
            elif args.command == "generate":
                result = store.generate(args.profile, args.port)
            elif args.command in ("start", "stop"):
                result = store.desired(args.profile, args.command == "start")
            elif args.command == "status":
                result = store.status(args.profile)
            else:
                ids = [int(value) for value in args.accounts.split(",")]
                if not 1 <= len(ids) <= 10 or len(set(ids)) != len(ids) or any(i <= 0 for i in ids):
                    raise Failure("一次绑定 1～10 个不同的正整数账号 ID")
                panel = Panel(args.panel_url, args.credentials, args.allow_http)
                result = []
                for account_id in ids:
                    # 逐账号提交；前面已成功的项不因后续项失败而撤销。
                    item = bind(store, f"account-{account_id}", panel, account_id, args.proxy_host)
                    result.append(item)
                    print(json.dumps(item, ensure_ascii=False), flush=True)
                return 0
            print(json.dumps(result, ensure_ascii=False))
        return 0
    except Failure as error:
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError, configparser.Error):
        print('{"success":false,"error":"本地配置或响应结构无效；详细值已隐藏，请核对文档"}', file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
