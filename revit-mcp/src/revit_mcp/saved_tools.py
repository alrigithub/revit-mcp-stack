from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

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
    name: str
    description: str
    engine: str
    transaction_mode: str
    timeout_ms: int
    params: tuple[dict[str, Any], ...]
    source: str


def registry_root() -> Path:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        raise RuntimeError("LOCALAPPDATA is unavailable")
    return Path(local) / "RevitMcp" / "tools"


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


def _load_manifest(manifest_path: Path) -> SavedTool:
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
    return SavedTool(name, description.strip(), engine, raw["transaction_mode"], timeout_ms, params, source)


def list_saved_tools(root: Path | None = None) -> dict[str, Any]:
    root = root or registry_root()
    tools: list[dict[str, Any]] = []
    invalid: list[dict[str, str]] = []
    manifests = sorted(root.glob("*.json")) if root.exists() else []
    for path in manifests[:MAX_LISTED_TOOLS]:
        try:
            tool = _load_manifest(path)
            tools.append({"name": tool.name, "description": tool.description, "engine": tool.engine})
        except (OSError, ValueError, json.JSONDecodeError) as ex:
            invalid.append({"file": path.name, "reason": str(ex)})
    result: dict[str, Any] = {"root": str(root), "tools": tools, "invalid": invalid}
    if len(manifests) > MAX_LISTED_TOOLS:
        result["truncated"] = True
    return result


def load_saved_tool(name: str, root: Path | None = None) -> SavedTool:
    if not NAME_PATTERN.match(name or ""):
        raise ValueError("tool name must match %s" % NAME_PATTERN.pattern)
    root = root or registry_root()
    manifest_path = root / ("%s.json" % name)
    if not manifest_path.is_file():
        raise LookupError("no saved tool named %r in %s" % (name, root))
    return _load_manifest(manifest_path)


def describe_saved_tool(tool: SavedTool) -> dict[str, Any]:
    return {"name": tool.name, "description": tool.description, "engine": tool.engine,
            "transaction_mode": tool.transaction_mode, "timeout_ms": tool.timeout_ms, "params": list(tool.params)}


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
