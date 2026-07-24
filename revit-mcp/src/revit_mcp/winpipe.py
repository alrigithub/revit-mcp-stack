from __future__ import annotations

import ctypes
import os
import queue
import threading
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
    result: dict[str, Any] | None = None
    error: BaseException | None = None


class PipeIoThread:
    """All Wait/Create/Read/Write calls execute on this one dedicated thread."""

    def __init__(self, api: Win32PipeApi | None = None) -> None:
        self._api = api or Win32PipeApi()
        self._tasks: queue.Queue[_Task | None] = queue.Queue()
        self._current: int | None = None
        self._thread = threading.Thread(target=self._run, name="revit-mcp-pipe-io", daemon=True)
        self._thread.start()

    def request(self, pipe_name: str, message: dict[str, Any], timeout_ms: int) -> dict[str, Any]:
        task = _Task(pipe_name, message, timeout_ms, threading.Event())
        self._tasks.put(task)
        if not task.done.wait((timeout_ms / 1000.0) + 2.0):
            self.cancel_current()
            raise PipeTransportError("pipe_timeout", "named-pipe request exceeded its timeout")
        if task.error:
            raise task.error
        return task.result or {}

    def cancel_current(self) -> None:
        handle = self._current
        if handle is not None:
            self._api.cancel(handle)

    def close(self) -> None:
        self.cancel_current()
        self._tasks.put(None)
        self._thread.join(timeout=2)

    def _run(self) -> None:
        while True:
            task = self._tasks.get()
            if task is None:
                return
            handle = None
            timer = None
            try:
                handle = self._api.connect(task.pipe_name, task.timeout_ms)
                self._current = handle
                timer = threading.Timer(task.timeout_ms / 1000.0, self.cancel_current)
                timer.daemon = True
                timer.start()
                write_all(lambda data: self._api.write(handle, data), encode_frame(task.message))
                task.result = read_frame(lambda count: self._api.read(handle, count))
            except BaseException as error:
                task.error = error
            finally:
                if timer:
                    timer.cancel()
                self._current = None
                if handle is not None:
                    self._api.close(handle)
                task.done.set()
