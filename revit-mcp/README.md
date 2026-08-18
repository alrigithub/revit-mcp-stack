# revit-mcp

Pure-Python 3.12 stdio MCP server — the client-side half of the stack. MCP tool calls land in `server.py`, are framed as versioned JSON by `client.py`/`winpipe.py`, and travel over a byte-mode Windows named pipe to the C# bridge inside Revit. Discovery lists Bridge ON records under `%LOCALAPPDATA%\RevitMcp\instances` and rejects PID-reuse.

The transport is a small `ctypes` wrapper over `WaitNamedPipeW`, `CreateFileW`, `ReadFile`, `WriteFile`, and `CancelIoEx`, with all pipe I/O on one dedicated thread. No `pywin32`, HTTP fallback, Node runtime, network listener, telemetry, or cloud service.

## Test

```powershell
./scripts/test.ps1
```

That uses `python` from PATH. To test against the deployed environment, use the bundled runtime: `%LOCALAPPDATA%\RevitMcp\mcp\0.9.0\runtime\Scripts\python.exe` with `PYTHONPATH=src`.

## Deploy and cost of a change

The deployed copy at `%LOCALAPPDATA%\RevitMcp\mcp\0.9.0\revit_mcp\` is not rebuilt automatically. After editing `src/revit_mcp`, run `../sync.ps1` from the repo root; changes load when the MCP client restarts its stdio server. The package version stays 0.9.0 because that path is baked into the MCP client registration.

## Reproducible package

`requirements.lock` contains exact Windows x64 CPython 3.12 wheel hashes. The MCP SDK is intentionally pinned to `mcp==1.10.1`: 1.11.0+ adds a `pywin32` dependency on Windows, which conflicts with this system's dependency contract. Do not upgrade casually.

```powershell
./scripts/package.ps1
```

The package script creates a local frozen environment, installs only hash-verified wheels, installs this adapter without dependency resolution, runs the tests plus a stdio smoke test, and writes `client-config.json` plus a SHA-256 inventory. Nothing is installed globally.

## Dynamic code contracts

`run_python` source runs as IronPython 2.7 statements with `uiapp`, `doc`, `uidoc`, and `request` in scope — no f-strings, use `%`/`.format()`, set `_result` to JSON-safe primitives/containers, and wrap `ElementId.Value` in `int()` (the serializer rejects .NET `Int64`).

`run_csharp` source is the body of `string EntryPoint.Run(UIApplication uiapp, Document doc, UIDocument? uidoc, string requestJson)`. It must return a JSON string. Diagnostics map back to `agent.cs`.

Every dynamic request requires `transaction_mode`: `read`, `auto`, `manual`, or `group`. Every Revit operation binds an explicit document session and generation.

## Saved tools

Proven scripts can be promoted to reusable named tools without rebuilding anything. The root defaults to `%LOCALAPPDATA%\RevitMcp\tools` (changeable from the Activity pane). Each tool is a `<name>.json` manifest plus `<name>.py` or `<name>.cs`; subfolders are groups, a group `.disabled` marker disables everything below it, and `<name>.disabled` disables one tool. `list_saved_tools` reports the configured root and enabled state; `run_saved_tool` validates params and executes through the normal `run_python`/`run_csharp` path. Changes on disk are live at the next call. The repo's `saved-tools/` folder mirrors into the root via `../sync.ps1 -Tools`.
