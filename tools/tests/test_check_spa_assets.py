"""验证部署探针能够识别入口成功但资源损坏的情况。"""

import contextlib
import importlib.util
import io
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
import unittest

spec = importlib.util.spec_from_file_location("probe", Path(__file__).parents[1] / "check-spa-assets.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class ProbeTests(unittest.TestCase):
    def run_probe(self, mode):
        class Handler(BaseHTTPRequestHandler):
            def do_HEAD(self):
                self.do_GET()

            def do_GET(self):
                status, mime, cache, body = 200, "text/html", "no-store", b'<script src="/ui/assets/app.js"></script><link href="/ui/assets/app.css">'
                if self.path.startswith("/ui/assets/"):
                    if "__deployment_missing" in self.path or mode == "missing":
                        status, body = 404, b""
                    else:
                        mime = "text/javascript" if self.path.endswith(".js") else "text/css"
                        cache, body = "public, max-age=31536000, immutable", b"/* resource */"
                        if mode == "html":
                            mime = "text/html"
                        if mode == "head-failure" and self.command == "HEAD":
                            status = 404
                self.send_response(status)
                self.send_header("Content-Type", mime)
                self.send_header("Cache-Control", cache)
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(body)

            def log_message(self, *_):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                probe.check(f"http://127.0.0.1:{server.server_port}")
        finally:
            server.shutdown()
            thread.join(timeout=5)
            server.server_close()

    def test_valid_assets_pass(self):
        self.run_probe("ok")

    def test_html_200_with_missing_assets_fails(self):
        with self.assertRaisesRegex(RuntimeError, "HTTP 404"):
            self.run_probe("missing")

    def test_html_fallback_instead_of_javascript_fails(self):
        with self.assertRaisesRegex(RuntimeError, "MIME=text/html"):
            self.run_probe("html")

    def test_head_failure_fails(self):
        with self.assertRaisesRegex(RuntimeError, "HEAD.*HTTP 404"):
            self.run_probe("head-failure")


if __name__ == "__main__":
    unittest.main()
