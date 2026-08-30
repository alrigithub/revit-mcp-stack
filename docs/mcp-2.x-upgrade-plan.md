# Upgrading revit-mcp from mcp 1.10.1 to 2.x — repo-specific plan

Companion to [mcp-2.x-migration.md](mcp-2.x-migration.md) (general SDK
changes). Written 2026-08-30 against mcp 2.1.1.

## Why upgrade

- The three high Dependabot alerts on `mcp` 1.10.1 (WebSocket Host/Origin
  validation, HTTP session principal, DNS rebinding) are all fixed or removed
  in 2.x. None are exploitable in our stdio-only deployment, but upgrading
  closes them properly instead of dismissing them.
- The 1.10.1 pin exists to avoid `pywin32`. That reason is now permanent —
  `pywin32>=311; sys_platform == "win32"` is an unconditional Windows dep of
  2.x with no opt-out — so the choice is "accept pywin32" or "freeze on the
  1.10 line forever". pywin32 is Win32 API bindings (same capability class as
  the ctypes calls we already make); it opens no listeners and doesn't change
  the local-only security posture. The real cost is packaging: more binary
  wheels to hash-lock, larger bundled runtime.

## Blast radius

The **only** file in the repo that imports the SDK is
`revit-mcp/src/revit_mcp/server.py` (verified by grep). The pipe client
(`client.py`/`winpipe.py`) is pure ctypes and untouched. The C# bridge,
pyRevit extension, and saved tools don't know the SDK exists.

## Exact code changes in `server.py`

### 1. Import + base class (lines 8, 52)

```python
# before
from mcp.server.fastmcp import FastMCP
class ConfigurableFastMCP(FastMCP): ...

# after
from mcp.server import MCPServer
class ConfigurableMCPServer(MCPServer): ...
```

Constructor call at line 66 already passes `instructions=` as a keyword and an
explicit name — compatible as-is.

### 2. The subclass overrides (lines 52–63) — verify signatures at upgrade time

`ConfigurableFastMCP` overrides `list_tools()` and `call_tool(name, arguments)`
to filter/deny tools disabled in settings.json. Both just gate and delegate to
`super()`. Action items:

- Check the installed v2 `MCPServer.call_tool` signature before porting — v2
  added keyword-only parameters in several APIs and the override must match
  `super()` exactly (accept `*args, **kwargs` and forward them, rather than
  hardcoding `(name, arguments)`).
- The `PermissionError` raised for disabled tools should be re-tested: confirm
  v2 converts server-side exceptions into an `is_error` tool result the same
  way 1.10 did (test: disable a tool in settings.json, call it, expect a clean
  error, not a crashed server).

### 3. snake_case field rename (line 250)

```python
# before
"params": list((tool.inputSchema or {}).get("properties", {}))
# after
"params": list((tool.input_schema or {}).get("properties", {}))
```

This is `write_tools_manifest()`, which produces `mcp-tools.json` for the
Activity pane. The JSON file's own shape is ours and doesn't change — only the
attribute read on the SDK Tool object.

### 4. Structured output — decide, don't drift

Every tool returns `dict[str, Any]` / `list[dict[str, Any]]`. In v2,
`structured_output=None` (default) auto-detects from the annotation, so our
tools will start emitting output schemas and `structuredContent` alongside the
text content. That's protocol-conformant and clients ignore what they don't
use, but it changes what goes over the wire. If anything odd shows up (e.g. a
client rendering both text and structured payloads), the escape hatch is
`@mcp.tool(structured_output=False)` per tool. Recommendation: leave
auto-detect on and smoke-test with Claude Code first.

### 5. Unchanged

- `mcp.run(transport="stdio")` (line 265) — identical in v2.
- `@mcp.tool()` decorators — all 22 use the no-arg form, compatible.
- `tool.name`, `tool.description` — already snake_case-safe.

## pyproject.toml

```toml
dependencies = ["mcp==2.1.1"]   # keep the exact pin; bump deliberately per release
```

`requires-python = ">=3.12,<3.13"` stays (SDK needs only >=3.10).

## New dependency surface to vet / hash-lock

Installing mcp 2.1.1 pulls (beyond what 1.10.1 had): `mcp-types`, `httpx2`,
`jsonschema`, `opentelemetry-api`, `pyjwt[crypto]` (+ `cryptography`),
`python-multipart`, `pywin32`, `sse-starlette`, newer `pydantic`/`starlette`/
`uvicorn`/`anyio`, `typing-inspection`. All hashes go into the existing lock;
the three sbom.json files update with the milestone commit per the versioning
policy. None of these open sockets on their own — the server still only ever
calls `run(transport="stdio")`, and the bridge pipe client remains pure ctypes.

## Rollout checklist

1. Branch; bump the pin in `revit-mcp/pyproject.toml`.
2. Install into the bundled runtime (`%LOCALAPPDATA%\RevitMcp\mcp\runtime`),
   regenerate the hash lock with the new wheel set.
3. Apply the `server.py` changes above (import, subclass name/signatures,
   `input_schema`).
4. `$env:PYTHONPATH="src"; & $py -m unittest discover -s tests` in `revit-mcp`
   (and the extension tests — they don't import mcp but are cheap).
5. `./sync.ps1`, then live smoke test: register the server with a client,
   confirm `initialize` succeeds, `mcp-tools.json` is written with params
   populated (catches the `input_schema` rename), `list_revit_instances`
   works, a disabled-tool call fails cleanly, and one full
   pid→documents→run_python round trip against a live bridge.
6. Update CLAUDE.md: replace the "pinned to 1.10.1, don't upgrade" note with
   the new policy (exact-pin on 2.x, pywin32 accepted, bump deliberately).
7. Milestone commit with version bump; Dependabot alerts on `mcp` close on the
   next scan of the pushed `pyproject.toml`. Then remove the `mcp` ignore rule
   from `.github/dependabot.yml` — with the pin at 2.1.1 we *want* update PRs
   again (the setuptools alert's fix, if any, arrives the same way).

## Open questions — ANSWERED by the probe prototype (2026-08-30)

A throwaway probe (branch `prototype/mcp2-probe`, `revit-mcp/prototype-mcp2/`,
one command: `./run.ps1`) ran the whole pattern against real mcp 2.1.1 over
real stdio. Results:

- **`MCPServer.call_tool` signature**:
  `call_tool(self, name: str, arguments: dict[str, Any], context: Context | None = None)`
  — v2 added a third `context` parameter; the override must accept and forward
  it. `list_tools(self)` is unchanged. Verified working with the port shown in
  the probe.
- **Disabled-tool gate**: raising `PermissionError` from the override surfaces
  to the client as a clean `is_error: true` result carrying the message text;
  the server logs a traceback to stderr and keeps serving (next call
  succeeded). Same operator experience as v1.
- **`tool.input_schema`**: the `write_tools_manifest` pattern works; `params`
  lists populate correctly.
- **Structured output (auto-detect on)**: dict-returning tools emit
  `structuredContent` directly; list-returning tools are wrapped as
  `{"result": [...]}`. Text content is still present alongside — no client
  breakage observed; leave auto-detect on.
- **pywin32**: installs from wheels into a venv and `import win32api` works
  with **no post-install script**. (The copy-based bundled-runtime deployment
  still deserves one smoke test at rollout, but wheel-level evidence is good.)
- **Protocol**: initialize negotiated revision `2025-11-25` with instructions
  delivered intact.
