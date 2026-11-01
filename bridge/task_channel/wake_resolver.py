# Author: Codex, 2026-10-19
"""Validate native Kanban wakes against one read-only board before queueing QQ text.

This module never bootstraps Hermes or sends messages. ``internal`` is a trusted
gateway property, never a field which an ingress adapter may accept from users.
The remaining checks bind that authority to the configured board, task and route.
"""
from __future__ import annotations

from collections.abc import Callable, Mapping
import json
import logging
from pathlib import Path
import re
import sqlite3


logger = logging.getLogger(__name__)
_BOARD = re.compile(r"[a-z0-9][a-z0-9_-]{0,63}\Z")
_TASK = r"t_[0-9a-f]{8}"
_WAKE_KINDS = ("completed", "gave_up", "crashed", "timed_out", "blocked",
               "review_requested", "changes_requested", "block_loop_detected")
_HEADERS = (
    ("zh", re.compile(rf"\[kanban\] 任务 (?P<task_id>{_TASK}) (?P<status>.+)。\Z")),
    ("en", re.compile(rf"\[kanban\] Task (?P<task_id>{_TASK}) (?P<status>.+)\.\Z")),
)
_CATALOG = {
    "en": {
        "statuses": ("completed", "gave up (retries exhausted)",
                     "crashed (worker exited); dispatcher will retry", "timed out; dispatcher will retry",
                     "blocked; needs attention", "handed off for review; the implementation is done",
                     "review requested changes (BLOCK); implementation is not approved",
                     "routed to triage after repeated blocks; orchestration attention needed"),
        "joiner": ", ", "review_default": "reviewer feedback requires changes",
        "review_detail": "Review feedback: {reason}\nInspect the existing card and its current review run; return work to the same implementation task. Do not treat this BLOCK as approval and do not create a duplicate task.",
    },
    "zh": {
        "statuses": ("已完成", "已放弃（重试次数耗尽）", "崩溃（worker 异常退出），dispatcher 将重试",
                     "超时，dispatcher 将重试", "被阻塞，需要处理", "已交付评审；实现已完成",
                     "评审要求修改（BLOCK)，实现未获批准", "反复阻塞后被转入待分诊，需要编排关注"),
        "joiner": "，", "review_default": "审核反馈要求修改",
        "review_detail": "评审反馈: {reason}\n请检查现有卡片及其当前评审运行；将工作返回同一实现任务。不要把此 BLOCK 视为批准，也不要创建重复任务。",
    },
}
# Validate whole native status strings, including batches and catalog joiners.
# A phrase containing a comma is never split into an invented event kind.
_STATUS_KINDS = {
    language: {catalog["joiner"].join(catalog["statuses"][index] for index in range(len(_WAKE_KINDS))
                                       if mask & (1 << index)):
               tuple(kind for index, kind in enumerate(_WAKE_KINDS) if mask & (1 << index))
               for mask in range(1, 1 << len(_WAKE_KINDS))}
    for language, catalog in _CATALOG.items()
}
# Keep the old class's global lookup valid while a supported plugin hot reload
# installs the new gateway hook. Existing adapter instances retain old methods;
# they can decline new kinds without raising after the module dictionary reloads.
_STATUSES = {status: kinds[-1] for mapping in _STATUS_KINDS.values() for status, kinds in mapping.items()}
_GUIDANCE = {
    "zh": "这是自动任务状态通知，不是再次分解任务的请求。创建后续任务前请先检查当前看板；不要重复创建已存在的任务或任务图。",
    "en": "This is an automatic task-status notification, not a request to decompose the task again. Inspect the current board before creating follow-up tasks; do not recreate tasks or graphs that already exist.",
}


def _value(obj, name, default=None):
    return obj.get(name, default) if isinstance(obj, Mapping) else getattr(obj, name, default)


def _string(value) -> str:
    return str(value or "")


def _first_line(value, limit: int) -> str:
    lines = _string(value).strip().splitlines()
    return lines[0][:limit] if lines else ""


def _normalize_native_text(value: str) -> str:
    # Bundled catalog values currently contain literal \\n while handoff separators
    # are real newlines; accept the same template after either rendering convention.
    return value.replace("\r\n", "\n").replace("\\n", "\n")


def _safe_review_reason(value) -> str:
    # Match native _safe_review_reason exactly, including its credential
    # redactor; this lazy import never imports hermes_bootstrap or runs a model.
    from agent.redact import redact_sensitive_text
    reason = redact_sensitive_text("" if value is None else str(value), force=True, redact_url_credentials=True)
    paths = re.compile(r"(?<![\w:/])(?:/(?:Users|home|private|tmp|var|etc|workspace)/[^\s,;]+|" r"[A-Za-z]:\\[^\s,;]+)")
    reason = " ".join(paths.sub("[local path]", reason).split())
    return reason[:159].rstrip() + "…" if len(reason) > 160 else reason


def _diagnostic(row: Mapping) -> bool:
    kind, payload = row.get("kind"), row.get("payload") or {}
    return kind in {"crashed", "timed_out", "gave_up"} or (
        kind in {"blocked", "block_loop_detected"} and payload.get("kind") != "needs_input")


def _notification_category(event) -> str | None:
    metadata = _value(event, "metadata")
    if not isinstance(metadata, Mapping):
        return None
    if "notification_category" in metadata:
        category = metadata["notification_category"]
        return category if isinstance(category, str) and category in {"result", "diagnostic"} else None
    # The native session FIFO intentionally omits the default result category.
    # Only its producer identity and admitted session anchors authorize that
    # omission; ordinary internal text and user-supplied metadata do not.
    key, session_id = metadata.get("gateway_session_key"), metadata.get("gateway_session_id")
    if not isinstance(key, str) or not key or not isinstance(session_id, str) or not session_id:
        return None
    try:
        identity = json.loads(_value(event, "message_id") or "")
    except (TypeError, ValueError):
        return None
    if (isinstance(identity, list) and len(identity) == 3 and identity[:2] == ["kanban-wake", key]
            and isinstance(identity[2], str) and re.fullmatch(r"[0-9a-f]{32}", identity[2])):
        return "result"
    return None


class TaskWakeResolver:
    """Resolve only configured QQ native Kanban wakes to durable notices.

    ``task_lookup(task_id)`` may be injected. It returns ``{"task": dict,
    "events": [dict], "subscriptions": [dict]}``, with native row fields and
    decoded event payloads/subscription delivery_metadata. The default uses a
    SQLite read-only URI below the explicit data root; it never opens a board
    through an API that could initialize or migrate production state.
    """

    def __init__(self, root, board, group_id, profile, outbox,
                 task_lookup: Callable[[str], Mapping | None] | None = None):
        if not _BOARD.fullmatch(str(board)):
            raise ValueError("A canonical board slug is required")
        if not str(group_id).strip() or not str(profile).strip():
            raise ValueError("An exact QQ route and owning profile are required")
        self.root = Path(root).resolve()
        self.board, self.group_id, self.profile = str(board), str(group_id), str(profile)
        self.outbox = outbox
        self.db_path = (self.root / "kanban.db" if self.board == "default" else
                        self.root / "kanban" / "boards" / self.board / "kanban.db")
        if not self.db_path.resolve().is_relative_to(self.root):
            raise ValueError("Board database must remain under the configured root")
        self.lookup = task_lookup or self._lookup

    def _lookup(self, task_id: str) -> dict | None:
        if not self.db_path.is_file():
            return None
        conn = sqlite3.connect(self.db_path.as_uri() + "?mode=ro", uri=True, timeout=2)
        conn.row_factory = sqlite3.Row
        try:
            conn.execute("PRAGMA query_only=ON")
            task = conn.execute("SELECT id,title,assignee,status,result,session_id FROM tasks WHERE id=?",
                                (task_id,)).fetchone()
            if task is None:
                return None
            events = [dict(row) for row in conn.execute(
                "SELECT id,task_id,kind,payload,created_at,run_id FROM task_events "
                "WHERE task_id=? AND kind IN (" + ",".join("?" for _ in _WAKE_KINDS) + ") "
                "ORDER BY id DESC LIMIT 128", (task_id, *_WAKE_KINDS))]
            subscriptions = [dict(row) for row in conn.execute(
                "SELECT task_id,platform,chat_id,thread_id,user_id,user_id_alt,chat_type,notifier_profile,"
                "delivery_mode,delivery_metadata FROM kanban_notify_subs "
                "WHERE task_id=? AND platform='qqbot' AND chat_id=? AND notifier_profile=?",
                (task_id, self.group_id, self.profile))]
            for row in events:
                row["payload"] = json.loads(row["payload"]) if row["payload"] else {}
            for row in subscriptions:
                row["delivery_metadata"] = json.loads(row["delivery_metadata"]) if row["delivery_metadata"] else {}
            return {"task": dict(task), "events": events, "subscriptions": subscriptions}
        finally:
            conn.close()

    def _owned_source(self, event) -> bool:
        source = _value(event, "source")
        platform = _value(source, "platform")
        platform = _value(platform, "value", platform)
        metadata = _value(event, "metadata")
        return (_value(event, "internal") is True and isinstance(metadata, Mapping)
                and _notification_category(event) in {"result", "diagnostic"}
                and platform == "qqbot" and _value(source, "chat_type") == "group"
                and _string(_value(source, "chat_id")) == self.group_id
                and _string(_value(source, "profile")) == self.profile)

    def _owns_subscription(self, sub: Mapping, source, task_id: str) -> bool:
        metadata = sub.get("delivery_metadata") or {}
        if not isinstance(metadata, Mapping):
            return False
        if not (sub.get("task_id") == task_id and sub.get("platform") == "qqbot"
                and _string(sub.get("chat_id")) == self.group_id
                and sub.get("notifier_profile") == self.profile
                and sub.get("delivery_mode") in {"wake", "notify+wake"}
                and (sub.get("chat_type") or metadata.get("chat_type")) == "group"):
            return False
        # Native FIFO wakes borrow the last admitted source for this group route.
        # Its member may differ from the original creator; the group/profile
        # route and trusted admission own permissions, not the old member id.
        for key in ("thread_id",):
            if _string(sub.get(key)) != _string(_value(source, key)):
                return False
        return all(_string(metadata.get(key)) == _string(_value(source, key))
                   for key in ("scope_id", "parent_chat_id"))

    def _native_text(self, task: Mapping, rows: list[Mapping], language: str) -> str:
        task_id = task["id"]
        kinds = {row["kind"] for row in rows}
        catalog = _CATALOG[language]
        status = catalog["joiner"].join(catalog["statuses"][index] for index, kind in enumerate(_WAKE_KINDS)
                                       if kind in kinds)
        title, assignee = _string(task.get("title") or task_id)[:120], _string(task.get("assignee"))
        if language == "zh":
            text = (f"[kanban] 任务 {task_id} {status}。\n标题: {title}\n执行者: @{assignee}"
                    f"\n看板: {self.board}\n\n请检查结果或决定下一步动作。")
        else:
            text = (f"[kanban] Task {task_id} {status}.\nTitle: {title}\nAssignee: @{assignee}"
                    f"\nBoard: {self.board}\n\nCheck the result or decide the next step.")
        handoff = review_detail = ""
        for row in sorted(rows, key=lambda item: item["id"]):
            kind, payload = row["kind"], row.get("payload") or {}
            if kind == "completed":
                summary = _first_line(payload.get("summary"), 200) or _first_line(task.get("result"), 160)
                if summary:
                    handoff = summary
            elif kind == "review_requested" and payload.get("summary"):
                handoff = _first_line(payload["summary"], 200)
            elif kind == "changes_requested":
                review_detail = _safe_review_reason(payload.get("reason")) or catalog["review_default"]
        if handoff:
            text += ("\n结果：" if language == "zh" else "\nResult: ") + handoff
        if review_detail:
            text += "\n" + catalog["review_detail"].format(reason=review_detail)
        return text + "\n\n" + _GUIDANCE[language]

    def notice_payload(self, task: Mapping, row: Mapping) -> dict:
        """Stable enqueue arguments from a trusted native task and immutable event.

        Gateway lifecycle consumers may reuse this method, so the exact same
        event always yields the same idempotency key and public payload.
        """
        task_id, kind = task.get("id"), row.get("kind")
        if (not isinstance(task_id, str) or not re.fullmatch(_TASK, task_id)
                or row.get("task_id") != task_id or kind not in _WAKE_KINDS
                or type(row.get("id")) is not int or row["id"] <= 0):
            raise ValueError("A matching native task and event are required")
        payload = row.get("payload") or {}
        if not isinstance(payload, Mapping):
            raise ValueError("Native event payload must be an object")
        if kind == "completed":
            detail = _first_line(payload.get("summary"), 700) or "任务已完成，请查看任务卡中的交付结果。"
            public_kind = "completed"
        elif kind in {"blocked", "block_loop_detected"}:
            reason = _first_line(payload.get("reason"), 660) or "等待下一步处理。"
            detail = ("需要处理：" if payload.get("kind") == "needs_input" else "已暂停：") + reason
            public_kind = "progress"
        else:
            public_kind = "progress"
            if kind == "review_requested":
                detail = "已交付评审：" + (_first_line(payload.get("summary"), 660) or "等待核对交付结果。")
            elif kind == "changes_requested":
                detail = "评审要求修改：" + (_safe_review_reason(payload.get("reason")) or "请查看任务卡中的评审反馈。")
            elif kind == "gave_up":
                detail = "已暂停：连续执行失败，重试次数已用尽，需要检查任务卡中的错误记录。"
            elif kind == "crashed":
                detail = "执行曾中断，等待重新执行。"
            else:
                detail = "执行已超时，等待重新执行。"
        from .task_privacy_loader import decorate_task
        detail = decorate_task(detail[:740], root=self.root, board=self.board,
                               task_id=task_id, run_id=row.get('run_id'))
        return dict(task_id=task_id, kind=public_kind,
                    event_key=f"kanban:{self.board}:{task_id}:{row['id']}:{kind}",
                    group_id=self.group_id, profile=self.profile, executor="", text=detail,
                    metadata={"task_channel_board": self.board, "task_event_id": row["id"],
                              "task_event_kind": kind}, progress_interval=0)

    def __call__(self, event) -> dict | None:
        if not self._owned_source(event):
            return None
        raw_text = _value(event, "text")
        if not isinstance(raw_text, str) or len(raw_text) > 8192:
            return None
        text = _normalize_native_text(raw_text)
        first_line = text.split("\n", 1)[0]
        parsed = next(((language, regex.fullmatch(first_line)) for language, regex in _HEADERS
                       if regex.fullmatch(first_line)), None)
        if parsed is None:
            return None
        language, match = parsed
        task_id, kinds = match["task_id"], _STATUS_KINDS[language].get(match["status"])
        if not kinds:
            return None
        try:
            record = self.lookup(task_id)
        except (OSError, sqlite3.Error, ValueError, TypeError) as exc:
            logger.warning("Task wake lookup failed (%s)", type(exc).__name__)
            return None
        if not isinstance(record, Mapping):
            return None
        task = record.get("task") or {}
        if (not isinstance(task, Mapping) or task.get("id") != task_id
                or task.get("status") not in {"ready", "running", "blocked", "review", "triage", "done"}):
            return None
        subs = record.get("subscriptions") or []
        if not any(isinstance(sub, Mapping) and self._owns_subscription(sub, _value(event, "source"), task_id)
                   for sub in subs):
            return None
        rows = [row for row in (record.get("events") or []) if isinstance(row, Mapping)
                and row.get("task_id") == task_id and type(row.get("id")) is int
                and row["id"] > 0 and row.get("kind") in _WAKE_KINDS]
        if not rows:
            return None
        row = max(rows, key=lambda value: value["id"])
        if row["kind"] not in kinds:
            return None
        if row["kind"] == "completed" and task.get("status") != "done":
            return None
        if row["kind"] == "blocked" and task.get("status") != "blocked":
            return None
        selected = [max((item for item in rows if item["kind"] == kind), key=lambda item: item["id"], default=None)
                    for kind in kinds]
        if any(item is None or not isinstance(item.get("payload") or {}, Mapping) for item in selected):
            return None
        category = "diagnostic" if all(_diagnostic(item) for item in selected) else "result"
        if _notification_category(event) != category:
            return None
        try:
            if text != _normalize_native_text(self._native_text(task, selected, language)):
                return None
            return self.outbox.enqueue(**self.notice_payload(task, row))
        except (ImportError, ValueError, TypeError) as exc:
            logger.warning("Task wake validation failed (%s)", type(exc).__name__)
            return None
