"""在隔离子进程耗尽并释放 fd；只向 stdout 报告白名单结果。"""
import errno
import json
import os
import resource

fds = []
exhausted = False
try:
    for _ in range(256):
        try:
            fds.append(os.open(os.devnull, os.O_RDONLY))
        except OSError as error:
            if error.errno != errno.EMFILE:
                raise
            exhausted = True
            break
finally:
    for fd in fds:
        os.close(fd)
fd = os.open(os.devnull, os.O_RDONLY)
os.close(fd)
print(json.dumps({"exhausted": exhausted, "recovered": True,
                  "nofile": resource.getrlimit(resource.RLIMIT_NOFILE)}))
