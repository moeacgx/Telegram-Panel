"""校验真实 wireproxy 解析合同；仅 configtest，不建立隧道或注册。"""
from pathlib import Path
import sys
import tempfile

sys.path.insert(0, "/app")
import warpctl

with tempfile.TemporaryDirectory() as directory:
    store = warpctl.Store(directory, wgcf=[sys.executable, str(Path(__file__).with_name("fake_cli.py")), "wgcf"])
    store.register("fake-account", True)
    # generate 调用真实 wireproxy -n；注册严格使用上面显式指定的本地替身。
    result = store.generate("fake-account", 18081)
    assert result["generated"]
    print("真实 wireproxy 已接受工具生成的配置；未注册、未启动隧道")
