from __future__ import annotations

import json
import struct
from collections.abc import Callable
from typing import Any

PROTOCOL_VERSION = "0.1"
MAX_FRAME_BYTES = 4 * 1024 * 1024


class ProtocolError(RuntimeError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def encode_frame(message: dict[str, Any], maximum: int = MAX_FRAME_BYTES) -> bytes:
    payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if not 0 < len(payload) <= maximum:
        raise ProtocolError("invalid_frame_size", f"frame size {len(payload)} is outside 1..{maximum}")
    return struct.pack("<I", len(payload)) + payload


def decode_payload(payload: bytes) -> dict[str, Any]:
    try:
        value = json.loads(payload.decode("utf-8", errors="strict"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ProtocolError("invalid_json", str(error)) from error
    if not isinstance(value, dict):
        raise ProtocolError("invalid_json", "top-level JSON value must be an object")
    return value


def read_exact(read: Callable[[int], bytes], count: int) -> bytes:
    chunks: list[bytes] = []
    remaining = count
    while remaining:
        chunk = read(remaining)
        if not chunk:
            raise ProtocolError("pipe_disconnected", "peer disconnected during a frame")
        if len(chunk) > remaining:
            raise ProtocolError("pipe_overread", "transport returned more bytes than requested")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def read_frame(read: Callable[[int], bytes], maximum: int = MAX_FRAME_BYTES) -> dict[str, Any]:
    length = struct.unpack("<I", read_exact(read, 4))[0]
    if not 0 < length <= maximum:
        raise ProtocolError("invalid_frame_size", f"frame size {length} is outside 1..{maximum}")
    return decode_payload(read_exact(read, length))


def write_all(write: Callable[[bytes], int], payload: bytes) -> None:
    offset = 0
    while offset < len(payload):
        written = write(payload[offset:])
        if written <= 0:
            raise ProtocolError("pipe_disconnected", "pipe write made no progress")
        offset += written
