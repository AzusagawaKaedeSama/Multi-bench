# Author: Codex, 2026-10-19
"""Injected gateway bridge; no Hermes imports, model calls, or background threads."""
from __future__ import annotations

import asyncio
from dataclasses import dataclass
from typing import Awaitable, Callable

try:
    from .outbox import TaskOutbox, render_notice
except ImportError:
    from outbox import TaskOutbox, render_notice


@dataclass(frozen=True)
class DeliveryOutcome:
    state: str  # sent / not_sent / unknown
    message_id: str = ""
    reason: str = ""

    def __post_init__(self):
        if self.state not in {"sent", "not_sent", "unknown"}:
            raise ValueError("Invalid delivery outcome")
        if self.state == "sent" and not self.message_id:
            raise ValueError("Remote message id is required")


class OutboxDispatcher:
    """A callback makes one send attempt; there is no blind transport retry.

    Lookup returns sent only for the exact outbox notice. not_sent must be an
    authoritative rejection/absence witness, not absence in a partial history page.
    Unknown remains pending operator/transport reconciliation when lookup is absent.
    Native Hermes completion leases still govern admission into the owning session;
    this lease governs only the final QQ POST after that admission.
    """

    def __init__(self, outbox: TaskOutbox,
                 sender: Callable[[dict, str], Awaitable[DeliveryOutcome]], *,
                 lookup: Callable[[dict], Awaitable[DeliveryOutcome]] | None = None,
                 timeout_seconds: float = 35, lease_seconds: float = 90):
        if timeout_seconds <= 0 or lease_seconds <= timeout_seconds:
            raise ValueError("Send lease must exceed positive transport timeout")
        self.outbox, self.sender, self.lookup = outbox, sender, lookup
        self.timeout_seconds, self.lease_seconds = timeout_seconds, lease_seconds

    async def deliver(self, notice_id: int) -> dict:
        row = self.outbox.get(notice_id)
        if row["status"] == "sending":
            self.outbox.claim(notice_id, lease_seconds=self.lease_seconds)
            row = self.outbox.get(notice_id)
        if row["status"] == "unknown":
            return await self.reconcile(notice_id)
        if row["status"] != "pending":
            return row
        claimed = self.outbox.claim(notice_id, lease_seconds=self.lease_seconds)
        if claimed is None:
            return self.outbox.get(notice_id)
        try:
            outcome = await asyncio.wait_for(
                self.sender(claimed, render_notice(claimed)), timeout=self.timeout_seconds)
            if not isinstance(outcome, DeliveryOutcome):
                raise TypeError("Transport must return DeliveryOutcome")
        except asyncio.CancelledError:
            self.outbox.settle(notice_id, claimed["claim_token"], state="unknown", reason="send_cancelled")
            raise
        except Exception as exc:
            # Neither timeout nor an arbitrary client exception proves non-delivery.
            outcome = DeliveryOutcome("unknown", reason=type(exc).__name__)
        state = {"sent": "sent", "not_sent": "failed", "unknown": "unknown"}[outcome.state]
        self.outbox.settle(notice_id, claimed["claim_token"], state=state,
                           message_id=outcome.message_id, reason=outcome.reason)
        return self.outbox.get(notice_id)

    async def reconcile(self, notice_id: int) -> dict:
        row = self.outbox.get(notice_id)
        if row["status"] != "unknown" or self.lookup is None:
            return row
        try:
            outcome = await asyncio.wait_for(self.lookup(row), timeout=self.timeout_seconds)
        except asyncio.CancelledError:
            raise
        except Exception:
            return self.outbox.get(notice_id)
        if not isinstance(outcome, DeliveryOutcome) or outcome.state == "unknown":
            return self.outbox.get(notice_id)
        state = "sent" if outcome.state == "sent" else "failed"
        try:
            return self.outbox.resolve_unknown(notice_id, state=state,
                evidence=outcome.reason or "authoritative_transport_lookup", message_id=outcome.message_id)
        except ValueError:
            # Another reconciler or a late successful send may have settled it first.
            return self.outbox.get(notice_id)


def hermes_send_result(result) -> DeliveryOutcome:
    """Conservative mapping for the installed QQAdapter's SendResult.

    QQAdapter creates a synthetic message_id if the API omits id, so only raw
    response id counts as remote confirmation. Failure text is not parsed into
    certainty: the caller may supply task_delivery_state='not_sent' only when its
    transport has a structured rejection witness.
    """
    raw = getattr(result, "raw_response", None) or {}
    remote_id = str(raw.get("id") or raw.get("message_id") or "")
    if getattr(result, "success", False) and remote_id:
        return DeliveryOutcome("sent", message_id=remote_id)
    if raw.get("task_delivery_state") == "not_sent":
        return DeliveryOutcome("not_sent", reason="structured_transport_rejection")
    return DeliveryOutcome("unknown", reason="qq_delivery_unconfirmed")
