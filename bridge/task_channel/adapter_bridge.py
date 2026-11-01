# Author: Codex, 2026-10-19.
"""Bind the existing Doloris adapter to the one configured physical QQ route."""
import json
from pathlib import Path

from .outbox import TaskOutbox
from .wake_resolver import TaskWakeResolver


def configure_adapter(adapter, root):
    root = Path(root)
    settings = json.loads((root / 'task-channel' / 'runtime.json').read_text(encoding='utf-8'))
    destination = str(settings['delivery_group_id'])
    if str(adapter._coordinator_group) != destination:
        return
    outbox = TaskOutbox(root / 'task-channel' / 'outbox.sqlite3')
    resolver = TaskWakeResolver(root, settings['board'], destination, 'doloris', outbox)
    adapter.configure_task_channel(outbox=outbox, group_id=destination, profile='doloris', resolve_event=resolver)

