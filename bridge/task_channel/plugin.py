# Author: Codex, 2026-10-19.
"""Supported tool overrides and lifecycle consumers for the shared task channel."""
from __future__ import annotations

from copy import deepcopy
from functools import wraps
import json
import logging
import os
from pathlib import Path

from .checkpoint_runtime import CHECKPOINT_TOOL_SCHEMA, CheckpointRuntime
from .dispatch import AdmissionError, dispatch_key
from .service import TaskChannel
from . import native

logger = logging.getLogger(__name__)


def _json(value):
    return json.dumps(value, ensure_ascii=False)


def _source():
    from gateway.session_context import get_session_env
    names = {'profile': 'PROFILE', 'platform': 'PLATFORM', 'chat_id': 'CHAT_ID',
             'chat_type': 'CHAT_TYPE', 'thread_id': 'THREAD_ID', 'message_id': 'MESSAGE_ID',
             'session_id': 'ID'}
    return {key: get_session_env('HERMES_SESSION_' + suffix, '') or '' for key, suffix in names.items()}


def _worker():
    from agent.delegation_context import is_dispatcher_owned_worker_context
    if not is_dispatcher_owned_worker_context():
        return None
    task, run = os.environ.get('HERMES_KANBAN_TASK'), os.environ.get('HERMES_KANBAN_RUN_ID')
    return (task, run) if task and run else None


def _prepared_args(args):
    args = dict(args)
    contract = args.pop('task_contract', None)
    if contract is not None:
        if not isinstance(contract, dict):
            raise ValueError('task_contract must be an object')
        from .checkpoint import parse_contract
        block = json.dumps({'author': 'Codex, 2026-10-19', 'version': 1, **contract}, ensure_ascii=False)
        body = str(args.get('body') or '') + '\n\n```task-channel\n' + block + '\n```'
        parse_contract(body)
        args['body'] = body
    return args


class ChannelPlugin:
    def __init__(self, ctx, settings):
        self.ctx, self.settings = ctx, settings
        self.channel = TaskChannel(settings)
        self.runtime = CheckpointRuntime(Path(settings['root']), settings['board'])
        self.originals = {}

    def manages(self, args):
        board = args.get('board')
        if board:
            return board == self.settings['board']
        source = _source()
        if source['platform'] == 'qqbot' and source['chat_type'] == 'group':
            return source['chat_id'] in self.settings.get('group_chat_ids', {}).values()
        return bool(_worker() and os.environ.get('HERMES_KANBAN_BOARD') == self.settings['board'])

    def create(self, args, **kw):
        original = self.originals['kanban_create']
        if not self.manages(args):
            return original(args, **kw)
        try:
            args, source = _prepared_args(args), _source()
            def create_native(data):
                return json.loads(original(data, **kw))
            return _json(self.channel.create(args, source, create_native))
        except (AdmissionError, ValueError) as exc:
            return _json({'ok': False, 'error': str(exc)})

    def comment(self, args, **kw):
        original = self.originals['kanban_comment']
        # Preserve native authorization, persisted author, and secret redaction first.
        result = original(args, **kw)
        if not self.manages(args):
            return result
        parsed = json.loads(result)
        if not parsed.get('ok') or args.get('comment_kind') == 'note':
            return result
        # A worker can report to its owner but cannot recursively create new work.
        if _worker():
            return result
        source = _source()
        followup_key = None
        if source.get('message_id') or args.get('idempotency_key'):
            identity = {'title': parsed['task_id'], 'body': args.get('body'),
                        'idempotency_key': args.get('idempotency_key')}
            followup_key = 'followup:' + dispatch_key(identity, source)
        followup = self.channel.dispatch.followup(parsed['task_id'], parsed['comment_id'],
            lambda data, original: native.create_card(self.settings['root'], self.settings['board'],
                                                     data, original=original, settings=self.settings),
            source=source, dispatch_id=followup_key)
        if followup is None:
            return result
        if followup.get('ok'):
            tid = followup['task_id']
            native.add_followup_link(self.settings['root'], self.settings['board'], parsed['task_id'], tid)
            notice = self.channel.receipt_notice(followup, _source())
            return _json({**parsed, 'followup_task_id': tid, 'receipt': followup['receipt'],
                          'task_notice_id': notice['id'] if notice else None})
        return _json({**parsed, 'followup_error': followup.get('error'), 'receipt': followup.get('receipt')})

    def complete(self, args, **kw):
        original = self.originals['kanban_complete']
        return self.runtime.wrap_complete(original, args, **kw) if self.manages(args) else original(args, **kw)

    def before_llm(self, **kw):
        worker = _worker()
        if worker and os.environ.get('HERMES_KANBAN_BOARD') == self.settings['board']:
            return {'context': self.runtime.worker_context(*worker)}
        if self.manages({}):
            return {'context': (
                '任务通道：新建需要交付文件、图片或压缩包的卡时，填写 task_contract，'
                'delivery="files"，acceptance 写明路径或文件名模式、格式 kind 和数量 count/min_count；'
                '外部输出目录写入 allowed_roots，已知输入版本写入 input_versions。'
                '只需在群里交付文字结论时填写 delivery="text"、acceptance=[]。'
                '验收要求来自用户本次任务；交付前核对任务卡、检查点和实际产物。'
                '同一派单重试保留 idempotency_key；追加已完成卡用 kanban_comment，'
                '纯记录评论填写 comment_kind="note"。')}
        return None

    def spawn(self, **kw):
        if kw.get('board') != self.settings['board']:
            return None
        outcome = self.runtime.on_spawn(**kw)
        logger.info('task-channel checkpoint spawn task=%s ok=%s', kw.get('task_id'), outcome.get('ok'))
        return None

    def exited(self, **kw):
        if kw.get('board') == self.settings['board']:
            result = self.runtime.on_exit(**kw)
            logger.info('task-channel checkpoint exit task=%s ok=%s', kw.get('task_id'), result.get('ok'))
        return None

    def tick(self, **kw):
        if kw.get('board') != self.settings['board']:
            return None
        repaired = native.repair_active_group_subscriptions(self.settings['root'], self.settings['board'], self.settings)
        if repaired:
            logger.info('task-channel active routes repaired tasks=%s', ','.join(repaired))
        activated = self.channel.dispatch.activate_waiting(
            lambda task_id, assignee=None: native.reactivate_card(self.settings['root'], self.settings['board'],
                                                                 task_id, assignee=assignee))
        if activated:
            logger.info('task-channel capacity admitted tasks=%s', ','.join(x['task_id'] for x in activated))
        self.channel.progress()
        return None

    def register_tools(self):
        # Discovery also runs during CLI/gateway cold start, before model_tools
        # materializes optional built-ins. Import their native registration once.
        import importlib
        importlib.import_module('tools.kanban_tools')
        from tools.registry import registry
        handlers = {'kanban_create': self.create, 'kanban_comment': self.comment, 'kanban_complete': self.complete}
        for name, handler in handlers.items():
            entry = registry.get_entry(name, scope=self.ctx._manager.scope_key)
            if entry is None:
                raise RuntimeError(f'Native {name} must be registered before task-channel')
            self.originals[name] = entry.handler
            schema = deepcopy(entry.schema)
            properties = schema.get('parameters', {}).get('properties', {})
            if name == 'kanban_create':
                properties['task_contract'] = {'type': 'object', 'description':
                    '新文件交付卡须声明 delivery=files 和非空acceptance；纯文字声明delivery=text。'
                    'version=1、input_versions、allowed_roots、acceptance；文件验收支持path/glob、kind、count/min_count/max_count。'}
            elif name == 'kanban_comment':
                properties['comment_kind'] = {'type': 'string', 'enum': ['change_request', 'note'],
                    'description': '默认作为追加要求；已完成卡自动建立跟进任务。纯存档评论显式填note。'}
                properties['idempotency_key'] = {'type': 'string', 'description':
                    '可选稳定追加要求编号；同一编号重试时复用原跟进卡。群消息默认自动生成。'}
            self.ctx.register_tool(name=name, toolset=entry.toolset, schema=schema, handler=handler,
                                   check_fn=entry.check_fn, requires_env=entry.requires_env,
                                   is_async=entry.is_async, description=entry.description,
                                   emoji=entry.emoji, override=True)
        self.ctx.register_tool(name='task_checkpoint', toolset='kanban', schema=CHECKPOINT_TOOL_SCHEMA,
                               handler=self.runtime.worker_checkpoint,
                               description=CHECKPOINT_TOOL_SCHEMA['description'], emoji='📍')


def register(ctx):
    settings = ctx.get_config('channel', {})
    if not settings.get('enabled', True):
        return
    plugin = ChannelPlugin(ctx, settings)
    plugin.register_tools()
    ctx.register_hook('pre_llm_call', plugin.before_llm)
    ctx.register_hook('on_kanban_worker_spawned', plugin.spawn)
    ctx.register_hook('on_kanban_worker_exited', plugin.exited)
    ctx.register_hook('on_kanban_worker_stale_claim', plugin.exited)
    ctx.register_hook('on_kanban_dispatch_tick', plugin.tick)

