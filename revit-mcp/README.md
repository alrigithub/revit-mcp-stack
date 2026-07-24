# revit-mcp

Pure-Python 3.12 stdio MCP server. It discovers Bridge ON Revit processes under `%LOCALAPPDATA%\RevitMcp\instances`, rejects PID-reuse records, and forwards versioned requests through a byte-mode Windows named pipe.

Agent-facing tool descriptions teach clients to batch related work into one dynamic script, use the IronPython 2.7 dialect and JSON-safe `_result`, handle busy/modal Revit without blindly repeating mutations, and choose explicit transaction modes.

The transport is a small `ctypes` wrapper over `WaitNamedPipeW`, `CreateFileW`, `ReadFile`, `WriteFile`, and `CancelIoEx`. All pipe I/O happens on one dedicated thread. There is no HTTP fallback, `pywin32`, Node runtime, network listener, telemetry, or cloud service.

## Development test

```powershell
./scripts/test.ps1
```

## Reproducible package

`requirements.lock` contains exact Windows x64 CPython 3.12 wheel hashes. MCP SDK 1.10.1 is intentionally pinned because 1.11.0 and later introduce a `pywin32` dependency on Windows, conflicting with this system's dependency contract.

```powershell
./scripts/package.ps1
```

The package script creates a local frozen environment, installs only hash-verified wheels, installs this adapter without dependency resolution, runs tests, and writes client configuration plus SHA-256 inventory. Nothing is installed globally.

## Dynamic code contracts

`run_python` source runs as IronPython 2.7 statements with `uiapp`, `doc`, `uidoc`, and `request` in scope. Set `_result` to JSON-safe primitives/containers.

`run_csharp` source is the body of `string EntryPoint.Run(UIApplication uiapp, Document doc, UIDocument? uidoc, string requestJson)`. It must return a JSON string. Diagnostics map back to `agent.cs`.

Every dynamic request requires `transaction_mode`: `read`, `auto`, `manual`, or `group`. Every Revit operation binds an explicit document session and generation.

## Saved tools

Proven scripts can be promoted to reusable named tools without rebuilding or reinstalling anything. Each saved tool is a pair of files in `%LOCALAPPDATA%\RevitMcp\tools`: `<name>.json` (manifest v1: name, description, engine `python`/`csharp`, pinned `transaction_mode`, `timeout_ms`, params schema) plus `<name>.py` or `<name>.cs` (the script; params arrive as its `request` object). `list_saved_tools` reads the registry per call and reports invalid manifests with reasons; `run_saved_tool` validates params against the manifest and executes through the normal `run_python`/`run_csharp` path. New files are live immediately — no restart, no reconnect.
