# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Local-only Revit MCP stack, three components in one repo:

- `revit-c-bridge/` — C# Revit add-in (Revit 2025/2026 on net8.0-windows, 2027 on net10.0-windows; only 2025 is live-certified). Named-pipe server, bounded request queue + ledger, transaction coordinator, Activity dockable pane, ribbon.
- `revit-mcp/` — pure-Python 3.12 stdio MCP server (`src/revit_mcp/`). ctypes named-pipe client, no pywin32/network. MCP SDK intentionally pinned to `mcp==1.10.1` (1.11+ drags in pywin32); don't upgrade casually.
- `revit-pyrevit-extention/` — pyRevit extension hosting the IronPython 2.7 execution provider. The tab folder MUST stay named `Revit MCP.tab` (with space) — that exact title merges it with the C# ribbon tab.

Security model: current-user-only pipe + per-process CSPRNG nonce, bounded frames, redacted errors, no listeners, hash-locked deps. Keep every change inside that posture.

## Architecture that spans files

- **Request path**: MCP tool → `server.py` → `client.py`/`winpipe.py` (framed JSON over pipe) → `PipeServer.cs` (nonce check, admission, ledger, queue) → `RevitRequestHandler.cs` on Revit's UI thread via ExternalEvent → transaction per `transaction_mode` (`read`/`auto`/`manual`/`group`) → provider.
- **Discovery**: bridge writes `%LOCALAPPDATA%\RevitMcp\instances\<pid>.json` on Bridge ON; the Python side lists/validates these (PID-reuse rejection). Both capabilities start OFF every Revit session — a human click (Bridge ON, Python ON) is required each session by design.
- **Dynamic code contract**: scripts receive the caller's `request` object only. The C# side unwraps it in `RevitRequestHandler.RequestPayload`; the Python provider unwraps in `_execute` (it needs the full envelope itself for compile-cache lookup). Don't reintroduce the envelope into script scope.
- **Providers**: IronPython 2.7 lives in pyRevit's persistent engine — module edits only take effect after the Python ON button (it `reload()`s the module; the engine caches imports). C# runs through the isolated Roslyn provider (`providers/roslyn/1/`, collectible AssemblyLoadContext); allowed compile references are the explicit list in `RoslynProviderHost.ReferenceManifest` — missing-assembly compile errors are fixed there.
- **Saved tools** (tool promotion without rebuilds): manifest+script pairs in `%LOCALAPPDATA%\RevitMcp\tools\` (`name.json` + `name.py`/`.cs`), validated by `revit_mcp/saved_tools.py`, read at call time by `list_saved_tools`/`run_saved_tool` — new files are live immediately, no reconnect. Params reach the script as its `request` object; `transaction_mode` is pinned in the manifest. Extra read-only search roots come from `saved_tools_paths` in settings.json (ordered, first root wins on ID collisions, disabled never falls through); the C# `LocalSettingsStore` must keep round-tripping that key or pane writes wipe it.
- **Pane data files**: MCP server writes `%LOCALAPPDATA%\RevitMcp\mcp-tools.json` at startup (tool list + LLM descriptions); the Activity pane's Tools/Saved views read these files, never the pipe.
- **IronPython 2.7 dialect** for all `run_python`/saved python tools: no f-strings, `%`/`.format()` only, JSON-safe `_result`, wrap `ElementId.Value` in `int()` (`_json_safe` rejects .NET `Int64`).

## saved-tools/

Repo folder of prebuilt saved tools (manifest+script pairs, subfolders are groups). Deploy with `./sync.ps1 -Tools` → `%LOCALAPPDATA%\RevitMcp\tools\`; live at the next call, no reconnect. Contract and conventions: `saved-tools/README.md`.

## Versioning

`revit-c-bridge/version.txt` is the single source of truth; `Directory.Build.props` stamps it into the DLL and the pane footer displays the *loaded* assembly's version. `package.ps1` ships the current number then auto-advances the file. The Python package stays at 0.9.0 because its deployed path (`...\mcp\0.9.0\`) is baked into the MCP registration.

## Commands

Bridge (PowerShell, from `revit-c-bridge/`):

```powershell
./scripts/build.ps1 -RevitYear 2025      # compile only
./scripts/test.ps1                        # 12 C# tests
./scripts/package.ps1 -RevitYear 2025    # build+test+stage artifacts/2025, bumps version.txt
./scripts/install.ps1 -RevitYear 2025    # Revit MUST be closed (DLL is locked while running)
```

Python (always use the bundled runtime, not system Python):

```powershell
cd revit-mcp
$py = "$env:LOCALAPPDATA\RevitMcp\mcp\0.9.0\runtime\Scripts\python.exe"
$env:PYTHONPATH = "src"; & $py -m unittest discover -s tests          # all tests
& $py -m unittest tests.test_saved_tools -v                           # single module
cd ../revit-pyrevit-extention; & $py -m unittest discover -s tests    # extension tests
```

Live smoke-testing against Revit: drive `revit_mcp.server` functions directly with `PYTHONPATH=%LOCALAPPDATA%\RevitMcp\mcp\0.9.0` (see the pattern: `list_revit_instances` → `list_documents` → call with pid/session/generation).

## Deployment mirrors (critical)

The repo is the source of truth, but the deployed copies must be kept in sync after edits — nothing rebuilds them automatically. `./sync.ps1` copies the first two (`-Tools` adds the third); `./doctor.ps1` reports drift and install health:

- `revit-mcp/src/revit_mcp/*` → `%LOCALAPPDATA%\RevitMcp\mcp\0.9.0\revit_mcp\` (plain source, copy files)
- `revit-pyrevit-extention/RevitMCP.extension/*` → `%APPDATA%\pyRevit\Extensions\RevitMCP.extension\` (then Python ON in Revit to reload the provider)
- `saved-tools/*` → `%LOCALAPPDATA%\RevitMcp\tools\` (only with `-Tools`; live at the next call)
- bridge: package → close Revit → install → reopen (both Revit-side capabilities come back OFF)

## Iteration loop with Revit

Bridge changes cost a Revit restart; design around it. Prefer, in order: saved tools (no restart at all) → Python provider edits (Python ON click) → Roslyn provider (reload_tool_provider) → bridge rebuild (restart). Batch bridge changes so one install carries several.

Git: private origin `alrigithub/revit-mcp-stack`. Commit at milestones together with the version bump so `version.txt` stays traceable to code.

## Roadmap

- LSP + Revit stubs
- Revit 2026/2027 live certification
