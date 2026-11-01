"""Bounded, credential-free schedule inventory and native Hermes changes.

JSON in on stdin; JSON out on stdout. This module never returns task prompts,
configuration secrets, raw native command output, or session contents.
"""

from __future__ import annotations

from datetime import datetime, timezone
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
import io
import json
import os
import re
import subprocess
import sys
import tomllib

sys.dont_write_bytecode = True

HERMES_HOME = Path('E:/Hermes')
HERMES_CLI = HERMES_HOME / 'bin/hermes.exe'
CODEX_AUTOMATIONS = Path('C:/Users/10939/.codex/automations')
POLICY = HERMES_HOME / 'shared/policies/小红书收藏夹采集规则.md'
SETTINGS = HERMES_HOME / 'shared/workbench/settings.json'
HERMES_TEMP = Path('E:/AgentData/Work/HermesTemp')
REVISION = 'XHS_COLLECTION_RULE_20261021'
MAX_FILE_BYTES = 2 * 1024 * 1024
MAX_ROWS = 500
PROFILE_RE = re.compile(r'^[A-Za-z0-9_.-]{1,80}$')
ID_RE = re.compile(r'^[A-Za-z0-9_.-]{1,128}$')


class AdapterError(Exception):
    def __init__(self, code: str):
        self.code = code


def read_json(path: Path):
    if not path.is_file():
        return None
    if path.stat().st_size > MAX_FILE_BYTES:
        raise AdapterError('store_too_large')
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise AdapterError('store_unreadable') from None


def safe_text(value, limit=200):
    if not isinstance(value, (str, int, float)):
        return None
    return str(value).replace('\x00', '')[:limit]


def safe_schedule(value):
    if isinstance(value, str):
        return safe_text(value, 250)
    if isinstance(value, dict):
        allowed = ('kind', 'expr', 'display', 'minutes', 'seconds', 'at', 'time', 'timezone')
        return {key: value[key] for key in allowed
                if key in value and isinstance(value[key], (str, int, float, bool))}
    return None


def schedule_matches(actual, requested):
    """Confirm a CLI edit using native schedule parsing, with an exact fallback."""
    compact = lambda value: ' '.join(str(value).strip().split())
    if isinstance(actual, str):
        return compact(actual) == compact(requested)
    if not isinstance(actual, dict):
        return False
    if any(compact(actual.get(key, '')) == compact(requested) for key in ('expr', 'display', 'at')):
        return True
    try:
        HERMES_TEMP.mkdir(parents=True, exist_ok=True)
        os.environ['HERMES_HOME'] = str(HERMES_HOME)
        os.environ['HERMES_BASE_HOME'] = str(HERMES_HOME)
        for key in ('TMPDIR', 'TMP', 'TEMP'):
            os.environ[key] = str(HERMES_TEMP)
        sys.path.insert(0, str(HERMES_HOME / 'hermes-agent'))
        with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            from cron.jobs import parse_schedule
            expected = safe_schedule(parse_schedule(requested))
        if not isinstance(expected, dict) or actual.get('kind') != expected.get('kind'):
            return False
        keys = tuple(key for key in expected if key != 'display')
        return bool(keys) and all(compact(actual.get(key, '')) == compact(expected[key]) for key in keys)
    except Exception:
        return False


def profile_homes():
    result = {'default': HERMES_HOME}
    root = HERMES_HOME / 'profiles'
    if root.is_dir():
        for path in sorted(root.iterdir(), key=lambda item: item.name)[:32]:
            if path.is_dir() and PROFILE_RE.fullmatch(path.name):
                result[path.name] = path
    return result


def hermes_rows():
    rows = []
    for profile, home in profile_homes().items():
        source = home / 'cron/jobs.json'
        data = read_json(source)
        if data is None:
            continue
        jobs = data.get('jobs', []) if isinstance(data, dict) else data
        if not isinstance(jobs, list) or len(jobs) > MAX_ROWS:
            raise AdapterError('invalid_hermes_store')
        for job in jobs:
            if not isinstance(job, dict) or not ID_RE.fullmatch(str(job.get('id', ''))):
                continue
            rows.append({
                'provider': 'hermes', 'id': profile + ':' + str(job['id']),
                'native_id': str(job['id']), 'profile': profile,
                'name': safe_text(job.get('name'), 160),
                'status': 'ACTIVE' if job.get('enabled') is True else 'PAUSED',
                'schedule': safe_schedule(job.get('schedule')),
                'next_run_at': safe_text(job.get('next_run_at'), 80),
                'last_run_at': safe_text(job.get('last_run_at'), 80),
                'last_status': safe_text(job.get('last_status'), 40),
                'source': str(source), 'editable': True,
                'write_mode': 'native_cli',
            })
    return rows


def codex_rows():
    rows = []
    if not CODEX_AUTOMATIONS.is_dir():
        return rows
    for path in sorted(CODEX_AUTOMATIONS.glob('*/automation.toml'))[:MAX_ROWS]:
        if path.stat().st_size > 128 * 1024:
            continue
        try:
            data = tomllib.loads(path.read_text(encoding='utf-8-sig'))
        except (OSError, UnicodeError, tomllib.TOMLDecodeError):
            continue
        rows.append({
            'provider': 'codex', 'id': safe_text(data.get('id', path.parent.name), 128),
            'name': safe_text(data.get('name'), 160),
            'status': safe_text(data.get('status'), 40),
            'kind': safe_text(data.get('kind'), 40),
            'schedule': safe_schedule(data.get('rrule')),
            'next_run_at': safe_text(data.get('next_run_at'), 80),
            'last_run_at': safe_text(data.get('last_run_at'), 80),
            'source': str(path), 'editable': True,
            'write_mode': 'native_app_request_queue',
        })
    return rows


def agent_rows():
    definitions = (
        ('hermes', 'Hermes', HERMES_HOME / 'memories/MEMORY.md'),
        ('dsh', 'DSH', Path('C:/Users/10939/.dsh/AGENTS.md')),
        ('claude', 'Claude', Path('C:/Users/10939/.claude/CLAUDE.md')),
    )
    rows = []
    for agent_id, name, entry in definitions:
        exists = entry.is_file()
        synced = False
        unified = False
        if exists and entry.stat().st_size <= 128 * 1024:
            text = entry.read_text(encoding='utf-8-sig')
            synced = REVISION in text
            unified = synced and 'E:/Hermes/shared/workbench/settings.json' in text
        rows.append({
            'id': agent_id, 'name': name, 'entry': str(entry),
            'status': 'synced' if synced else ('available' if exists else 'not_found'),
            'status_detail': '统一规则与参数入口已同步' if unified else
                             ('采集规则已同步' if synced else '尚未发现共享采集规则'),
            'shared_rule_revision': REVISION if synced else None,
        })
    return rows


def dsh_capability():
    # The official Web host has durable schedule APIs. The headless executor
    # does not mount that host service. Never invent an empty active catalog.
    module = Path('E:/Hermes/tools/dsh-0.2.1-rc.2/node_modules/@deepseek-ai/dsh-schedule/lib/index.js')
    candidates = (
        Path('C:/Users/10939/.dsh/storages/schedule.json'),
        Path('C:/Users/10939/.dsh/storages/schedule'),
        Path('E:/Hermes/tools/dsh-home/storages/schedule.json'),
        Path('E:/Hermes/tools/dsh-home/storages/schedule'),
    )
    return {
        'native_schedule_supported': module.is_file(),
        'status': 'host_connection_required' if module.is_file() else 'not_found',
        'read': False, 'write': False,
        'source': str(module),
        'catalog_api': 'ctx.remote.schedule.catalog()',
        'update_api': 'ctx.remote.schedule.update(request)',
        'delete_api': 'ctx.remote.schedule.delete({sessionId,id})',
        'found_default_stores': [str(path) for path in candidates if path.exists()],
        'detail': '官方 Web 宿主支持持久提醒；需连接正在运行的宿主取得权威清单。已查默认目录和 Hermes 无界面执行器目录。',
    }


def inventory():
    return {
        'ok': True, 'automations': hermes_rows() + codex_rows(),
        'agents': agent_rows(),
        'capabilities': {
            'hermes': {'read': True, 'write': ['pause', 'resume', 'update'],
                       'editable_fields': ['name', 'schedule'],
                       'transport': 'native_cli', 'timeout_seconds': 15,
                       'source': str(HERMES_CLI)},
            'codex': {'read': True, 'write': 'native_app_request_queue',
                      'required_native_tool': 'automation_update',
                      'direct_toml_write': False},
            'dsh': dsh_capability(),
            'claude': {'read': False, 'write': False, 'status': 'no_native_store_found',
                       'detail': '已查本机 .claude 默认目录，未发现 scheduled-tasks.json、scheduled-tasks 或 tasks 原生调度存储。'},
        },
        'shared_policy': str(POLICY), 'shared_settings': str(SETTINGS),
        'generated_at': datetime.now(timezone.utc).isoformat(),
    }


def change(request):
    if request.get('provider') != 'hermes':
        raise AdapterError('native_app_request_required' if request.get('provider') == 'codex' else 'provider_not_writable')
    action = request.get('action')
    if action not in ('pause', 'resume', 'update'):
        raise AdapterError('action_not_allowed')
    identity = request.get('id')
    if not isinstance(identity, str) or ':' not in identity:
        raise AdapterError('profile_qualified_id_required')
    profile, native_id = identity.split(':', 1)
    if profile not in profile_homes() or not ID_RE.fullmatch(native_id):
        raise AdapterError('invalid_job_identity')
    previous = next((row for row in hermes_rows() if row['id'] == identity), None)
    if previous is None:
        raise AdapterError('job_not_found')
    changes = request.get('changes', {})
    if not isinstance(changes, dict):
        raise AdapterError('invalid_changes')
    if action != 'update' and changes:
        raise AdapterError('unexpected_changes')
    if set(changes) - {'name', 'schedule'}:
        raise AdapterError('field_not_allowed')
    arguments = [] if profile == 'default' else ['--profile', profile]
    arguments += ['cron', 'edit' if action == 'update' else action, native_id]
    if action == 'update':
        if not changes:
            raise AdapterError('empty_changes')
        for key in ('name', 'schedule'):
            if key not in changes:
                continue
            value = changes[key]
            limit = 160 if key == 'name' else 250
            if not isinstance(value, str) or not value.strip() or len(value) > limit or any(ord(ch) < 32 for ch in value):
                raise AdapterError('invalid_' + key)
            arguments += ['--' + key, value.strip()]
    environment = dict(os.environ)
    HERMES_TEMP.mkdir(parents=True, exist_ok=True)
    environment.update({'HERMES_HOME': str(HERMES_HOME), 'HERMES_BASE_HOME': str(HERMES_HOME),
                        'PYTHONIOENCODING': 'utf-8', 'TMPDIR': str(HERMES_TEMP),
                        'TMP': str(HERMES_TEMP), 'TEMP': str(HERMES_TEMP)})
    try:
        process = subprocess.Popen([str(HERMES_CLI), *arguments], env=environment,
                                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL, shell=False,
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            # Terminate this short-lived CLI and its launcher children only.
            # Pause/resume/edit do not start the scheduler or run a job.
            try:
                subprocess.run(['taskkill.exe', '/PID', str(process.pid), '/T', '/F'],
                               stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL, timeout=2, shell=False,
                               creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            except (OSError, subprocess.TimeoutExpired):
                process.kill()
            return {'ok': False, 'error': 'native_cli_timeout', 'outcome': 'unknown',
                    'refresh_required': True, 'id': identity,
                    'message': '原生命令返回状态尚未确认，请刷新后查看。'}
    except OSError:
        raise AdapterError('native_cli_unavailable') from None
    if process.returncode != 0:
        return {'ok': False, 'error': 'native_cli_rejected', 'exit_code': process.returncode,
                'refresh_required': True, 'id': identity,
                'message': '原生命令未接受这项修改，请核对字段后重试。'}
    current = next((row for row in hermes_rows() if row['id'] == identity), None)
    confirmed = current is not None
    if confirmed and action == 'pause':
        confirmed = current['status'] == 'PAUSED'
    elif confirmed and action == 'resume':
        confirmed = current['status'] == 'ACTIVE'
    elif confirmed and action == 'update':
        if 'name' in changes:
            confirmed = current['name'] == changes['name'].strip()
        if confirmed and 'schedule' in changes:
            confirmed = schedule_matches(current['schedule'], changes['schedule'].strip())
    if not confirmed:
        return {'ok': False, 'error': 'native_state_unconfirmed', 'outcome': 'unknown',
                'refresh_required': True, 'id': identity,
                'message': '原生命令返回状态尚未确认，请刷新后查看。'}
    return {'ok': True, 'id': identity, 'action': action, 'outcome': 'confirmed',
            'verified': True, 'automation': current}


def main():
    try:
        raw = sys.stdin.buffer.read(16 * 1024 + 1)
        if len(raw) > 16 * 1024:
            raise AdapterError('request_too_large')
        request = json.loads(raw.decode('utf-8-sig'))
        if not isinstance(request, dict):
            raise AdapterError('invalid_request')
        operation = request.get('operation')
        if operation == 'inventory':
            response = inventory()
        elif operation == 'change':
            response = change(request)
        else:
            raise AdapterError('operation_not_allowed')
    except AdapterError as error:
        response = {'ok': False, 'error': error.code}
    except (UnicodeError, json.JSONDecodeError):
        response = {'ok': False, 'error': 'invalid_json'}
    except Exception:
        response = {'ok': False, 'error': 'adapter_failed'}
    sys.stdout.write(json.dumps(response, ensure_ascii=True, separators=(',', ':')))
    sys.stdout.flush()


if __name__ == '__main__':
    main()
