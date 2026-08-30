"""PROTOTYPE — THROWAWAY. Not production code.

Drives probe_server.py over a real stdio pipe with the mcp 2.1.1 client and
prints the full state after every step. Answers, in order:
  A. server-side list_tools/call_tool override signatures work
  B. real stdio initialize handshake succeeds (negotiated protocol printed)
  C. disabled tool is hidden from list_tools
  D. tool.input_schema read (mcp-tools.json pattern) works
  E. dict/list returning tools round-trip; structured content shape printed
  F. disabled-tool call -> clean error result, server survives (next call ok)
"""
import asyncio
import json
import subprocess
import sys

from mcp import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client

SERVER = __file__.replace("probe_client.py", "probe_server.py")
DISABLED = "blocked_tool"


def show(step: str, payload) -> None:
    print("\n=== %s ===" % step)
    print(json.dumps(payload, indent=1, default=str))


async def main() -> None:
    # D first: the manifest read runs in-process in the server module
    manifest = subprocess.run(
        [sys.executable, SERVER, "--manifest-check"],
        capture_output=True, text=True, env={"PROBE_DISABLED_TOOLS": DISABLED, "SYSTEMROOT": __import__("os").environ["SYSTEMROOT"]},
    )
    show("D. write_tools_manifest via tool.input_schema",
         json.loads(manifest.stdout) if manifest.returncode == 0 else {"FAILED": manifest.stderr[-2000:]})

    params = StdioServerParameters(command=sys.executable, args=[SERVER],
                                   env={"PROBE_DISABLED_TOOLS": DISABLED})
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            show("B. initialize handshake", {
                "protocol_version": init.protocol_version,
                "server": {"name": init.server_info.name, "version": init.server_info.version},
                "instructions": init.instructions,
            })

            tools = await session.list_tools()
            show("A+C. list_tools (override active, %r must be absent)" % DISABLED, {
                "names": [t.name for t in tools.tools],
                "disabled_hidden": DISABLED not in [t.name for t in tools.tools],
                "ping_input_schema_params": list((tools.tools[0].input_schema or {}).get("properties", {})),
            })

            r = await session.call_tool("ping", {"echo": "revit"})
            show("E1. call ping (dict return)", {
                "is_error": r.is_error,
                "structured_content": r.structured_content,
                "text_content": [c.text for c in r.content if c.type == "text"],
            })

            r = await session.call_tool("list_things", {"limit": 2})
            show("E2. call list_things (list return)", {
                "is_error": r.is_error,
                "structured_content": r.structured_content,
            })

            r = await session.call_tool("blocked_tool", {})
            show("F1. call disabled tool (expect clean is_error, not a crash)", {
                "is_error": r.is_error,
                "text_content": [c.text for c in r.content if c.type == "text"],
            })

            r = await session.call_tool("ping", {"echo": "still alive?"})
            show("F2. server survived the PermissionError", {
                "is_error": r.is_error,
                "structured_content": r.structured_content,
            })

    print("\nVERDICT: all steps above printed real state; read F1/F2 for the gate behavior.")


if __name__ == "__main__":
    asyncio.run(main())
