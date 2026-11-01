# Author: Codex, 2026-10-19
"""Checkpoint plugin integration; exact-board SQLite reads, no Hermes bootstrap.

The board remains the authority for workspace, run ownership and acceptance.
State is written locally plus a durable mirror outside ephemeral workspaces.
"""
from __future__ import annotations

from contextlib import closing
import hashlib
import json
import logging
import os
from pathlib import Path
import re
import sqlite3
import sys
from typing import Any, Callable

from .checkpoint import AUTHOR, CheckpointError, CheckpointStore, parse_contract

_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\Z")
_ACTIVE = frozenset({"running", "review"})
_log = logging.getLogger(__name__)

CHECKPOINT_TOOL_SCHEMA = {
    "name": "task_checkpoint",
    "description": "保存当前看板任务的阶段检查点，列出已经完成的步骤、产物路径与下一步。每完成一个阶段调用一次；中断后按保存的下一步继续。",
    "parameters": {
        "type": "object",
        "properties": {
            "task_id": {"type": "string", "description": "当前任务编号；通常省略，自动使用执行进程所属任务。"},
            "completed_steps": {"type": "array", "items": {"type": "string"}},
            "artifacts": {"type": "array", "items": {"type": "string"}},
            "next_step": {"type": "string"},
        },
        "required": ["next_step"],
        "additionalProperties": False,
    },
}


def _json(value: dict[str, Any]) -> str:
    return json.dumps({"author": AUTHOR, **value}, ensure_ascii=False)


def _rid(value: Any) -> str:
    return "" if value is None else str(value)


def _path(value: Any, label: str) -> Path:
    if not isinstance(value, (str, Path)) or not str(value).strip():
        raise CheckpointError(f"{label} is missing")
    raw = Path(value)
    if str(raw).startswith(("\\\\", "//")):
        raise CheckpointError(f"{label} must be a local path")
    if not raw.is_absolute():
        raise CheckpointError(f"{label} must be absolute")
    return raw.resolve()


class _MirrorStore(CheckpointStore):
    """Reuse the checkpoint's lock/atomic writer at its durable location."""

    def __init__(self, root: Path, board: str, task_id: str):
        super().__init__(root, task_id, board=board)
        self.directory = root / "task-channel" / "checkpoints" / board
        self.path = self.directory / f"{task_id}.json"

    def _prepare(self) -> None:
        parent = self.workspace
        for part in ("task-channel", "checkpoints", self.board):
            parent = parent / part
            resolved = parent.resolve()
            if self.workspace not in resolved.parents:
                raise CheckpointError("persistent checkpoint directory escapes Hermes root")
            parent.mkdir(exist_ok=True)
        super()._prepare()


class CheckpointRuntime:
    """Use from plugin hooks and the kanban_complete tool override.

    ``task_lookup(task_id, run_id=None)`` may be injected for isolated tests. It
    returns the task mapping plus ``run`` (requested/current attempt) and
    ``latest_run`` mappings. The default lookup reads one exact board DB only.
    Worker-facing mutation resolves ownership from dispatcher environment, or
    trusted handler kwargs ``kanban_task_id`` / ``kanban_run_id`` for adapters.
    """

    def __init__(self, root: Path, board: str, *, task_lookup: Callable[..., Any] | None = None):
        self.root = _path(root, "Hermes root")
        if not isinstance(board, str) or not _ID.fullmatch(board):
            raise CheckpointError("invalid board")
        self.board = board
        candidate = (self.root / "kanban.db" if board == "default" else
                     self.root / "kanban" / "boards" / board / "kanban.db")
        self.db_path = candidate.resolve()
        if self.root not in self.db_path.parents:
            raise CheckpointError("board DB escapes Hermes root")
        self.task_lookup = task_lookup or self._read_task

    def _read_task(self, task_id: str, run_id: str | None = None) -> dict[str, Any] | None:
        if not self.db_path.is_file():
            raise CheckpointError(f"board database is missing: {self.db_path}")
        with closing(sqlite3.connect(self.db_path.as_uri() + "?mode=ro", uri=True, timeout=1)) as conn:
            conn.row_factory = sqlite3.Row
            conn.execute("PRAGMA query_only=ON")
            row = conn.execute("SELECT * FROM tasks WHERE id = ?", (task_id,)).fetchone()
            if row is None:
                return None
            task = dict(row)
            requested = run_id if run_id is not None else task.get("current_run_id")
            run = (conn.execute("SELECT * FROM task_runs WHERE task_id = ? AND id = ?",
                                (task_id, requested)).fetchone() if requested is not None else None)
            latest = conn.execute("SELECT * FROM task_runs WHERE task_id = ? ORDER BY id DESC LIMIT 1",
                                  (task_id,)).fetchone()
            task.update(run=dict(run) if run else None, latest_run=dict(latest) if latest else None)
            return task

    def _lookup(self, task_id: str, run_id: str | None = None) -> dict[str, Any]:
        if not isinstance(task_id, str) or not _ID.fullmatch(task_id):
            raise CheckpointError("invalid task_id")
        task = self.task_lookup(task_id, run_id)
        if task is None:
            raise CheckpointError(f"task {task_id} was not found on board {self.board}")
        if not isinstance(task, dict) or task.get("id") != task_id:
            raise CheckpointError("task lookup returned a mismatched card")
        return task

    def _assert_current(self, task: dict[str, Any], run_id: str) -> None:
        current = _rid(task.get("current_run_id"))
        if current:
            if not run_id or run_id != current:
                raise CheckpointError("stale or missing run_id; the board has another current execution")
            run = task.get("run")
            if not isinstance(run, dict) or _rid(run.get("id")) != current or run.get("task_id") != task["id"]:
                raise CheckpointError("the current task run could not be verified")
            if run.get("ended_at") is not None:
                raise CheckpointError("the current task run has already ended")
            if task.get("status") not in _ACTIVE:
                raise CheckpointError("the current card is not in an active execution state")
        elif run_id:
            raise CheckpointError("this task no longer has an active execution")

    def _store(self, task: dict[str, Any], *, require_workspace: bool = True) -> tuple[CheckpointStore, dict[str, Any]]:
        workspace = _path(task.get("workspace_path"), "task workspace")
        if require_workspace and not workspace.is_dir():
            raise CheckpointError("task workspace is missing; restore it before continuing")
        contract = parse_contract(task.get("body") or "") or {}
        raw_roots = contract.get("allowed_roots", [])
        if not isinstance(raw_roots, list) or len(raw_roots) > 16:
            raise CheckpointError("contract allowed_roots must be a list with at most 16 paths")
        roots = []
        for value in raw_roots:
            if not isinstance(value, str) or not value.strip():
                raise CheckpointError("contract allowed_roots contains an invalid path")
            raw = Path(value)
            approved = _path(raw if raw.is_absolute() else workspace / raw, "approved output root")
            if approved == Path(approved.anchor):
                raise CheckpointError("an entire filesystem drive cannot be an approved output root")
            roots.append(approved)
        return CheckpointStore(workspace, task["id"], board=self.board, allowed_roots=roots), contract

    @staticmethod
    def _input_versions(task: dict[str, Any], contract: dict[str, Any]) -> dict[str, str]:
        card_input = json.dumps({"title": task.get("title") or "", "body": task.get("body") or ""},
                                ensure_ascii=False, sort_keys=True).encode("utf-8")
        versions = {"card_spec_sha256": hashlib.sha256(card_input).hexdigest()}
        for name, value in contract.get("input_versions", {}).items():
            versions[f"input:{name}"] = value
        return versions

    def _mirror(self, task_id: str) -> _MirrorStore:
        return _MirrorStore(self.root, self.board, task_id)

    def _save_mirror(self, task: dict[str, Any], state: dict[str, Any], store: CheckpointStore) -> dict[str, Any]:
        snapshot = json.loads(json.dumps(state))
        snapshot["source_workspace"] = str(store.workspace)
        presence = []
        for raw in snapshot["artifacts"]:
            try:
                artifact = store._allowed_path(raw)
                presence.append({"path": str(artifact), "exists": artifact.is_file()})
            except (CheckpointError, OSError):
                presence.append({"path": raw, "exists": False, "status": "outside_current_scope"})
        snapshot["artifact_presence"] = presence
        mirror = self._mirror(task["id"])
        with mirror._locked():
            # A delayed observer from a prior run must not overwrite the mirror
            # after the dispatcher has advanced to a newer run/workspace.
            owner = self._lookup(task["id"], _rid(state.get("run_id")) or None)
            current, run = _rid(owner.get("current_run_id")), _rid(state.get("run_id"))
            latest = owner.get("latest_run")
            if current and current != run:
                raise CheckpointError("newer execution owns the persistent checkpoint")
            if run and isinstance(latest, dict) and _rid(latest.get("id")) != run:
                raise CheckpointError("a newer run exists; persistent checkpoint write refused")
            return mirror._write(snapshot)

    @staticmethod
    def _restore_state(store: CheckpointStore, state: dict[str, Any], run_id: str) -> dict[str, Any]:
        restored = json.loads(json.dumps(state))
        previous = _path(restored.get("source_workspace", str(store.workspace)), "previous task workspace")
        usable, pending = [], []
        recorded = [{"source_path": raw, "expected_path": raw} for raw in restored["artifacts"]]
        recorded += list(restored.get("pending_artifacts", []))
        for item in recorded:
            old = Path(item["expected_path"])
            old = old if old.is_absolute() else previous / old
            candidate = store.workspace / old.relative_to(previous) if old.is_relative_to(previous) else old
            try:
                candidate = store._allowed_path(str(candidate))
                if candidate.is_file() or candidate.is_dir():
                    usable.append(str(candidate))
                    continue
                status = "missing"
            except (CheckpointError, OSError):
                status = "outside_current_scope"
            pending.append({"source_path": item.get("source_path", str(old)),
                            "expected_path": str(candidate), "status": status})
        # Missing output invalidates the completion claim, while keeping its
        # history and the exact next step available for targeted recovery.
        if pending:
            restored["pending_recheck_steps"] = list(dict.fromkeys(
                restored.get("pending_recheck_steps", []) + restored["completed_steps"]))
            restored["completed_steps"] = []
        restored.update(artifacts=list(dict.fromkeys(usable)), pending_artifacts=pending,
                        restored_from_workspace=str(previous), source_workspace=str(store.workspace),
                        run_id=run_id, status="running")
        return restored

    @staticmethod
    def _resolve_recheck(store: CheckpointStore, state: dict[str, Any]) -> dict[str, Any]:
        pending = []
        for item in state.get("pending_artifacts", []):
            try:
                candidate = store._allowed_path(item["expected_path"])
                if candidate.is_file() or candidate.is_dir():
                    state["artifacts"] = list(dict.fromkeys(state["artifacts"] + [str(candidate)]))
                    continue
            except (CheckpointError, OSError, KeyError):
                _log.debug("restored artifact still needs scoped verification: %s", item)
            pending.append(item)
        state["pending_artifacts"] = pending
        state["pending_recheck_steps"] = [step for step in state.get("pending_recheck_steps", [])
                                           if step not in state["completed_steps"]]
        return state

    def _prepare_current(self, task: dict[str, Any], run_id: str) -> CheckpointStore:
        self._assert_current(task, run_id)
        store, contract = self._store(task)
        versions = self._input_versions(task, contract)
        existing = store.load()
        if existing is None:
            existing = self._mirror(task["id"]).load()
        if existing is not None:
            if existing["input_versions"] != versions:
                raise CheckpointError("input versions changed; review completed steps before updating the checkpoint")
            if (not store.path.exists() or existing.get("run_id") != run_id
                    or existing.get("status") == "interrupted"):
                restored = self._restore_state(store, existing, run_id)
                with store._locked():
                    store._write(restored)
        # Acceptance is always reloaded from the card, so editing checkpoint JSON
        # cannot remove the card's delivery requirements.
        state = store.initialize(input_versions=versions, acceptance=contract.get("acceptance", []), run_id=run_id)
        self._save_mirror(task, state, store)
        return store

    def on_spawn(self, task_id: str, workspace_path: str | Path | None = None,
                 run_id: Any = None, **kw: Any) -> dict[str, Any]:
        try:
            run = _rid(run_id)
            task = self._lookup(task_id, run)
            if workspace_path is not None and _path(workspace_path, "spawn workspace") != _path(task.get("workspace_path"), "task workspace"):
                raise CheckpointError("spawn workspace does not match the board card")
            if not run:
                raise CheckpointError("spawn event is missing its run_id")
            store = self._prepare_current(task, run)
            return {"ok": True, "task_id": task_id, "run_id": run, "checkpoint": str(store.path),
                    "persistent_checkpoint": str(self._mirror(task_id).path)}
        except (CheckpointError, OSError, sqlite3.Error) as exc:
            return {"ok": False, "task_id": task_id, "error": str(exc)}

    def worker_context(self, task_id: str, run_id: Any) -> str:
        try:
            run = _rid(run_id)
            task = self._lookup(task_id, run or None)
            store = self._prepare_current(task, run)
            state = store.load()
            pending = state.get("pending_artifacts", [])
            note = ""
            if pending or state.get("pending_recheck_steps"):
                note = "\n需恢复或重新核对的产物：" + "；".join(x["expected_path"] for x in pending)
                note += "\n待重新核对的历史步骤：" + "；".join(state.get("pending_recheck_steps", []))
            return store.resume_context() + note + "\n使用 task_checkpoint 保存阶段进度；交付前按卡内验收条件核对产物。"
        except (CheckpointError, OSError, sqlite3.Error) as exc:
            return f"任务 {task_id} 检查点待处理：{exc}。先修复检查点问题，再继续执行。"

    def _owned_target(self, args: dict[str, Any], kw: dict[str, Any], *, worker_only: bool) -> tuple[str, str]:
        delegation = sys.modules.get("agent.delegation_context")
        is_delegated = getattr(delegation, "is_delegated_child_process_context", None)
        owns_context = getattr(delegation, "is_dispatcher_owned_worker_context", None)
        if os.environ.get("HERMES_DELEGATED_CHILD_CONTEXT") or (callable(is_delegated) and is_delegated()):
            raise CheckpointError("delegated children return their progress to the task's owning worker")
        if worker_only and callable(owns_context) and not owns_context():
            raise CheckpointError("task_checkpoint requires a dispatcher-owned execution context")
        owned_task = str(kw.get("kanban_task_id") or os.environ.get("HERMES_KANBAN_TASK") or "")
        run_id = _rid(kw.get("kanban_run_id") or os.environ.get("HERMES_KANBAN_RUN_ID"))
        task_id = str(args.get("task_id") or owned_task or "")
        if owned_task and task_id != owned_task:
            raise CheckpointError("a worker can update only its own dispatcher-assigned task")
        if worker_only and (not owned_task or not run_id):
            raise CheckpointError("task_checkpoint requires a dispatcher-owned task and current run")
        explicit_board = args.get("board")
        if explicit_board and explicit_board != self.board:
            raise CheckpointError("task board does not match this task-channel runtime")
        if args.get("allowed_roots") is not None:
            raise CheckpointError("approved output roots come from the card contract")
        return task_id, run_id

    def worker_checkpoint(self, args: dict[str, Any], **kw: Any) -> str:
        try:
            if not isinstance(args, dict):
                raise CheckpointError("checkpoint arguments must be an object")
            task_id, run_id = self._owned_target(args, kw, worker_only=True)
            task = self._lookup(task_id, run_id)
            store = self._prepare_current(task, run_id)
            supplied_artifacts = args.get("artifacts")
            if supplied_artifacts is not None:
                if not isinstance(supplied_artifacts, list) or any(not isinstance(p, str) for p in supplied_artifacts):
                    raise CheckpointError("artifacts must be a list of paths")
                for artifact in supplied_artifacts:
                    store._allowed_path(artifact)
            state = store.update(completed_steps=args.get("completed_steps"),
                                 artifacts=supplied_artifacts, next_step=args.get("next_step"), run_id=run_id)
            with store._locked():
                state = self._resolve_recheck(store, store.load())
                store._write(state)
            self._save_mirror(task, state, store)
            return _json({"ok": True, "task_id": task_id, "run_id": run_id,
                          "checkpoint": str(store.path), "completed_steps": state["completed_steps"],
                          "artifacts": state["artifacts"], "next_step": state["next_step"],
                          "pending_artifacts": state.get("pending_artifacts", []),
                          "persistent_checkpoint": str(self._mirror(task_id).path)})
        except (CheckpointError, OSError, sqlite3.Error) as exc:
            return _json({"ok": False, "error": str(exc)})

    def wrap_complete(self, original_handler: Callable[..., Any], args: dict[str, Any], **kw: Any) -> Any:
        store = None
        try:
            if not isinstance(args, dict):
                raise CheckpointError("completion arguments must be an object")
            task_id, run_id = self._owned_target(args, kw, worker_only=False)
            task = self._lookup(task_id, run_id or None)
            artifacts = args.get("artifacts") or []
            if not isinstance(artifacts, list):
                raise CheckpointError("completion artifacts must be a list of paths")
            metadata = args.get("metadata")
            if isinstance(metadata, dict) and metadata.get("artifacts"):
                if not isinstance(artifacts, list) or not isinstance(metadata["artifacts"], list):
                    raise CheckpointError("completion artifacts must be a list of paths")
                artifacts = artifacts + metadata["artifacts"]
            self._assert_current(task, run_id)
            contract = parse_contract(task.get("body") or "")
            legacy_coordination = not task.get("workspace_path") and contract is None and not artifacts and not run_id
            if legacy_coordination:
                return original_handler(args, **kw)
            store = self._prepare_current(task, run_id)
            with store._locked():
                snapshot = self._resolve_recheck(store, store.load())
                store._write(snapshot)
            if snapshot.get("pending_artifacts") or snapshot.get("pending_recheck_steps"):
                self._save_mirror(task, snapshot, store)
                missing = [item["expected_path"] for item in snapshot.get("pending_artifacts", [])]
                return _json({"ok": False, "task_id": task_id,
                              "error": "恢复检查点后仍需核对历史步骤或补回产物；核对后使用 task_checkpoint 保存进度。",
                              "pending_artifacts": missing,
                              "pending_recheck_steps": snapshot.get("pending_recheck_steps", [])})
            validation = store.validate_completion(artifacts)
            if not validation["ok"]:
                return _json({"ok": False, "task_id": task_id, "error": "交付检查待补齐：" + "；".join(validation["errors"]),
                              "validation": validation})
            # Recheck ownership immediately before the native handler. Its own
            # expected_run_id CAS remains the transaction-level final fence.
            self._assert_current(self._lookup(task_id, run_id or None), run_id)
            snapshot.update(status="ready_for_review", completion_validation=validation,
                            artifacts=list(dict.fromkeys(snapshot["artifacts"] + artifacts)))
            self._save_mirror(task, snapshot, store)
        except (CheckpointError, OSError, sqlite3.Error) as exc:
            return _json({"ok": False, "error": str(exc)})
        result = original_handler(args, **kw)
        try:
            parsed = json.loads(result) if isinstance(result, str) else result
            if isinstance(parsed, dict) and parsed.get("ok") is True and store is not None:
                # Native complete_task may already have removed scratch, so
                # persist the verdict without recreating that workspace.
                snapshot.update(status="completed", next_step="",
                                native_attachments=parsed.get("attachments", []),
                                artifact_preservation="native_kanban_attachments",
                                source_workspace_removed=not store.workspace.is_dir())
                if not run_id and parsed.get("run_id") is not None:
                    snapshot["run_id"] = _rid(parsed["run_id"])
                self._save_mirror(task, snapshot, store)
                if store.workspace.is_dir():
                    store.update(status="completed", next_step="", run_id=run_id)
        except (ValueError, OSError, sqlite3.Error) as exc:
            # Native completion succeeded; the observer can reconcile its
            # checkpoint on exit without turning a success into a duplicate try.
            _log.warning("task %s completed; checkpoint reconciliation needed: %s", task_id, exc)
        return result

    def on_exit(self, task_id: str, run_id: Any = None, **kw: Any) -> dict[str, Any]:
        try:
            run = _rid(run_id)
            if not run:
                raise CheckpointError("exit event is missing its run_id")
            task = self._lookup(task_id, run)
            current = _rid(task.get("current_run_id"))
            if current and current != run:
                return {"ok": False, "task_id": task_id, "ignored": "newer_run_owns_task"}
            latest = task.get("latest_run")
            if isinstance(latest, dict) and _rid(latest.get("id")) != run:
                return {"ok": False, "task_id": task_id, "ignored": "newer_run_exists"}
            store, _ = self._store(task, require_workspace=False)
            state = store.load() if store.workspace.is_dir() else None
            if state is None:
                state = self._mirror(task_id).load()
            if state is None or state.get("run_id") != run:
                return {"ok": False, "task_id": task_id, "ignored": "checkpoint_owned_by_other_run"}
            attempt = task.get("run")
            if not isinstance(attempt, dict) or _rid(attempt.get("id")) != run or attempt.get("task_id") != task_id:
                raise CheckpointError("exit event's task run could not be verified")
            if attempt.get("ended_at") is None:
                raise CheckpointError("exit event's task run is still active in the board")
            completed = task.get("status") in {"done", "archived"} and attempt.get("outcome") == "completed"
            status = "completed" if completed else "interrupted"
            state["status"] = status
            if completed:
                state.update(next_step="", artifact_preservation="native_kanban_attachments",
                             source_workspace_removed=not store.workspace.is_dir())
            self._save_mirror(task, state, store)
            if store.workspace.is_dir():
                store.update(status=status, run_id=run)
            return {"ok": True, "task_id": task_id, "run_id": run, "status": status,
                    "checkpoint": str(store.path), "persistent_checkpoint": str(self._mirror(task_id).path)}
        except (CheckpointError, OSError, sqlite3.Error) as exc:
            return {"ok": False, "task_id": task_id, "error": str(exc)}
