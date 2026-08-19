from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from .runtime_settings import load_settings

MANIFEST_VERSION = 1
NAME_PATTERN = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
ENGINES = {"python": ".py", "csharp": ".cs"}
TRANSACTION_MODES = {"read", "auto", "manual", "group"}
PARAM_TYPES = {"string": str, "integer": int, "number": (int, float), "boolean": bool, "array": list, "object": dict}
MAX_DESCRIPTION_CHARS = 500
MAX_SOURCE_BYTES = 100_000
MAX_LISTED_TOOLS = 200


@dataclass(frozen=True)
class SavedTool:
    id: str
    name: str
    group: str
    description: str
    engine: str
    transaction_mode: str
    timeout_ms: int
    params: tuple[dict[str, Any], ...]
    source: str
    enabled: bool
    disabled_reason: str | None


def registry_root() -> Path:
    return load_settings().saved_tools_root


def registry_roots() -> tuple[Path, ...]:
    """Ordered search roots: the primary (writable) root first, then saved_tools_paths, deduplicated."""
    settings = load_settings()
    roots: list[Path] = []
    seen: set[str] = set()
    for candidate in (settings.saved_tools_root, *settings.saved_tools_paths):
        key = os.path.normcase(os.path.normpath(str(candidate)))
        if key not in seen:
            seen.add(key)
            roots.append(candidate)
    return tuple(roots)


def _resolve_roots(root: Path | None) -> tuple[Path, ...]:
    return (root,) if root is not None else registry_roots()


def _relative_id(manifest_path: Path, root: Path) -> tuple[str, str]:
    relative = manifest_path.relative_to(root)
    group_parts = relative.parts[:-1]
    if any(not NAME_PATTERN.fullmatch(part) for part in group_parts):
        raise ValueError("group folder names must match %s" % NAME_PATTERN.pattern)
    group = "/".join(group_parts)
    return ("%s/%s" % (group, manifest_path.stem) if group else manifest_path.stem), group


def _manifest_path(tool_id: str, root: Path) -> Path:
    parts = tool_id.replace("\\", "/").split("/")
    if not parts or any(not NAME_PATTERN.fullmatch(part) for part in parts):
        raise ValueError("tool id segments must match %s" % NAME_PATTERN.pattern)
    return root.joinpath(*parts[:-1], parts[-1] + ".json")


def _disabled_state(manifest_path: Path, root: Path) -> tuple[bool, str | None]:
    tool_marker = manifest_path.with_suffix(".disabled")
    if tool_marker.is_file():
        return False, "tool disabled"
    current = manifest_path.parent
    while current != root:
        if (current / ".disabled").is_file():
            group = current.relative_to(root).as_posix()
            return False, "group %s disabled" % group
        current = current.parent
    return True, None


def _validate_param(entry: Any) -> dict[str, Any]:
    if not isinstance(entry, dict):
        raise ValueError("each param must be an object")
    name = entry.get("name")
    if not isinstance(name, str) or not NAME_PATTERN.match(name):
        raise ValueError("param name must match %s" % NAME_PATTERN.pattern)
    if entry.get("type") not in PARAM_TYPES:
        raise ValueError("param %r type must be one of %s" % (name, sorted(PARAM_TYPES)))
    if not isinstance(entry.get("description"), str) or not entry["description"].strip():
        raise ValueError("param %r needs a non-empty description" % name)
    if not isinstance(entry.get("required"), bool):
        raise ValueError("param %r needs a boolean 'required'" % name)
    return entry


def _load_manifest(manifest_path: Path, root: Path) -> SavedTool:
    raw = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ValueError("manifest must be a JSON object")
    if raw.get("manifest_version") != MANIFEST_VERSION:
        raise ValueError("manifest_version must be %d" % MANIFEST_VERSION)
    name = raw.get("name")
    if not isinstance(name, str) or not NAME_PATTERN.match(name):
        raise ValueError("name must match %s" % NAME_PATTERN.pattern)
    if name != manifest_path.stem:
        raise ValueError("name %r must equal the manifest filename stem %r" % (name, manifest_path.stem))
    description = raw.get("description")
    if not isinstance(description, str) or not description.strip() or len(description) > MAX_DESCRIPTION_CHARS:
        raise ValueError("description must be a non-empty string of at most %d chars" % MAX_DESCRIPTION_CHARS)
    engine = raw.get("engine")
    if engine not in ENGINES:
        raise ValueError("engine must be one of %s" % sorted(ENGINES))
    if raw.get("transaction_mode") not in TRANSACTION_MODES:
        raise ValueError("transaction_mode must be one of %s" % sorted(TRANSACTION_MODES))
    timeout_ms = raw.get("timeout_ms", 30_000)
    if not isinstance(timeout_ms, int) or isinstance(timeout_ms, bool) or not 1 <= timeout_ms <= 600_000:
        raise ValueError("timeout_ms must be an integer between 1 and 600000")
    params_raw = raw.get("params", [])
    if not isinstance(params_raw, list):
        raise ValueError("params must be a list")
    params = tuple(_validate_param(entry) for entry in params_raw)
    if len({entry["name"] for entry in params}) != len(params):
        raise ValueError("param names must be unique")
    source_path = manifest_path.with_suffix(ENGINES[engine])
    if not source_path.is_file():
        raise ValueError("missing source file %s" % source_path.name)
    if source_path.stat().st_size > MAX_SOURCE_BYTES:
        raise ValueError("source exceeds %d bytes" % MAX_SOURCE_BYTES)
    source = source_path.read_text(encoding="utf-8")
    tool_id, group = _relative_id(manifest_path, root)
    enabled, disabled_reason = _disabled_state(manifest_path, root)
    return SavedTool(tool_id, name, group, description.strip(), engine, raw["transaction_mode"],
                     timeout_ms, params, source, enabled, disabled_reason)


def list_saved_tools(root: Path | None = None) -> dict[str, Any]:
    roots = _resolve_roots(root)
    tools: list[dict[str, Any]] = []
    invalid: list[dict[str, str]] = []
    shadowed: list[dict[str, str]] = []
    owner_by_id: dict[str, str] = {}
    seen_files: set[str] = set()
    processed = 0
    truncated = False
    for base in roots:
        manifests = sorted(base.rglob("*.json")) if base.exists() else []
        for path in manifests:
            file_key = os.path.normcase(os.path.normpath(str(path)))
            if file_key in seen_files:
                continue
            seen_files.add(file_key)
            if processed >= MAX_LISTED_TOOLS:
                truncated = True
                break
            processed += 1
            try:
                tool = _load_manifest(path, base)
                if tool.id in owner_by_id:
                    shadowed.append({"id": tool.id, "root": str(base), "shadowed_by": owner_by_id[tool.id]})
                    continue
                owner_by_id[tool.id] = str(base)
                tools.append({"id": tool.id, "name": tool.name, "group": tool.group,
                              "description": tool.description, "engine": tool.engine,
                              "enabled": tool.enabled, "disabled_reason": tool.disabled_reason,
                              "root": str(base)})
            except (OSError, ValueError, json.JSONDecodeError) as ex:
                invalid.append({"file": path.relative_to(base).as_posix(), "reason": str(ex), "root": str(base)})
        if truncated:
            break
    result: dict[str, Any] = {"root": str(roots[0]), "roots": [str(base) for base in roots],
                              "tools": tools, "invalid": invalid, "shadowed": shadowed}
    if truncated:
        result["truncated"] = True
    return result


def load_saved_tool(name: str, root: Path | None = None, allow_disabled: bool = False) -> SavedTool:
    roots = _resolve_roots(root)
    for base in roots:
        manifest_path = _manifest_path(name or "", base)
        if not manifest_path.is_file():
            continue
        # First root owning the id wins; a disabled first hit does NOT fall through to a
        # later root — a lower-precedence script must never run in place of a disabled one.
        tool = _load_manifest(manifest_path, base)
        if not tool.enabled and not allow_disabled:
            raise PermissionError("saved tool %r is disabled: %s" % (tool.id, tool.disabled_reason))
        return tool
    raise LookupError("no saved tool named %r in %s" % (name, ", ".join(str(base) for base in roots)))


def describe_saved_tool(tool: SavedTool) -> dict[str, Any]:
    return {"id": tool.id, "name": tool.name, "group": tool.group, "description": tool.description,
            "engine": tool.engine, "transaction_mode": tool.transaction_mode,
            "timeout_ms": tool.timeout_ms, "params": list(tool.params), "enabled": tool.enabled,
            "disabled_reason": tool.disabled_reason}


def validate_arguments(tool: SavedTool, arguments: dict[str, Any]) -> dict[str, Any]:
    known = {entry["name"]: entry for entry in tool.params}
    unknown = sorted(set(arguments) - set(known))
    if unknown:
        raise ValueError("unknown params %s; allowed: %s" % (unknown, sorted(known)))
    resolved: dict[str, Any] = {}
    for name, entry in known.items():
        if name in arguments:
            value = arguments[name]
        elif entry["required"]:
            raise ValueError("missing required param %r" % name)
        elif "default" in entry:
            value = entry["default"]
        else:
            continue
        expected = PARAM_TYPES[entry["type"]]
        if isinstance(value, bool) and entry["type"] != "boolean":
            raise ValueError("param %r must be of type %s" % (name, entry["type"]))
        if not isinstance(value, expected):
            raise ValueError("param %r must be of type %s" % (name, entry["type"]))
        resolved[name] = value
    return resolved
