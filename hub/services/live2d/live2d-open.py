# 作者：Codex（GPT-6）· 2026-10-21
"""启动统一 Live2D；--check 在 8 秒内核验进程、动态清单和旧网址，不打开浏览器。"""
import argparse
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener

HERE = Path(__file__).resolve().parent
PYTHON = Path(r'D:\anaconda3\python.exe')
SERVER = HERE / 'live2d.py'
ROOT = Path(r'F:\5_editSourceFile\Our Notes\Live2D\其他角色_20261013')
TEMP = Path(r'E:\AgentData\Work')
DEFAULT_UIKA = '006_adv-uika_006_live_01'
PORTS = (8999, 8998)

PROCESS_QUERY = r'''
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$rows = @(Get-CimInstance Win32_Process -Filter '__PID_FILTER__' | ForEach-Object {
  [pscustomobject]@{pid=$_.ProcessId; executable=$_.ExecutablePath; commandline=$_.CommandLine}
})
ConvertTo-Json -InputObject $rows -Compress
'''


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


OPENER = build_opener(ProxyHandler({}), NoRedirect())


def child_environment():
    physical = TEMP.resolve()
    if physical.drive.upper() != 'E:':
        raise RuntimeError('Live2D 临时目录需要位于 E 盘。')
    physical.mkdir(parents=True, exist_ok=True)
    value = os.environ.copy()
    value.update(TEMP=str(physical), TMP=str(physical), TMPDIR=str(physical), PYTHONDONTWRITEBYTECODE='1')
    return value


def remaining(deadline, maximum):
    seconds = deadline - time.monotonic()
    if seconds <= 0:
        raise RuntimeError('统一 Live2D 启动等待已达到 8 秒，请查看端口或模型目录。')
    return min(seconds, maximum)


def listeners(deadline, environment):
    sockets = subprocess.run(
        ['netstat.exe', '-ano', '-p', 'tcp'], capture_output=True,
        encoding='ascii', errors='replace', timeout=remaining(deadline, 2),
        env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
    if sockets.returncode:
        raise RuntimeError('读取 Live2D 监听端口失败。')
    rows = []
    for line in sockets.stdout.splitlines():
        fields = line.split()
        if len(fields) == 5 and fields[0] == 'TCP' and fields[3] == 'LISTENING':
            address, port = fields[1].rsplit(':', 1)
            if port.isdigit() and int(port) in PORTS:
                rows.append({'port': int(port), 'address': address, 'pid': int(fields[4])})
    if not rows:
        return []
    query = PROCESS_QUERY.replace('__PID_FILTER__', ' OR '.join(
        'ProcessId = ' + str(pid) for pid in sorted({row['pid'] for row in rows})))
    result = subprocess.run(
        ['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', query],
        capture_output=True, encoding='utf-8-sig', errors='replace',
        timeout=remaining(deadline, 3), env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError('读取 Live2D 端口进程失败。')
    value = json.loads(result.stdout.strip() or '[]')
    processes = value if isinstance(value, list) else [value]
    identities = {int(process['pid']): process for process in processes}
    for row in rows:
        row.update(identities.get(row['pid'], {}))
    return rows


def verify_processes(rows):
    found = {}
    for row in rows:
        port = int(row['port'])
        tokens = [part.strip('"') for part in shlex.split(row.get('commandline') or '', posix=False)]
        arguments = tokens[2:]
        identity_ok = (
            row.get('address') == '127.0.0.1'
            and Path(row.get('executable') or '').resolve() == PYTHON.resolve()
            and len(tokens) >= 3 and Path(tokens[1]).resolve() == SERVER.resolve()
            and arguments in ([str(port)], ['--port', str(port)]))
        if not identity_ok or port in found:
            raise RuntimeError(f'端口 {port} 正由其他程序使用，请先检查该端口。')
        found[port] = int(row['pid'])
    return found


def valid_health(value, port=8999):
    if not isinstance(value, dict) or value.get('kind') != 'OurNotesLive2D':
        return False
    if Path(value.get('root') or '').resolve() != ROOT.resolve() or value.get('port') != port:
        return False
    if Path(value.get('runtimeRoot') or '').resolve() != HERE:
        return False
    if not re.fullmatch(r'[a-f0-9]{64}', str(value.get('catalogVersion', ''))):
        return False
    libraries = value.get('libraries')
    if not isinstance(libraries, dict) or set(libraries) != {'bdon', 'gbp'}:
        return False
    total = 0
    for lib, cubism in (('bdon', 4), ('gbp', 2)):
        row = libraries[lib]
        if not isinstance(row, dict) or row.get('cubism') != cubism:
            return False
        counts = [row.get('models'), row.get('characters')]
        if any(type(count) is not int or count < 0 for count in counts) or counts[1] > counts[0]:
            return False
        total += counts[0]
    return (total == value.get('models') and total > 0 and libraries['bdon']['models'] > 0
            and type(value.get('characters')) is int and value['characters'] > 0
            and type(value.get('uika_models')) is int and value['uika_models'] > 0)


def ready(port, deadline):
    path = '/api/health' if port == 8999 else '/'
    try:
        with OPENER.open(Request(f'http://127.0.0.1:{port}{path}'),
                         timeout=remaining(deadline, .5)) as response:
            if port == 8998:
                raise RuntimeError('初华旧网址仍在显示独立播放器，请检查 8998 的启动程序。')
            value = json.loads(response.read(16384))
            if not valid_health(value):
                raise RuntimeError('统一 Live2D 的服务身份或模型清单格式与当前程序不符。')
            return value
    except HTTPError as exc:
        if port == 8998 and exc.code == 302:
            target = urlsplit(exc.headers.get('Location', ''))
            if (target.scheme == 'http' and target.netloc == '127.0.0.1:8999'
                    and target.path == '/' and parse_qs(target.query).get('modelId') == [DEFAULT_UIKA]):
                return True
        raise RuntimeError(f'端口 {port} 的 Live2D 响应异常。') from exc
    except (URLError, TimeoutError, ConnectionError):
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--model-id')
    args = parser.parse_args()
    if args.model_id and not re.fullmatch(r'(?:gbp-[A-Za-z0-9_-]+|[A-Za-z0-9_-]+)', args.model_id):
        parser.error('模型编号格式错误')
    if args.model_id and len(args.model_id) > 220:
        parser.error('模型编号过长')
    if not PYTHON.is_file() or not SERVER.is_file():
        parser.error('统一 Live2D 的 Python 或服务程序不存在')
    deadline = time.monotonic() + 8
    started = []
    success = False
    try:
        environment = child_environment()
        before = verify_processes(listeners(deadline, environment))
        for port in PORTS:
            if port not in before:
                started.append(subprocess.Popen(
                    [str(PYTHON), str(SERVER), str(port)], cwd=str(HERE), env=environment,
                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    close_fds=True, creationflags=subprocess.CREATE_NO_WINDOW))
        pending = set(PORTS)
        health = None
        while pending:
            for port in tuple(pending):
                value = ready(port, deadline)
                if value:
                    if port == 8999:
                        health = value
                    pending.remove(port)
            if pending:
                for process in started:
                    if process.poll() is not None:
                        raise RuntimeError('Live2D 服务启动失败，请检查模型归档和端口。')
                time.sleep(remaining(deadline, .15))
        after = verify_processes(listeners(deadline, environment))
        if set(after) != set(PORTS):
            raise RuntimeError('统一 Live2D 的监听端口未齐全。')
        success = True
        url = 'http://127.0.0.1:8999/'
        if args.model_id:
            url += '?' + urlencode({'modelId': args.model_id})
        if args.check:
            print(json.dumps({'author': 'Codex (GPT-6)', 'status': 'ready',
                              'ports': {str(port): pid for port, pid in after.items()},
                              'started_pids': [process.pid for process in started],
                              'characters': health['characters'], 'models': health['models'],
                              'uika_models': health['uika_models'], 'libraries': health['libraries'],
                              'catalogVersion': health['catalogVersion'], 'url': url}, ensure_ascii=False))
        else:
            os.startfile(url)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as exc:
        print('Live2D 启动失败：' + str(exc), file=sys.stderr)
        return 1
    finally:
        if not success:
            for process in started:
                if process.poll() is None:
                    process.terminate()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
