import asyncio
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from mcp.server.fastmcp import FastMCP

from . import saved_tools
from .client import BridgeClient
from .discovery import pyrevit_install
from .runtime_settings import load_settings

AGENT_INSTRUCTIONS = """Local-only Revit bridge. Select a PID and explicit document session/generation before Revit work.
Batch related model work into ONE run_python or run_csharp script instead of many small calls: each call waits for Revit's single UI-thread ExternalEvent and may be delayed while Revit is busy or modal. Use execute_batch when separate steps specifically need atomic grouping and structured per-step results.
Python source is IronPython 2.7: no f-strings or Python 3-only syntax; use % or .format(), return JSON-safe data through _result, and use the available uiapp, doc, uidoc, request, Revit API, and .NET interop objects.
If Revit is busy/modal and a request remains queued or reports revit_busy, wait until Revit is ready. Retry reads safely; for mutations, first resolve get_request_status and reuse the same request_id/idempotency_key rather than blindly creating a second mutation.
transaction_mode is required for dynamic code: read opens no transaction; auto wraps one bridge-owned transaction; manual makes the script own and close every transaction; group wraps one bridge-owned transaction inside an assimilated group for one undo item.
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


class ConfigurableFastMCP(FastMCP):
    async def list_all_tools(self):
        return await super().list_tools()

    async def list_tools(self):
        disabled = load_settings().disabled_mcp_tools
        return [tool for tool in await self.list_all_tools() if tool.name not in disabled]

    async def call_tool(self, name: str, arguments: dict[str, Any]):
        if name in load_settings().disabled_mcp_tools:
            raise PermissionError("MCP tool %r is disabled in Revit MCP settings" % name)
        return await super().call_tool(name, arguments)


mcp = ConfigurableFastMCP("revit-mcp-local", instructions=AGENT_INSTRUCTIONS + "\n" + _environment_note())


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
def get_request_status(pid: int, request_id: str) -> dict[str, Any]:
    """Resolve the real final disposition of a previously admitted request."""
    return _call(pid, "get_request_status", {"request_id": request_id})


@mcp.tool()
def get_active_context(pid: int, timeout_ms: int = 30_000) -> dict[str, Any]:
    """Return the active Revit document/view context."""
    return _call(pid, "get_active_context", timeout_ms=timeout_ms)


@mcp.tool()
def run_python(pid: int, document_session: str, document_generation: int, source: str, transaction_mode: str,
               request: dict[str, Any] | None = None, timeout_ms: int = 30_000, request_id: str | None = None,
               idempotency_key: str | None = None) -> dict[str, Any]:
    """Run ONE batched IronPython 2.7 script on Revit's UI thread. Prefer one script containing all related operations over many calls because each call pays an unpredictable Revit UI wait. No f-strings/Python 3-only syntax; use % or .format(), set JSON-safe `_result`, and use `uiapp`, `doc`, `uidoc`, `request`, Revit API, and .NET interop. transaction_mode: read=no transaction, auto=one bridge transaction, manual=script owns/closes transactions, group=one assimilated undo group. If queued/revit_busy, wait; check status and reuse IDs before retrying mutations."""
    return _call(pid, "run_python", {"source": source, "request": request or {}}, document_session, document_generation,
                 transaction_mode, timeout_ms, request_id, idempotency_key)


@mcp.tool()
def run_csharp(pid: int, document_session: str, document_generation: int, source: str, transaction_mode: str,
               request: dict[str, Any] | None = None, timeout_ms: int = 30_000, request_id: str | None = None,
               idempotency_key: str | None = None) -> dict[str, Any]:
    """Compile and run ONE C# entry body containing all related operations; avoid many small calls because each pays an unpredictable Revit UI wait. The body runs with Revit/.NET objects on the UI thread and must return bounded JSON. transaction_mode: read=no transaction, auto=one bridge transaction, manual=code owns/closes transactions, group=one assimilated undo group. If queued/revit_busy, wait; check status and reuse IDs before retrying mutations."""
    return _call(pid, "run_csharp", {"source": source, "request": request or {}}, document_session, document_generation,
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
                       timeout_ms: int = 60_000, request_id: str | None = None,
                       idempotency_key: str | None = None) -> dict[str, Any]:
    """Execute an action and return bounded element projections plus warning delta."""
    return _call(pid, "execute_and_verify", {"action": action, "element_ids": element_ids, "preflights": preflights or []},
                 document_session, document_generation, transaction_mode, timeout_ms, request_id, idempotency_key)


@mcp.tool()
def query_elements(pid: int, document_session: str, document_generation: int, category_id: int | None = None,
                   limit: int = 100, timeout_ms: int = 30_000) -> dict[str, Any]:
    """Return bounded RevitLookup-inspired projections for a category or the document."""
    args: dict[str, Any] = {"limit": limit}
    if category_id is not None:
        args["category_id"] = category_id
    return _call(pid, "query_elements", args, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def get_elements(pid: int, document_session: str, document_generation: int, element_ids: list[int], timeout_ms: int = 30_000) -> dict[str, Any]:
    """Get bounded identity, parameters, boxes, geometry summary, relationships, and worksharing data."""
    return _call(pid, "get_elements", {"element_ids": element_ids}, document_session, document_generation, "read", timeout_ms)


@mcp.tool()
def get_parameters(pid: int, document_session: str, document_generation: int, element_ids: list[int], timeout_ms: int = 30_000) -> dict[str, Any]:
    """Get resolved instance parameters with storage type, units, raw values, and display values."""
    return _call(pid, "get_parameters", {"element_ids": element_ids}, document_session, document_generation, "read", timeout_ms)


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
                file_name: str, timeout_ms: int = 60_000) -> dict[str, Any]:
    """Export one view to PDF after model commit; file effects are not Revit-undoable."""
    return _call(pid, "export_view", {"view_id": view_id, "output_directory": output_directory, "file_name": file_name},
                 document_session, document_generation, "read", timeout_ms)


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
    """List saved tools and the configured registry root. Folder paths are group names. Disabled tools remain visible but cannot run. Without `name`, returns every tool plus invalid manifests; with a tool ID such as `qa/list_levels`, returns full detail. Read this before creating saved-tool files so they go to the configured root."""
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
    return _call(pid, bridge_tool, {"source": tool.source, "request": arguments}, document_session,
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
                   "params": list((tool.inputSchema or {}).get("properties", {}))} for tool in tools],
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
