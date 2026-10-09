"""构建时下载固定官方发布资产；校验失败即停止，绝不运行未验证二进制。"""
import hashlib
import io
import json
from pathlib import Path
import sys
import tarfile
import urllib.request


def install(architecture, target):
    manifest = json.loads(Path(__file__).with_name("dependencies.json").read_text())
    target.mkdir(parents=True, exist_ok=True)
    for name, package in manifest.items():
        asset = package[architecture]
        with urllib.request.urlopen(asset["url"], timeout=60) as response:
            data = response.read(100 * 1024 * 1024 + 1)
        if len(data) > 100 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != asset["sha256"]:
            raise RuntimeError("依赖资产大小或 SHA256 校验失败")
        if name == "wireproxy":
            with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
                members = [member for member in archive.getmembers() if member.name == "wireproxy" and member.isfile()]
                if len(members) != 1 or members[0].size > 100 * 1024 * 1024:
                    raise RuntimeError("归档中没有唯一普通二进制文件")
                data = archive.extractfile(members[0]).read()
        executable = target / name
        executable.write_bytes(data)
        executable.chmod(0o755)


if __name__ == "__main__":
    install(sys.argv[1], Path(sys.argv[2]))
