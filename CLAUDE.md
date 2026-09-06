# Agent guidance

## Scope

Maintain a small generic Revit bridge. Keep company modeling logic in independently deployed saved tools. Prefer one coherent script and a compact result; verification and PNGs should resolve a real uncertainty, never become compulsory after every edit.

Three components:
- `revit-c-bridge`: Revit UI-thread dispatcher, bounded queue/ledger, transactions, receipts, native inspection/PNG service, ribbon and Activity pane.
- `revit-mcp`: Python 3.12 stdio MCP server and Windows pipe client. A separate control lane keeps status responsive during execution.
- `revit-pyrevit-extention`: persistent pyRevit IronPython 2.7 provider. The folder spelling is part of existing install paths.

Read `GOTCHAS.md` before live modeling. Public tool behavior is documented in `docs/tools.md`.

## Contracts to preserve

- Same-user pipe + process nonce; no HTTP fallback. Keep frame, source, result, queue and ledger bounds.
- Select PID and explicit document session/generation. UI operations require the active document. Never silently rebind stale identities.
- Bridge and Python start OFF each process. Execution policy defaults to saved scripts only. No new approval framework is needed for ordinary authorized work.
- Validate/materialize bridge-owned mutation results before commit. Report actual commit/rollback; manual code and non-atomic batches may leave changes.
- A timeout or unknown transport outcome is not rollback. Resolve the ledger and reuse operation IDs before mutation retries. Retention is bounded and resets on Revit restart.
- Capture warnings before suppression. Preserve diagnostic source lines without locals or full source dumps. Log bounded operational summaries.
- Settings round-trip unknown keys. Saved-tool roots are ordered; a disabled or invalid first owner never falls through to another root.
- Python scripts receive `uiapp`, `doc`, `uidoc`, `request`, `_result`, `report_progress`, `check_cancelled`. C# entry bodies receive `uiapp`, `doc`, `uidoc`, `requestJson` and return a JSON string. Provider implementation handles the outer envelope.
- Native capture code is shared between MCP and ribbon. Only reserved helper views may be replaced/reset; preserve the working view and selection.

## Development

Use the installed bundled runtime, not system Python. `runtime/python.exe` is a relocatable embedded distribution; it ignores PYTHONPATH. Test scripts insert the repository source path explicitly.

```powershell
./revit-c-bridge/scripts/build.ps1 -RevitYear 2025
./revit-c-bridge/scripts/test.ps1
./revit-mcp/scripts/test.ps1
./revit-pyrevit-extention/scripts/test.ps1
./sync.ps1
./doctor.ps1
```

`sync.ps1` copies server/provider source. Restart the MCP client for server changes; reload the Python provider for provider changes. C# bridge changes require package → close Revit → install → restart. Batch changes to minimize restarts. Roslyn references are explicit in `RoslynProviderHost.ReferenceManifest`.

`revit-mcp/validation/live_release.py` exercises real MCP stdio against an explicitly named disposable model. Never run it on a company production model. The `contracts` phase tests mutation recovery; `captures` exports visual evidence. Keep its output outside this repository and inspect representative PNGs. Run relevant checks once; repeat only after changes or failures.

## Release

`revit-c-bridge/version.txt` owns the stack version. Update Python package metadata and all SBOMs at the same milestone; packaging does not change the version. Dependencies are pinned/hash-locked. Deliberate updates require lock/SBOM refresh and relevant tests.

```powershell
./package-release.ps1 -RevitYear 2025
```

The result is one portable ZIP and checksum in `dist/`. Package scripts build/test and replace generated staging folders. Build downloads are cached outside the repo. Revit 2026/2027 targets exist but require their own live validation before rollout.

Keep docs concise and current. Keep model files, screenshots, historical plans and trial scripts outside the repo. Commit release milestones; never push or publish without user authorization.
