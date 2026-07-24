from __future__ import annotations

import datetime as dt
import uuid
from typing import Any

from .discovery import get_instance, list_instances
from .protocol import PROTOCOL_VERSION
from .winpipe import PipeIoThread, PipeTransportError


class BridgeClient:
    def __init__(self, transport: PipeIoThread | None = None) -> None:
        self.transport = transport or PipeIoThread()

    def close(self) -> None:
        self.transport.close()

    def instances(self) -> list[dict[str, Any]]:
        return [item.__dict__ for item in list_instances(cleanup_stale=True)]

    def call(
        self,
        pid: int,
        tool: str,
        arguments: dict[str, Any] | None = None,
        *,
        document_session: str | None = None,
        document_generation: int | None = None,
        transaction_mode: str | None = None,
        timeout_ms: int = 30_000,
        request_id: str | None = None,
        idempotency_key: str | None = None,
    ) -> dict[str, Any]:
        instance = get_instance(pid)
        request_id = request_id or uuid.uuid4().hex
        deadline = dt.datetime.now(dt.timezone.utc) + dt.timedelta(milliseconds=timeout_ms)
        request = {
            "protocol_version": PROTOCOL_VERSION,
            "request_id": request_id,
            "tool": tool,
            "instance_nonce": instance.instance_nonce,
            "document_session": document_session,
            "document_generation": document_generation,
            "deadline_utc": deadline.isoformat(),
            "idempotency_key": idempotency_key,
            "transaction_mode": transaction_mode,
            "arguments": arguments or {},
        }
        try:
            return self.transport.request(instance.pipe_name, request, timeout_ms)
        except PipeTransportError as error:
            return {"protocol_version": PROTOCOL_VERSION, "request_id": request_id, "state": "failed", "error": error.as_dict(), "omitted_fields": [], "deferred_fields": []}
