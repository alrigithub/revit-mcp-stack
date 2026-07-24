from __future__ import annotations

import ctypes
import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Callable


@dataclass(frozen=True)
class RevitInstance:
    pid: int
    process_start_utc_ticks: int
    revit_year: str
    protocol_version: str
    pipe_name: str
    bridge_state: str
    instance_nonce: str
    written_utc: str


def discovery_root() -> Path:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        raise RuntimeError("LOCALAPPDATA is unavailable")
    return Path(local) / "RevitMcp" / "instances"


def process_start_filetime(pid: int) -> int | None:
    if os.name != "nt":
        return None
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        creation = ctypes.c_uint64()
        exit_time = ctypes.c_uint64()
        kernel = ctypes.c_uint64()
        user = ctypes.c_uint64()
        if not kernel32.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exit_time), ctypes.byref(kernel), ctypes.byref(user)):
            return None
        return creation.value
    finally:
        kernel32.CloseHandle(handle)


def list_instances(
    root: Path | None = None,
    start_lookup: Callable[[int], int | None] = process_start_filetime,
    cleanup_stale: bool = False,
) -> list[RevitInstance]:
    root = root or discovery_root()
    if not root.exists():
        return []
    live: list[RevitInstance] = []
    for path in root.glob("*.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            instance = RevitInstance(**data)
            valid = instance.bridge_state == "on" and start_lookup(instance.pid) == instance.process_start_utc_ticks
            if valid:
                live.append(instance)
            elif cleanup_stale:
                path.unlink(missing_ok=True)
        except (OSError, ValueError, TypeError, json.JSONDecodeError):
            if cleanup_stale:
                path.unlink(missing_ok=True)
    return sorted(live, key=lambda item: (item.revit_year, item.pid))


def pyrevit_install(appdata: Path | None = None) -> dict[str, object]:
    appdata = appdata or Path(os.environ.get("APPDATA", ""))
    config = appdata / "pyRevit" / "pyRevit_config.ini"
    clones: list[Path] = []
    if config.is_file():
        try:
            for line in config.read_text(encoding="utf-8").splitlines():
                if line.strip().startswith("clones"):
                    _, _, raw = line.partition("=")
                    clones = [Path(str(item)) for item in json.loads(raw.strip()).values()]
                    break
        except (OSError, ValueError, json.JSONDecodeError):
            clones = []
    clones.append(appdata / "pyRevit-Master")
    for clone in clones:
        version_file = clone / "pyrevitlib" / "pyrevit" / "version"
        try:
            if version_file.is_file():
                return {"installed": True, "version": version_file.read_text(encoding="utf-8").strip(), "clone": str(clone)}
        except OSError:
            continue
    return {"installed": config.is_file(), "version": None, "clone": None}


def get_instance(pid: int, root: Path | None = None) -> RevitInstance:
    matches = [item for item in list_instances(root) if item.pid == pid]
    if not matches:
        raise LookupError(f"No live Bridge ON discovery record for Revit PID {pid}")
    return matches[0]
