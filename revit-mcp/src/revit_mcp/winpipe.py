from __future__ import annotations

import ctypes
import os
import queue
import threading
import time
from ctypes import wintypes
from dataclasses import dataclass
from typing import Any

from .protocol import ProtocolError, encode_frame, read_frame, write_all

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
OPEN_EXISTING = 3
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class PipeTransportError(ProtocolError):
    def __init__(self, code: str, message: str, winerror: int | None = None):
        super().__init__(code, message)
        self.winerror = winerror

    def as_dict(self) -> dict[str, Any]:
        return {
            "code": self.code,
            "message": str(self),
            "winerror": self.winerror,
            "category": "pipe_or_edr",
            "remediation": "Confirm Bridge ON, same Windows user, discovery freshness, and AV/EDR named-pipe policy.",
        }


class Win32PipeApi:
    def __init__(self) -> None:
        if os.name != "nt":
            raise OSError("Windows named pipes require Windows")
        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel32.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
        self.kernel32.WaitNamedPipeW.restype = wintypes.BOOL
        self.kernel32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        self.kernel32.CreateFileW.restype = wintypes.HANDLE
        self.kernel32.ReadFile.argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
        self.kernel32.WriteFile.argtypes = [wintypes.HANDLE, wintypes.LPCVOID, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
        self.kernel32.CancelIoEx.argtypes = [wintypes.HANDLE, wintypes.LPVOID]
        self.kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel32.CloseHandle.restype = wintypes.BOOL

    def connect(self, pipe_name: str, timeout_ms: int) -> int:
        path = "\\\\.\\pipe\\" + pipe_name
        if not self.kernel32.WaitNamedPipeW(path, timeout_ms):
            error = ctypes.get_last_error()
            raise PipeTransportError("wait_named_pipe_failed", ctypes.FormatError(error), error)
        handle = self.kernel32.CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, None, OPEN_EXISTING, 0, None)
        if handle == INVALID_HANDLE_VALUE:
            error = ctypes.get_last_error()
            raise PipeTransportError("create_file_failed", ctypes.FormatError(error), error)
        return handle

    def read(self, handle: int, count: int) -> bytes:
        buffer = ctypes.create_string_buffer(count)
        read = wintypes.DWORD()
        if not self.kernel32.ReadFile(handle, buffer, count, ctypes.byref(read), None):
            error = ctypes.get_last_error()
            raise PipeTransportError("read_file_failed", ctypes.FormatError(error), error)
        return buffer.raw[: read.value]

    def write(self, handle: int, data: bytes) -> int:
        written = wintypes.DWORD()
        buffer = ctypes.create_string_buffer(data)
        if not self.kernel32.WriteFile(handle, buffer, len(data), ctypes.byref(written), None):
            error = ctypes.get_last_error()
            raise PipeTransportError("write_file_failed", ctypes.FormatError(error), error)
        return written.value

    def cancel(self, handle: int) -> None:
        self.kernel32.CancelIoEx(handle, None)

    def close(self, handle: int) -> None:
        self.kernel32.CloseHandle(handle)


@dataclass
class _Task:
    pipe_name: str
    message: dict[str, Any]
    timeout_ms: int
    done: threading.Event
    deadline: float
    cancelled: bool = False
    timer: threading.Timer | None = None
    result: dict[str, Any] | None = None
    error: BaseException | None = None


class PipeIoThread:
    """All Wait/Create/Read/Write calls execute on this one dedicated thread."""

    def __init__(self, api: Win32PipeApi | None = None) -> None:
        self._api = api or Win32PipeApi()
        self._tasks: queue.Queue[_Task | None] = queue.Queue()
        self._current: int | None = None
        self._active: _Task | None = None
        self._gate = threading.Lock()
        self._closed = False
        self._thread = threading.Thread(target=self._run, name="revit-mcp-pipe-io", daemon=True)
        self._thread.start()

    def request(self, pipe_name: str, message: dict[str, Any], timeout_ms: int) -> dict[str, Any]:
        if isinstance(timeout_ms, bool) or not isinstance(timeout_ms, int) or not 1 <= timeout_ms <= 600_000:
            raise ValueError("timeout_ms must be an integer between 1 and 600000")
        task = _Task(pipe_name, message, timeout_ms, threading.Event(), time.monotonic() + timeout_ms / 1000.0)
        with self._gate:
            if self._closed:
                raise PipeTransportError("transport_closed", "named-pipe transport is closed")
            self._tasks.put(task)
        if not task.done.wait(max(0.0, task.deadline - time.monotonic())):
            self._cancel_task(task)
            raise PipeTransportError("pipe_timeout", "named-pipe request exceeded its timeout")
        if task.error:
            raise task.error
        return task.result or {}

    def cancel_current(self) -> None:
        with self._gate:
            task = self._active
        if task is not None:
            self._cancel_task(task)

    def _cancel_task(self, task: _Task) -> None:
        with self._gate:
            if task.done.is_set():
                return
            task.cancelled = True
            if task.error is None:
                task.error = PipeTransportError("pipe_timeout", "named-pipe request exceeded its timeout")
            if self._active is task and self._current is not None:
                self._api.cancel(self._current)

    def _watch_timeout(self, task: _Task) -> None:
        self._cancel_task(task)
        with self._gate:
            if self._active is task and not task.done.is_set():
                # Cancellation can race the start of synchronous ReadFile/WriteFile.
                # Keep cancelling this task's handle until its worker has unwound.
                task.timer = threading.Timer(0.05, self._watch_timeout, args=(task,))
                task.timer.daemon = True
                task.timer.start()

    @staticmethod
    def _check_live(task: _Task) -> None:
        if task.cancelled or time.monotonic() >= task.deadline:
            raise PipeTransportError("pipe_timeout", "named-pipe request exceeded its timeout")

    def close(self) -> None:
        with self._gate:
            if self._closed:
                return
            self._closed = True
            while True:
                try:
                    pending = self._tasks.get_nowait()
                except queue.Empty:
                    break
                if pending is not None:
                    pending.cancelled = True
                    pending.error = PipeTransportError("transport_closed", "named-pipe transport is closed")
                    pending.done.set()
            self._tasks.put(None)
        self.cancel_current()
        self._thread.join(timeout=2)

    def _run(self) -> None:
        while True:
            task = self._tasks.get()
            if task is None:
                return
            handle = None
            try:
                with self._gate:
                    if self._closed:
                        raise PipeTransportError("transport_closed", "named-pipe transport is closed")
                    self._check_live(task)
                    self._active = task
                payload = encode_frame(task.message)
                self._check_live(task)
                remaining = max(1, int((task.deadline - time.monotonic()) * 1000))
                handle = self._api.connect(task.pipe_name, remaining)
                with self._gate:
                    self._current = handle
                    self._check_live(task)
                    task.timer = threading.Timer(max(0.0, task.deadline - time.monotonic()), self._watch_timeout, args=(task,))
                    task.timer.daemon = True
                    task.timer.start()

                def write(data):
                    self._check_live(task)
                    return self._api.write(handle, data)

                def read(count):
                    self._check_live(task)
                    return self._api.read(handle, count)

                write_all(write, payload)
                task.result = read_frame(read)
            except ProtocolError as error:
                with self._gate:
                    if task.error is None:
                        task.error = PipeTransportError(error.code, str(error), getattr(error, "winerror", None))
            except BaseException as error:
                with self._gate:
                    if task.error is None:
                        task.error = error
            finally:
                with self._gate:
                    if task.timer:
                        task.timer.cancel()
                    if self._active is task:
                        self._current = None
                        self._active = None
                    task.done.set()
                if handle is not None:
                    self._api.close(handle)
