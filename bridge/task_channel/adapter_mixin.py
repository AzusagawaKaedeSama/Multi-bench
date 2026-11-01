# Author: Codex, 2026-10-19
"""Optional mixin for the existing CoordinatorQQAdapter, with an injected task resolver.

Place before CoordinatorQQAdapter in the MRO. Configure at adapter construction.
The resolver must validate an internal wake against the task DB and return the
already persisted outbox notice; ordinary user messages must return None.
"""
from __future__ import annotations

import asyncio
from contextvars import ContextVar
import inspect
import logging

try:
    from .outbox_gateway import DeliveryOutcome, OutboxDispatcher, hermes_send_result
except ImportError:
    from outbox_gateway import DeliveryOutcome, OutboxDispatcher, hermes_send_result


_SEND_WITNESS = ContextVar("task_channel_send_witness", default=None)
_TASK_WAKE_NOTICE = ContextVar("task_channel_wake_notice", default=None)
_TASK_INTAKE_EVENT = ContextVar("task_channel_intake_event", default=None)
logger = logging.getLogger(__name__)


class TaskNoticeAdapterMixin:
    def configure_task_channel(self, *, outbox, group_id: str, profile: str,
                               resolve_event=None, result_factory=None, lookup=None):
        self._task_channel_outbox = outbox
        self._task_channel_group = str(group_id)
        self._task_channel_profile = str(profile)
        self._task_channel_resolve_event = resolve_event
        self._task_channel_result_factory = result_factory
        self._task_channel_dispatcher = OutboxDispatcher(outbox, self._send_task_transport, lookup=lookup)
        self._task_channel_poll_task = None
        self._task_channel_event_notices = {}

    async def connect(self, *, is_reconnect=False):
        connected = await super().connect(is_reconnect=is_reconnect)
        if (connected and hasattr(self, "_task_channel_outbox")
                and (self._task_channel_poll_task is None or self._task_channel_poll_task.done())):
            self._task_channel_poll_task = asyncio.create_task(self._poll_task_notices())
        return connected

    async def disconnect(self):
        poll = getattr(self, "_task_channel_poll_task", None)
        self._task_channel_poll_task = None
        if poll is not None and poll is not asyncio.current_task():
            poll.cancel()
            try:
                await poll
            except asyncio.CancelledError:
                pass
        return await super().disconnect()

    async def _poll_task_notices(self):
        while True:
            try:
                await asyncio.to_thread(self._task_channel_outbox.retry_due,
                    group_id=self._task_channel_group, profile=self._task_channel_profile)
                rows = await asyncio.to_thread(self._task_channel_outbox.pending, limit=30,
                    group_id=self._task_channel_group, profile=self._task_channel_profile)
                for row in rows:
                    if (str(row["group_id"]) != self._task_channel_group
                            or str(row["profile"]) != self._task_channel_profile):
                        continue
                    if row["status"] == "unknown" and self._task_channel_dispatcher.lookup is None:
                        continue
                    await self.send_task_notice(row["id"])
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                logger.warning("Task notice poll failed (%s)", type(exc).__name__)
            await asyncio.sleep(2)

    async def _resolve_task_event(self, event):
        resolver = getattr(self, "_task_channel_resolve_event", None)
        if (resolver is None or not getattr(event, "internal", False)
                or str(getattr(event.source, "chat_id", "")) != self._task_channel_group):
            return None
        row = resolver(event)
        if inspect.isawaitable(row):
            row = await row
        if row is not None:
            row = self._task_channel_outbox.get(int(row["id"]))
            if (row["kind"] not in {"completed", "progress"} or str(row["group_id"]) != self._task_channel_group
                    or str(row["profile"]) != self._task_channel_profile):
                raise ValueError("Task event resolver returned an unrelated notice")
            message_id = getattr(event, "message_id", None)
            if message_id:
                mapping = self._task_channel_event_notices
                mapping[str(message_id)] = row["id"]
                while len(mapping) > 1024:
                    mapping.pop(next(iter(mapping)))
        return row

    async def _process_message_background(self, event, session_key):
        row = await self._resolve_task_event(event)
        token = _TASK_WAKE_NOTICE.set(row)
        intake_token = _TASK_INTAKE_EVENT.set(self._intake_identity(event))
        try:
            return await super()._process_message_background(event, session_key)
        finally:
            _TASK_INTAKE_EVENT.reset(intake_token)
            _TASK_WAKE_NOTICE.reset(token)

    def _intake_identity(self, event):
        if not hasattr(self, '_task_channel_outbox') or getattr(event, 'internal', False):
            return None
        source = getattr(event, 'source', None)
        group = str(getattr(source, 'chat_id', '') or '')
        message_id = str(getattr(event, 'message_id', '') or '')
        if group != self._task_channel_group or not message_id:
            return None
        return {'message_id': message_id, 'group_id': group,
                'source_profile': str(getattr(source, 'profile', None) or self._task_channel_profile)}

    async def _received_notices(self, *, message_id, group_id, source_profile):
        if (not hasattr(self, '_task_channel_outbox')
                or str(group_id) != self._task_channel_group or not message_id):
            return []
        return await asyncio.to_thread(self._task_channel_outbox.received_for_message,
            str(message_id), group_id=str(group_id), profile=self._task_channel_profile,
            source_profile=str(source_profile))

    async def _received_for_event(self, event):
        identity = self._intake_identity(event)
        return await self._received_notices(**identity) if identity else []

    async def _send_received_notices(self, rows):
        outcomes = [await self.send_task_notice(row['id']) for row in rows]
        representative = next((row for row in outcomes if row['status'] != 'sent'), outcomes[-1])
        result = self._task_notice_result(representative)
        result.raw_response.update(task_outbox_ids=[row['id'] for row in outcomes],
                                   task_outbox_statuses=[row['status'] for row in outcomes],
                                   message_ids=[row.get('message_id') for row in outcomes])
        return result

    async def _ensure_connected(self):
        connected = await super()._ensure_connected()
        witness = _SEND_WITNESS.get()
        if not connected and witness is not None:
            witness["outcome"] = DeliveryOutcome("not_sent", reason="transport_not_connected_before_post")
        return connected

    async def _post_message(self, path, body):
        witness = _SEND_WITNESS.get()
        try:
            result = await super()._post_message(path, body)
        except Exception as exc:
            # Parent QQAdapter collapses these exceptions into SendResult(False).
            # Capture certainty before that boundary, limited to task-owned sends.
            status = getattr(exc, "status", None)
            if (witness is not None and type(exc).__name__ == "QQAPIError"
                    and isinstance(status, int) and 400 <= status < 500):
                witness["outcome"] = DeliveryOutcome("not_sent", reason=f"qq_http_rejected_{status}")
            raise
        if witness is not None:
            witness["outcome"] = hermes_send_result(result)
        return result

    async def _send_task_transport(self, row, content):
        if (str(row["group_id"]) != self._task_channel_group
                or str(row["profile"]) != self._task_channel_profile):
            return DeliveryOutcome("not_sent", reason="task_notice_route_mismatch")
        chunks = self.truncate_message(self.format_message(content), self.MAX_MESSAGE_LENGTH)
        if len(chunks) != 1:
            return DeliveryOutcome("not_sent", reason="task_notice_requires_single_post")
        witness = {}
        token = _SEND_WITNESS.set(witness)
        try:
            metadata = {**row["metadata"], "chat_type": "group", "_task_channel_owned": True}
            # Bypass this mixin's explicit-outbox send door to avoid recursion.
            result = await super().send(row["group_id"], content, metadata=metadata)
            return witness.get("outcome") or hermes_send_result(result)
        finally:
            _SEND_WITNESS.reset(token)

    async def send_task_notice(self, notice_id: int):
        return await self._task_channel_dispatcher.deliver(notice_id)

    def _task_notice_result(self, row):
        factory = self._task_channel_result_factory
        if factory is None:
            # Called only inside a loaded gateway; isolated unit tests inject one.
            from gateway.platforms.base import SendResult
            factory = SendResult
        success = row["status"] == "sent"
        return factory(success=success, message_id=row.get("message_id"),
                       error=None if success else f"task_outbox_{row['status']}", retryable=False,
                       raw_response={"task_outbox_id": row["id"], "task_outbox_status": row["status"],
                                     "id": row.get("message_id")})

    async def send(self, chat_id, content, reply_to=None, metadata=None):
        owned_id = (metadata or {}).get("_task_channel_notice_id")
        if owned_id is None and hasattr(self, "_task_channel_event_notices"):
            owned_id = self._task_channel_event_notices.get(str(reply_to or ""))
        contextual = _TASK_WAKE_NOTICE.get()
        if owned_id is None and contextual is not None and str(chat_id) == str(contextual["group_id"]):
            owned_id = contextual["id"]
        if owned_id is not None and hasattr(self, "_task_channel_outbox"):
            row = self._task_channel_outbox.get(int(owned_id))
            if str(chat_id) != str(row["group_id"]):
                raise ValueError("Explicit outbox message targets a different group")
            return self._task_notice_result(await self.send_task_notice(row["id"]))
        source_profile = str((metadata or {}).get('_task_channel_source_profile')
                             or (metadata or {}).get('hermes_profile')
                             or getattr(self, '_task_channel_profile', ''))
        receipts = await self._received_notices(message_id=reply_to, group_id=chat_id,
                                                source_profile=source_profile)
        intake = _TASK_INTAKE_EVENT.get()
        if not receipts and intake is not None and str(chat_id) == intake['group_id']:
            receipts = await self._received_notices(**intake)
        if receipts:
            return await self._send_received_notices(receipts)
        return await super().send(chat_id, content, reply_to=reply_to, metadata=metadata)

    async def send_final_ledgered(self, event, session_key, text_content, metadata, *,
                                reply_to, is_ephemeral_response=False):
        row = await self._resolve_task_event(event)
        if row is not None:
            # The task row owns the final POST, even when the supervisor's wake
            # generates a different paraphrase on replay. Its durable id stays fixed.
            stored = self._task_channel_outbox.get(int(row["id"]))
            if stored["kind"] not in {"completed", "progress"}:
                raise ValueError("Internal task wake must reference completion/progress")
            result = self._task_notice_result(await self.send_task_notice(stored["id"]))
            return result, self
        receipts = await self._received_for_event(event)
        if receipts:
            return await self._send_received_notices(receipts), self
        return await super().send_final_ledgered(event, session_key, text_content, metadata,
                    reply_to=reply_to, is_ephemeral_response=is_ephemeral_response)

    async def _deliver_attachments(self, event, extracted, metadata, *, anything_sent, record_delivery):
        if await self._resolve_task_event(event) is not None or await self._received_for_event(event):
            return  # The canonical task notice carries its approved result text.
        return await super()._deliver_attachments(event, extracted, metadata,
                    anything_sent=anything_sent, record_delivery=record_delivery)
