import asyncio
import base64
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from mcp.server import MCPServer
from mcp.types import CallToolResult, ImageContent, TextContent

from . import saved_tools
from .client import BridgeClient
from .discovery import pyrevit_install
from .runtime_settings import load_settings

AGENT_INSTRUCTIONS = """Local-only Revit bridge. Select a PID and explicit document session/generation before Revit work.
Batch related model work into ONE run_python or run_csharp script instead of many small calls: each call waits for Revit's single UI-thread ExternalEvent and may be delayed while Revit is busy or modal. Use execute_batch when separate steps specifically need atomic grouping and structured per-step results.
Python source is IronPython 2.7: no f-strings or Python 3-only syntax; use % or .format(), return JSON-safe data through _result, and use the available uiapp, doc, uidoc, request, Revit API, and .NET interop objects.
If Revit is busy/modal and a request remains queued or reports revit_busy, wait until Revit is ready. Retry reads safely; for mutations, first resolve get_request_status and reuse the same request_id/idempotency_key rather than blindly creating a second mutation.
Transport errors return state unknown: they do not prove rollback. Manual scripts and non-atomic batches can leave committed changes after an error; inspect the model and ledger before recovery. Ledger history is bounded and does not survive a Revit restart, so request_not_found is not proof that a mutation never ran.
transaction_mode is required for dynamic code: read opens no transaction; auto wraps one bridge-owned transaction; manual makes the script own and close every transaction; group wraps one bridge-owned transaction inside an assimilated group for one undo item.
Verification is proportionate: use a compact read or selected PNG sheet when it answers a real uncertainty. Optional assertions and capture_model are never required after every mutation. Progress/cancellation hooks are cooperative and cannot interrupt a native API call.
Saved tools are proven scripts promoted to reusable named tools on disk: call list_saved_tools before creating files so you use its configured root; subfolders are groups. Run enabled tools with run_saved_tool. New files and enable/disable markers are live immediately without restart."""

client = BridgeClient()


def _environment_note() -> str:
    lines: list[str] = []
    try:
        instances = client.instances()
        if instances:
            lines.append("Environment at server start: " + "; ".join(
                "Revit %s PID %s bridge %s" % (item["revit_year"], item["pid"], item["bridge_state"]) for item in instances)
                + ". Re-check live state with list_revit_instances.")
        else:
            lines.append("Environment at server start: no live Revit bridge instance"
                         " (start Revit and click Bridge ON, then re-check with list_revit_instances).")
    except Exception:
        lines.append("Environment at server start: instance discovery unavailable.")
    try:
        pyrevit = pyrevit_install()
        if pyrevit["version"]:
            lines.append("pyRevit %s is installed; run_python becomes available after Python ON"
                         " (confirm with get_capabilities)." % pyrevit["version"])
        elif pyrevit["installed"]:
            lines.append("pyRevit is installed but its version was not detected.")
        else:
            lines.append("pyRevit was not detected on this machine; run_python needs the pyRevit companion extension.")
    except Exception:
        pass
    return "\n".join(lines)


class ConfigurableMCPServer(MCPServer):
    async def list_all_tools(self):
        return await super().list_tools()

    async def list_tools(self):
        disabled = load_settings().disabled_mcp_tools
        return [tool for tool in await self.list_all_tools() if tool.name not in disabled]

    async def call_tool(self, name: str, arguments: dict[str, Any], context=None):
        # v2 added the third `context` parameter; it must be accepted and forwarded
        if name in load_settings().disabled_mcp_tools:
            raise PermissionError("MCP tool %r is disabled in Revit MCP settings" % name)
        return await super().call_tool(name, arguments, context)


mcp = ConfigurableMCPServer("revit-mcp-local", instructions=AGENT_INSTRUCTIONS + "\n" + _environment_note())


def _call(pid: int, tool: str, arguments: dict[str, Any] | None = None, document_session: str | None = None,
          document_generation: int | None = None, transaction_mode: str | None = None, timeout_ms: int = 30_000,
          request_id: str | None = None, idempotency_key: str | None = None) -> dict[str, Any]:
    return client.call(pid, tool, arguments, document_session=document_session, document_generation=document_generation,
                       transaction_mode=transaction_mode, timeout_ms=timeout_ms, request_id=request_id, idempotency_key=idempotency_key)


@mcp.tool()
def list_revit_instances() -> list[dict[str, Any]]:
    """List live Bridge ON Revit processes after rejecting stale PID-reuse records."""
    return client.instances()


@mcp.tool()
def list_documents(pid: int, timeout_ms: int = 30_000) -> dict[str, Any]:
    """List process/document-session/generation records for a selected Revit process."""
    return _call(pid, "list_documents", timeout_ms=timeout_ms)


@mcp.tool()
def get_capabilities(pid: int) -> dict[str, Any]:
    """Read bridge, Roslyn, Python, security, and projection capabilities without entering the Revit queue."""
    return _call(pid, "get_capabilities")


@mcp.tool()
def get_request_status(pid: int, request_id: str, after_revision: int | None = None, wait_ms: int = 0,
                       request_cancel: bool = False) -> dict[str, Any]:
    """Resolve disposition and timing without the UI queue. Optional long poll (max 30s) waits for a revision change. request_cancel cancels queued work or requests cooperative cancellation; running scripts must call check_cancelled/ReportProgress. Never assume a cancellation request means rollback."""
    args: dict[str, Any] = {"request_id": request_id, "wait_ms": max(0, min(wait_ms, 30_000)), "request_cancel": request_cancel}
    if after_revision is not None:
        args["after_revision"] = after_revision
    return _call(pid, "get_request_status", args, timeout_ms=max(30_000, min(wait_ms, 30_000) + 5000))


@mcp.tool()
def get_active_context(pid: int, timeout_ms: int = 30_000) -> dict[str, Any]:
    """Return the active Revit document/view context. `edit_mode` reports Revit's active edit mode: "None" means no edit mode is open; any other value (family/sketch/group editing) means mutations will misbehave until the user exits it; null means this Revit build (pre-2025.3) cannot report it."""
    return _call(pid, "get_active_context", timeout_ms=timeout_ms)


@mcp.tool()
def run_python(pid: int, document_session: str, document_generation: int, source: str, transaction_mode: str,
               request: dict[str, Any] | None = None, label: str | None = None, timeout_ms: int = 30_000,
               request_id: str | None = None, idempotency_key: str | None = None) -> dict[str, Any]:
    """Run ONE batched IronPython 2.7 script on Revit's UI thread. Prefer one script containing all related operations over many calls because each call pays an unpredictable Revit UI wait. No f-strings/Python 3-only syntax; use % or .format(), set JSON-safe `_result`, and use `uiapp`, `doc`, `uidoc`, `request`, Revit API, and .NET interop. transaction_mode: read=no transaction, auto=one bridge transaction, manual=script owns/closes transactions, group=one assimilated undo group. Always pass `label`: a short human-readable phrase describing the script's intent (e.g. "place 12 frame beams"); it is shown in the Revit Activity pane. If queued/revit_busy, wait; check status and reuse IDs before retrying mutations."""
    arguments: dict[str, Any] = {"source": source, "request": request or {}}
    if label:
        arguments["label"] = label[:120]
    return _call(pid, "run_python", arguments, document_session, document_generation,
                 transaction_mode, timeout_ms, request_id, idempotency_key)


@mcp.tool()
def run_csharp(pid: int, document_session: str, document_generation: int, source: str, transaction_mode: str,
               request: dict[str, Any] | None = None, label: str | None = None, timeout_ms: int = 30_000,
               request_id: str | None = None, idempotency_key: str | None = None) -> dict[str, Any]:
    """Compile and run ONE C# entry body containing all related operations; avoid many small calls because each pays an unpredictable Revit UI wait. The body runs with Revit/.NET objects on the UI thread and must return bounded JSON. transaction_mode: read=no transaction, auto=one bridge transaction, manual=code owns/closes transactions, group=one assimilated undo group. Always pass `label`: a short human-readable phrase describing the code's intent (e.g. "tag all doors on level 1"); it is shown in the Revit Activity pane. If queued/revit_busy, wait; check status and reuse IDs before retrying mutations."""
    arguments: dict[str, Any] = {"source": source, "request": request or {}}
    if label:
        arguments["label"] = label[:120]
    return _call(pid, "run_csharp", arguments, document_session, document_generation,
                 transaction_mode, timeout_ms, request_id, idempotency_key)


@mcp.tool()
def execute_batch(pid: int, document_session: str, document_generation: int, steps: list[dict[str, Any]],
                  atomic: bool = True, timeout_ms: int = 60_000, request_id: str | None = None,
                  idempotency_key: str | None = None) -> dict[str, Any]:
    """Execute genuinely separate steps in one request. Atomic mode uses one transaction group/undo item; non-atomic mode requires each step's transaction_mode. Prefer a single run_python/run_csharp script when the work can naturally be expressed together, avoiding repeated Revit UI waits."""
    return _call(pid, "execute_batch", {"steps": steps, "atomic": atomic}, document_session, document_generation,
                 "group" if atomic else "manual", timeout_ms, request_id, idempotency_key)


@mcp.tool()
def execute_and_verify(pid: int, document_session: str, document_generation: int, action: dict[str, Any],
                       element_ids: list[int], transaction_mode: str, preflights: list[str] | None = None,
                       checks: list[dict[str, Any]] | None = None, result_ids_key: str = "element_ids",
                       rollback_on_failure: bool = False, fields: list[str] | None = None,
                       timeout_ms: int = 60_000, request_id: str | None = None,
                       idempotency_key: str | None = None) -> dict[str, Any]:
    """Execute once, regenerate, then optionally check exists/bounds/count/parameter assertions. Also inspect IDs from the result_ids_key array in the script result. checks=[] runs no assertions. rollback_on_failure requires auto/group. Parameter checks use stable parameter_id and expected raw value (internal units), with optional tolerance. Commit-time warnings live in receipt.details.notices. Legacy preflights must be empty."""
    return _call(pid, "execute_and_verify", {"action": action, "element_ids": element_ids, "preflights": preflights or [],
                 "checks": checks or [], "result_ids_key": result_ids_key, "rollback_on_failure": rollback_on_failure,
                 **({"fields": fields} if fields is not None else {})},
                 document_session, document_generation, transaction_mode, timeout_ms, request_id, idempotency_key)


@mcp.tool()
def query_elements(pid: int, document_session: str, document_generation: int, category_id: int | None = None,
                   limit: int = 100, timeout_ms: int = 30_000, include_types: bool = False,
                   after_id: int | None = None, name_contains: str | None = None, type_id: int | None = None,
                   level_id: int | None = None, fields: list[str] | None = None) -> dict[str, Any]:
    """Query by category, name, type, or level with a stable element-ID cursor. Defaults to compact identity/relationships. include_types includes type elements. fields can request identity, instance_parameters, type_parameters, bounding_boxes, geometry, relationships, worksharing, phase, design_option, materials. Bounds and raw values use Revit internal units."""
    args: dict[str, Any] = {"limit": limit, "include_types": include_types}
    args.update({key: value for key, value in {"after_id": after_id, "name_contains": name_contains,
                "type_id": type_id, "level_id": level_id, "fields": fields}.items() if value is not None})
    if category_id is not None:
        args["category_id"] = category_id
    return _call(pid, "query_elements", args, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def get_elements(pid: int, document_session: str, document_generation: int, element_ids: list[int], timeout_ms: int = 30_000,
                 fields: list[str] | None = None) -> dict[str, Any]:
    """Get bounded identity, parameters, boxes, geometry summary, relationships, and worksharing data."""
    return _call(pid, "get_elements", {"element_ids": element_ids, **({"fields": fields} if fields is not None else {})}, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def get_parameters(pid: int, document_session: str, document_generation: int, element_ids: list[int], timeout_ms: int = 30_000,
                   parameter_ids: list[int] | None = None) -> dict[str, Any]:
    """Get resolved instance parameters with storage type, units, raw values, and display values."""
    return _call(pid, "get_parameters", {"element_ids": element_ids, **({"parameter_ids": parameter_ids} if parameter_ids is not None else {})}, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def get_warnings(pid: int, document_session: str, document_generation: int, timeout_ms: int = 30_000) -> dict[str, Any]:
    """Get bounded warning DTOs with definition ID, severity, description, and involved IDs."""
    return _call(pid, "get_warnings", {}, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def select_elements(pid: int, document_session: str, document_generation: int, element_ids: list[int], timeout_ms: int = 30_000) -> dict[str, Any]:
    """Select elements; the bound document must be active."""
    return _call(pid, "select_elements", {"element_ids": element_ids}, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def zoom_to_elements(pid: int, document_session: str, document_generation: int, element_ids: list[int], timeout_ms: int = 30_000) -> dict[str, Any]:
    """Zoom to elements; the bound document must be active."""
    return _call(pid, "zoom_to_elements", {"element_ids": element_ids}, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def open_view(pid: int, document_session: str, document_generation: int, view_id: int, timeout_ms: int = 30_000) -> dict[str, Any]:
    """Request a view change in the active bound document."""
    return _call(pid, "open_view", {"view_id": view_id}, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def export_view(pid: int, document_session: str, document_generation: int, view_id: int, output_directory: str,
                file_name: str, timeout_ms: int = 60_000, format: str = "pdf",
                view_ids: list[int] | None = None, pixel_size: int = 1600) -> dict[str, Any]:
    """Export one or up to 24 view_ids to PNG or combined PDF. Returns actual created paths in a unique subfolder. No transaction; file effects are not Revit-undoable."""
    return _call(pid, "export_view", {"view_id": view_id, "view_ids": view_ids or [], "output_directory": output_directory, "file_name": file_name,
                                       "format": format, "pixel_size": pixel_size},
                 document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def capture_model(pid: int, document_session: str, document_generation: int, preset: str = "building",
                  element_ids: list[int] | None = None, level_ids: list[int] | None = None,
                  output_directory: str | None = None, margin_m: float = 2.0, pixel_size: int = 1600,
                  include_isolated: bool = True, cut_fraction: float = 0.5, axis: str = "long",
                  inline_images: bool = True,
                  timeout_ms: int = 120_000, request_id: str | None = None,
                  idempotency_key: str | None = None) -> CallToolResult:
    """Create reusable Revit inspection views and labelled PNG contact sheets, preserving the working view. Presets: building (NSEW orthographic elevations + four AXOs), elevations, axo, floors (level/host/intersection), element (section box with context, isolated components, horizontal/longitudinal middle cuts), horizontal, vertical. Explicit element_ids define framing; otherwise physical geometry and grids suggest a building. vertical axis is long/short. cut_fraction locates the cut. Runs without Python. Returns actual PNG paths and view IDs; inspect selected sheets once when visual evidence is useful. Helper views are model changes; files are not undoable."""
    args: dict[str, Any] = {"preset": preset, "element_ids": element_ids or [], "level_ids": level_ids or [],
                            "margin_m": margin_m, "pixel_size": pixel_size, "include_isolated": include_isolated,
                            "cut_fraction": cut_fraction, "axis": axis}
    if output_directory is not None:
        args["output_directory"] = output_directory
    response = _call(pid, "capture_model", args, document_session, document_generation, "manual",
                     timeout_ms, request_id, idempotency_key)
    content: list[Any] = [TextContent(type="text", text=json.dumps(response, ensure_ascii=False))]
    if inline_images and response.get("state") == "succeeded":
        for path in (response.get("result") or {}).get("contact_sheets", [])[:2]:
            image_path = Path(path)
            if image_path.suffix.lower() == ".png" and image_path.is_file() and image_path.stat().st_size <= 4 * 1024 * 1024:
                content.append(ImageContent(type="image", data=base64.b64encode(image_path.read_bytes()).decode("ascii"), mime_type="image/png"))
    return CallToolResult(content=content, structured_content=response, is_error=response.get("state") == "failed")


@mcp.tool()
def reload_python_provider(pid: int, timeout_ms: int = 30_000) -> dict[str, Any]:
    """Quiesce Python admission, cancel queued old-generation work, self-test, and register a new generation."""
    return _call(pid, "reload_python_provider", timeout_ms=timeout_ms)


@mcp.tool()
def reload_tool_provider(pid: int, timeout_ms: int = 30_000) -> dict[str, Any]:
    """Reload the isolated versioned Roslyn provider."""
    return _call(pid, "reload_tool_provider", timeout_ms=timeout_ms)


@mcp.tool()
def list_saved_tools(name: str | None = None) -> dict[str, Any]:
    """List saved tools and the configured registry roots (`roots` is ordered; on duplicate tool IDs the first root wins and later copies appear under `shadowed`). Folder paths are group names. Disabled tools remain visible but cannot run. Without `name`, returns every tool plus invalid manifests; with a tool ID such as `qa/list_levels`, returns full detail. Read this before creating saved-tool files: new files go in the primary root (`root`), the first entry of `roots`."""
    if name is None:
        return saved_tools.list_saved_tools()
    return saved_tools.describe_saved_tool(saved_tools.load_saved_tool(name, allow_disabled=True))


@mcp.tool()
def run_saved_tool(pid: int, document_session: str, document_generation: int, name: str,
                   params: dict[str, Any] | None = None, timeout_ms: int | None = None,
                   request_id: str | None = None, idempotency_key: str | None = None) -> dict[str, Any]:
    """Run an enabled saved tool by ID with params validated against its manifest; folder groups use IDs such as `qa/list_levels`. Discover IDs and schemas via list_saved_tools. The manifest pins the engine and transaction_mode, and params reach the script as its `request` object."""
    tool = saved_tools.load_saved_tool(name)
    arguments = saved_tools.validate_arguments(tool, params or {})
    bridge_tool = "run_python" if tool.engine == "python" else "run_csharp"
    return _call(pid, bridge_tool, {"source": tool.source, "request": arguments, "label": name[:120]}, document_session,
                 document_generation, tool.transaction_mode, timeout_ms or tool.timeout_ms, request_id, idempotency_key)


@mcp.tool()
def get_logs_tail(pid: int, count: int = 100) -> dict[str, Any]:
    """Read bounded operational metadata logs; source, model data, environment, and results are excluded."""
    return _call(pid, "get_logs_tail", {"count": count})


def write_tools_manifest(root: Path | None = None) -> Path:
    """Publish the exact tool list and descriptions the LLM receives, for the Revit Activity pane."""
    root = root or Path(os.environ["LOCALAPPDATA"]) / "RevitMcp"
    root.mkdir(parents=True, exist_ok=True)
    tools = asyncio.run(mcp.list_all_tools())
    payload = {
        "written_utc": datetime.now(timezone.utc).isoformat(),
        "server": "revit-mcp-local",
        "tools": [{"name": tool.name, "description": tool.description or "",
                   "params": list((tool.input_schema or {}).get("properties", {}))} for tool in tools],
    }
    target = root / "mcp-tools.json"
    staging = root / "mcp-tools.json.tmp"
    staging.write_text(json.dumps(payload, indent=1), encoding="utf-8")
    os.replace(staging, target)
    return target


def main() -> None:
    try:
        write_tools_manifest()
    except Exception:
        pass
    try:
        mcp.run(transport="stdio")
    finally:
        client.close()


if __name__ == "__main__":
    main()
