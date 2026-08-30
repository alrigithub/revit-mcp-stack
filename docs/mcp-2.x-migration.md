# MCP Python SDK v2 migration reference

Distilled from the official SDK docs (via context7) and PyPI, 2026-08-30.
Sources: https://py.sdk.modelcontextprotocol.io/v2/migration ·
https://py.sdk.modelcontextprotocol.io/v2/whats-new ·
https://pypi.org/pypi/mcp/json

This is the general SDK picture. The repo-specific plan is in
[mcp-2.x-upgrade-plan.md](mcp-2.x-upgrade-plan.md).

## Current state on PyPI

- Latest release: **mcp 2.1.1** (we are on 1.10.1 — a major-version jump, not
  the 1.11 bump the old pin note was written against).
- `requires-python >= 3.10` — our 3.12 runtime is fine.

### Full dependency list of mcp 2.1.1 (from PyPI metadata)

```
anyio>=4.9 (>=4.10 on py3.14)
httpx2>=2.5.0
jsonschema>=4.20.0
mcp-types==2.1.1          # wire types split into their own package
opentelemetry-api>=1.28.0
pydantic>=2.12.0
pyjwt[crypto]>=2.10.1
python-multipart>=0.0.9
pywin32>=311; sys_platform == "win32"   # the dep the 1.10.1 pin was avoiding
sse-starlette>=3.0.0
starlette>=0.27
typing-extensions>=4.13.0
typing-inspection>=0.4.1
uvicorn>=0.31.1
# extras: cli -> python-dotenv, typer; rich -> rich
```

`pywin32` is now an unconditional Windows dependency — there is no extra or
marker that avoids it. The HTTP-server stack (starlette, uvicorn,
sse-starlette, pyjwt) installs regardless of whether you ever open a network
transport; a stdio-only server simply never imports/listens with it.

## Breaking changes that exist in v2

### FastMCP renamed to MCPServer

```python
# v1
from mcp.server.fastmcp import FastMCP
mcp = FastMCP("Demo")

# v2
from mcp.server import MCPServer          # or: from mcp.server.mcpserver import MCPServer
mcp = MCPServer("Demo")
```

- Submodules under `mcp.server.fastmcp.*` moved to `mcp.server.mcpserver.*`
  with the same internal structure.
- `FastMCPError` → `MCPServerError`.
- Default server name changed from `"FastMCP"` to `"mcp-server"` (irrelevant
  if you pass a name explicitly).
- Constructor positional order changed — pass everything except the name as
  keyword arguments (`MCPServer("Demo", instructions=...)`).

### Wire types moved to `mcp-types`, all fields snake_case

- Type definitions live in the separate `mcp-types` package (pulled in
  automatically, version-locked to the SDK).
- **All camelCase attributes are now snake_case**:
  `result.isError` → `result.is_error`, `tools.nextCursor` → `tools.next_cursor`,
  `tool.inputSchema` → `tool.input_schema`.
- Extra fields on MCP types are no longer preserved; Resource URIs are plain
  strings rather than `AnyUrl`.

### Transport configuration moved from constructor to `run()`

```python
# v1: FastMCP("Demo", host=..., port=..., json_response=True)
# v2:
mcp = MCPServer("Demo")
mcp.run(transport="streamable-http", host="0.0.0.0", port=9000, json_response=True)
```

- The `Settings` object now only holds constructor-owned fields (debug,
  log_level, auth); host/port/etc. were removed from it.
- **`mcp.run(transport="stdio")` is unchanged** — stdio takes no parameters:

```python
run(transport: Literal["stdio", "sse", "streamable-http"] = "stdio", **kwargs) -> None
# stdio path: anyio.run(self.run_stdio_async)
```

### Transports

- WebSocket support **removed** (one of our three security alerts was
  specifically about the WebSocket transport — the vulnerable code no longer
  exists in v2).
- StreamableHTTP client components removed; non-2xx responses surface as
  JSON-RPC errors.
- stdio server streams are kept on private descriptors (likely why `pywin32`
  became a Windows dependency).

### Tool registration (`@mcp.tool()`)

Signature is compatible with v1 usage; new optional kwargs only:

```python
def tool(self, name=None, title=None, description=None, annotations=None,
         icons=None, meta=None, structured_output=None) -> Callable
```

- `structured_output=None` auto-detects from the return type annotation;
  `False` forces plain-text output. Functions returning `dict`/`list`
  annotations get an auto-generated output schema and `structuredContent` in
  results.
- Tools may declare a `Context` parameter for logging/progress (unchanged).
- New in v2: a `Resolve(...)` parameter mechanism and `InputRequiredResult`
  for multi-round tool input — opt-in, no effect on plain tools.

### Client-side `call_tool` (for reference)

`ClientSession.call_tool` returns a `CallToolResult` and gained keyword-only
options (`input_responses`, `request_state`, `allow_input_required`,
`allow_claimed`). Not directly relevant to a server, but the shape shows the
v2 result convention: `content` + `structured_content` + `is_error`.
