# 作者：dsh（DeepSeek-V4.1-Flash）· 2026-10-31
"""审阅服务看门狗：每 20 秒检查 3955，发现宕了就重新拉起，并记一行日志。"""
import subprocess, time, socket, datetime
from pathlib import Path

PORT = 3955
SCRIPT = Path(r'D:/2-视频制作/0-工作流/审阅服务/审阅服务.py')
PYTHON = Path(r'D:/anaconda3/python.exe')
LOG = Path(r'E:/AgentData/Work/review-service-watchdog.log')


def alive():
    with socket.socket() as s:
        s.settimeout(2)
        return s.connect_ex(('127.0.0.1', PORT)) == 0


def log(msg):
    LOG.parent.mkdir(parents=True, exist_ok=True)
    with LOG.open('a', encoding='utf-8') as f:
        f.write('%s %s\n' % (datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S'), msg))


if __name__ == '__main__':
    log('watchdog start')
    while True:
        try:
            if not alive():
                out = open(LOG.parent/'review-service.out.log', 'ab')
                proc = subprocess.Popen([str(PYTHON), str(SCRIPT), str(PORT)],
                                        cwd=str(SCRIPT.parent), stdin=subprocess.DEVNULL,
                                        stdout=out, stderr=out,
                                        creationflags=0x08000000)
                log('restart pid=%d' % proc.pid)
                time.sleep(6)
        except Exception as exc:
            log('watchdog error %s' % type(exc).__name__)
        time.sleep(20)
