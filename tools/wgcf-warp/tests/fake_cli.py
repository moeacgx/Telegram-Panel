"""测试替身：只写临时材料和本地监听，不调用 Cloudflare 或 WireGuard。"""
import base64
import json
from pathlib import Path
import socket
import sys
import time

args = sys.argv[1:]
if args[0] == "wgcf":
    path = Path(args[args.index("--config") + 1])
    mode_file = path.parent / "fake-mode.json"
    mode = json.loads(mode_file.read_text()) if mode_file.exists() else {}
    if "register" in args:
        count = path.parent / "registrations.txt"
        count.write_text(str(int(count.read_text()) + 1 if count.exists() else 1))
        if mode.get("timeout"):
            time.sleep(10)
        key = base64.b64encode(bytes(range(32))).decode()
        path.write_text(f'device_id = "fake-device"\naccess_token = "FAKE_SECRET_TOKEN"\nprivate_key = "{key}"\n')
        if mode.get("partial"):
            print("FAKE_SECRET_TOKEN", file=sys.stderr)
            sys.exit(3)
    else:
        key = base64.b64encode(bytes(range(32))).decode()
        output = Path(args[args.index("--profile") + 1])
        output.write_text(f"[Interface]\nPrivateKey = {key}\nAddress = 172.16.0.2/32, 2606:4700::1/128\nDNS = 1.1.1.1\nMTU = 1280\n[Peer]\nPublicKey = {key}\nAllowedIPs = 0.0.0.0/0, ::/0\nEndpoint = engage.cloudflareclient.com:2408\n"
                          + ("PostUp = echo FAKE_SECRET_TOKEN\n" if mode.get("injection") else ""))
elif "-n" not in args:
    import configparser
    config = configparser.ConfigParser()
    config.read(args[args.index("-c") + 1])
    port = int(config["Socks5"]["BindAddress"].rsplit(":", 1)[1])
    with socket.socket() as server:
        server.bind(("127.0.0.1", port))
        server.listen()
        while True:
            client, _ = server.accept()
            client.close()
