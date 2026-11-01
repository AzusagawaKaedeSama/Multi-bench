"""Load the operator's privacy helper by source hash, including after plugin reload."""
import hashlib
import importlib.util
from pathlib import Path
import sys
import threading

_IMPORT_LOCK = threading.RLock()

def decorate_task(text, *, root, board, task_id, run_id):
    root = Path(root).resolve(strict=True)
    source = root / 'plugins' / 'budget-routing' / 'task_privacy.py'
    if not source.is_file():
        return text
    signature = hashlib.sha256(source.read_bytes()).hexdigest()[:16]
    name = '_hermes_task_privacy_' + signature
    with _IMPORT_LOCK:
        module = sys.modules.get(name)
        if module is None:
            spec = importlib.util.spec_from_file_location(name, source)
            module = importlib.util.module_from_spec(spec)
            sys.modules[name] = module
            try:
                spec.loader.exec_module(module)
            except BaseException:
                sys.modules.pop(name, None)
                raise
    board_db = root / 'kanban.db' if board == 'default' else root / 'kanban' / 'boards' / board / 'kanban.db'
    return module.decorate_task(text, board_db=board_db, task_id=task_id, run_id=run_id)
