#!/usr/bin/env python3
"""检查 SPA 入口及其实际引用资源，防止仅凭 HTML 200 误判部署成功。"""

import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from html.parser import HTMLParser


class EntryParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.assets = set()

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        value = attrs.get("src") if tag == "script" else attrs.get("href") if tag == "link" else None
        if value and value.startswith("/ui/assets/"):
            self.assets.add(value)


def check(base_url):
    deadline = time.monotonic() + 50

    def request(path, method="GET"):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError("验收超过 50 秒")
        req = urllib.request.Request(base_url.rstrip("/") + path, method=method)
        try:
            with urllib.request.urlopen(req, timeout=min(10, remaining)) as response:
                return response.status, response.headers, response.read(2 * 1024 * 1024)
        except urllib.error.HTTPError as error:
            return error.code, error.headers, b""

    status, headers, body = request("/ui/dashboard")
    if status != 200 or "text/html" not in headers.get("Content-Type", ""):
        raise RuntimeError(f"入口异常：HTTP {status}")
    if "no-store" not in headers.get("Cache-Control", ""):
        raise RuntimeError("入口没有禁止缓存")
    parser = EntryParser()
    parser.feed(body.decode("utf-8"))
    if not parser.assets or len(parser.assets) > 32:
        raise RuntimeError("入口资源列表为空或超出检查上限")
    for path in sorted(parser.assets):
        suffix = urllib.parse.urlsplit(path).path.rsplit(".", 1)[-1]
        expected = {"js": ("text/javascript", "application/javascript"), "css": ("text/css",)}.get(suffix)
        if expected is None:
            raise RuntimeError(f"未知入口资源类型：{path}")
        for method in ("GET", "HEAD"):
            status, headers, body = request(path, method)
            content_type = headers.get("Content-Type", "").split(";", 1)[0].lower()
            if status != 200 or content_type not in expected or (method == "GET" and not body):
                raise RuntimeError(f"资源异常：{method} {path} HTTP {status} MIME={content_type}")
            if "immutable" not in headers.get("Cache-Control", ""):
                raise RuntimeError(f"资源没有不可变缓存：{path}")
        print(f"PASS {path}")
    status, headers, _ = request("/ui/assets/__deployment_missing_asset__.js")
    if status != 404 or "no-store" not in headers.get("Cache-Control", ""):
        raise RuntimeError(f"缺失资源必须返回禁止缓存的 404，实际 HTTP {status}")
    print(f"PASS SPA 入口、{len(parser.assets)} 个资源 GET/HEAD、缺失资源 404")


if __name__ == "__main__":
    try:
        check(sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5000")
    except Exception as error:
        print(f"FAIL {error}", file=sys.stderr)
        sys.exit(1)
