"""PROTOTYPE — THROWAWAY. Not production code.

Minimal port of revit_mcp/server.py's SDK-facing pattern onto mcp 2.1.1,
with the bridge replaced by stubs. Answers: do the documented v2 renames
(MCPServer, input_schema, call_tool signature) make our pattern work?

Run via probe_client.py / run.ps1 — this file is the stdio server side.
"""
import json
import os
import sys
from typing import Any

from mcp.server import MCPServer

# stands in for load_settings().disabled_mcp_tools (in-memory, no persistence)
DISABLED_TOOLS = set(filter(None, os.environ.get("PROBE_DISABLED_TOOLS", "").split(",")))


class ConfigurableMCPServer(MCPServer):
    """Port of ConfigurableFastMCP: settings-driven tool disable gate."""

    async def list_all_tools(self):
        return await super().list_tools()

    async def list_tools(self):
        return [tool for tool in await self.list_all_tools() if tool.name not in DISABLED_TOOLS]

    async def call_tool(self, name: str, arguments: dict[str, Any], context=None):
        # v2 grew a third `context` parameter — must be accepted and forwarded
        if name in DISABLED_TOOLS:
            raise PermissionError("MCP tool %r is disabled in Revit MCP settings" % name)
        return await super().call_tool(name, arguments, context)


mcp = ConfigurableMCPServer("probe-mcp2", instructions="Prototype server for the mcp 2.x upgrade probe.")


@mcp.tool()
def ping(echo: str = "pong") -> dict[str, Any]:
    """Returns a dict, like every real revit_mcp tool."""
    return {"ok": True, "echo": echo, "pid": os.getpid()}


@mcp.tool()
def list_things(limit: int = 3) -> list[dict[str, Any]]:
    """Returns a list of dicts, like list_revit_instances."""
    return [{"n": i} for i in range(limit)]


@mcp.tool()
def blocked_tool() -> dict[str, Any]:
    """Disabled via PROBE_DISABLED_TOOLS; must never actually run."""
    return {"should": "never happen"}


def write_tools_manifest_probe() -> dict[str, Any]:
    """Port of write_tools_manifest's SDK-facing read (inputSchema -> input_schema)."""
    import asyncio
    tools = asyncio.run(mcp.list_all_tools())
    return {
        "tools": [{"name": t.name, "description": t.description or "",
                   "params": list((t.input_schema or {}).get("properties", {}))} for t in tools],
    }


if __name__ == "__main__":
    if "--manifest-check" in sys.argv:
        print(json.dumps(write_tools_manifest_probe()))
    else:
        mcp.run(transport="stdio")
