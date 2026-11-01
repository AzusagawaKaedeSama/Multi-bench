# Author: Codex, 2026-10-19.
"""Persist verified native wakes before the LLM, using the supported gateway hook."""
import asyncio
import hashlib
import importlib.util
import logging
from pathlib import Path
import sys

from .outbox import TaskOutbox

logger=logging.getLogger(__name__)

def _resolver_class():
    # An already connected adapter may own an older resolver class. Load the
    # new source in its own namespace, without changing that object's globals.
    path=Path(__file__).with_name('wake_resolver.py')
    signature=hashlib.sha256(path.read_bytes()).hexdigest()[:16]
    name='task_channel._notice_resolver_'+signature
    module=sys.modules.get(name)
    if module is None:
        spec=importlib.util.spec_from_file_location(name,path)
        module=importlib.util.module_from_spec(spec)
        sys.modules[name]=module
        try:spec.loader.exec_module(module)
        except BaseException:
            sys.modules.pop(name,None)
            raise
    return module.TaskWakeResolver

def register(ctx):
    settings=ctx.get_config('channel',{})
    profile=settings.get('profile')
    if not settings.get('enabled',True) or profile not in {'default','doloris'}:
        return
    root=Path(settings['root'])
    outbox=TaskOutbox(root/'task-channel'/'outbox.sqlite3')
    resolver_type=_resolver_class()
    # Connected adapters hold old resolver instances. Upgrade only their pure formatter.
    for name,module in tuple(sys.modules.items()):
        if name == 'task_channel.wake_resolver' or name.startswith('task_channel._notice_resolver_'):
            old_type=getattr(module,'TaskWakeResolver',None)
            if isinstance(old_type,type):old_type.notice_payload=resolver_type.notice_payload
    resolver=resolver_type(root,settings['board'],settings['delivery_group_id'],'doloris',outbox)

    if profile=='default':
        from .event_collector import EventCollector
        collector=EventCollector(root,settings['board'],settings['delivery_group_id'],'doloris',outbox,resolver)
        def collect(*,board=None,**kwargs):
            if board!=settings['board']:return None
            count=collector.collect(limit=100)
            if count:
                logger.info('task-channel events collected notices=%s cursor=%s',count,collector.last_result.get('cursor'))
            return None
        ctx.register_hook('on_kanban_dispatch_tick',collect)
        return

    async def consume(*,event,**kwargs):
        if not getattr(event,'internal',False):return None
        row=await asyncio.to_thread(resolver,event)
        if row is None:return None
        logger.info('task-channel native notice task=%s notice=%s kind=%s',row['task_id'],row['id'],row['kind'])
        return {'action':'skip','reason':'task-channel-native-notice'}

    ctx.register_hook('pre_gateway_dispatch',consume)
