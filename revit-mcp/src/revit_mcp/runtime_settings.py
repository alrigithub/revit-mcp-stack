from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

TOOL_NAME_PATTERN = re.compile(r"^[a-z][a-z0-9_]{0,63}$")


@dataclass(frozen=True)
class RuntimeSettings:
    path: Path
    saved_tools_root: Path
    disabled_mcp_tools: frozenset[str]
    error: str | None = None
    saved_tools_paths: tuple[Path, ...] = ()
    disabled_tool_paths: frozenset[str] = frozenset()

    def is_path_disabled(self, root: Path) -> bool:
        return os.path.normcase(os.path.normpath(str(root))) in self.disabled_tool_paths


def default_base_root() -> Path:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        raise RuntimeError("LOCALAPPDATA is unavailable")
    return Path(local) / "RevitMcp"


def settings_path(base_root: Path | None = None) -> Path:
    return (base_root or default_base_root()) / "settings.json"


def default_saved_tools_root(base_root: Path | None = None) -> Path:
    return (base_root or default_base_root()) / "tools"


def load_settings(base_root: Path | None = None) -> RuntimeSettings:
    base = base_root or default_base_root()
    path = settings_path(base)
    fallback = default_saved_tools_root(base)
    if not path.is_file():
        return RuntimeSettings(path, fallback, frozenset())
    try:
        raw: Any = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(raw, dict):
            raise ValueError("settings must be a JSON object")
        root_value = raw.get("saved_tools_root")
        if root_value is None:
            root = fallback
        elif not isinstance(root_value, str) or not root_value.strip():
            raise ValueError("saved_tools_root must be a non-empty absolute path")
        else:
            root = Path(os.path.expandvars(root_value)).expanduser()
            if not root.is_absolute():
                raise ValueError("saved_tools_root must be an absolute path")
        paths_value = raw.get("saved_tools_paths") or []
        if not isinstance(paths_value, list) or not all(isinstance(item, str) and item.strip() for item in paths_value):
            raise ValueError("saved_tools_paths must be a list of non-empty absolute paths")
        extra_paths: list[Path] = []
        for item in paths_value:
            candidate = Path(os.path.expandvars(item)).expanduser()
            if not candidate.is_absolute():
                raise ValueError("saved_tools_paths entries must be absolute paths: %r" % item)
            extra_paths.append(candidate)
        disabled_value = raw.get("disabled_mcp_tools", [])
        if not isinstance(disabled_value, list) or not all(isinstance(item, str) for item in disabled_value):
            raise ValueError("disabled_mcp_tools must be a list of tool names")
        invalid = sorted(name for name in disabled_value if not TOOL_NAME_PATTERN.fullmatch(name))
        if invalid:
            raise ValueError("invalid disabled MCP tool names: %s" % invalid)
        disabled_paths_value = raw.get("disabled_tool_paths") or []
        if not isinstance(disabled_paths_value, list) or not all(isinstance(item, str) and item.strip() for item in disabled_paths_value):
            raise ValueError("disabled_tool_paths must be a list of non-empty absolute paths")
        disabled_paths: set[str] = set()
        for item in disabled_paths_value:
            candidate = Path(os.path.expandvars(item)).expanduser()
            if not candidate.is_absolute():
                raise ValueError("disabled_tool_paths entries must be absolute paths: %r" % item)
            disabled_paths.add(os.path.normcase(os.path.normpath(str(candidate))))
        return RuntimeSettings(path, root, frozenset(disabled_value), saved_tools_paths=tuple(extra_paths),
                               disabled_tool_paths=frozenset(disabled_paths))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        return RuntimeSettings(path, fallback, frozenset(), str(error))

