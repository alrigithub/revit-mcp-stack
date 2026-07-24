# Revit MCP bridge v0.9

This workspace contains a local-only Revit automation stack. It has no network listener, telemetry, Node runtime, `pywin32`, or pyRevit Routes dependency.

| Component | Responsibility |
| --- | --- |
| [`revit-c-bridge`](revit-c-bridge/README.md) | Revit `IExternalApplication`, current-user named pipe, discovery, bounded UI queue, request ledger, document/transaction binding, verification, and isolated Roslyn execution. |
| [`revit-pyrevit-extention`](revit-pyrevit-extention/README.md) | Zero-package pyRevit companion that registers a persistent IronPython delegate and exposes Python ON/OFF controls. The folder name intentionally follows the agreed spelling. |
| [`revit-mcp`](revit-mcp/README.md) | Python stdio MCP process, discovery, Win32 named-pipe client, tool schemas, packaging, and live-test adapter. |

Both Revit-side capabilities start **OFF** in every Revit session. Bridge ON/OFF controls admission to the pipe. Python ON/OFF independently controls dynamic Python execution. No request confirmation dialogs are used.

The C# ribbon also includes an **Activity** control. It opens a compact native Revit dockable pane that follows Revit's light/dark theme live and shows the actual bridge, Python, C#, active-document, queue, and bounded recent-request state. Source code and model results are not logged.

## Validation sequence

Run from PowerShell at the repository root:

```powershell
./revit-c-bridge/scripts/test.ps1
./revit-mcp/scripts/test.ps1
./revit-pyrevit-extention/scripts/test.ps1
./revit-c-bridge/scripts/build.ps1 -RevitYear 2025
./revit-c-bridge/scripts/package.ps1 -RevitYear 2025
./revit-mcp/scripts/package.ps1
./revit-pyrevit-extention/scripts/package.ps1
```

Revit 2025 is the current live-tested target:

```powershell
./revit-c-bridge/scripts/build.ps1 -RevitYear 2025
./revit-c-bridge/scripts/package.ps1 -RevitYear 2025
./revit-c-bridge/scripts/install.ps1 -RevitYear 2025
./revit-pyrevit-extention/scripts/install.ps1
./revit-mcp/validation/run-live.ps1 -RevitYear 2025
```

The live harness writes JSON Lines results and a Markdown summary under `revit-mcp/validation/results/`. It does not infer a pass for any unchecked Revit-only behavior. See [`LIVE-REVIT-CHECKLIST.md`](revit-mcp/validation/LIVE-REVIT-CHECKLIST.md).

## Security boundary

V0.x trusts clients running as the same Windows user. The boundary is a current-user-only named pipe plus a random per-process nonce, bounded frames/source/results/queue/logs, explicit session gates, and no listening TCP port. Protocol fields reserve room for future authentication; V0.x does not claim full client identity.

## Generated artifacts

Build/package outputs are ignored in every component. Packaging scripts copy only reviewed build outputs into a staging directory. Target-PC install scripts do not resolve dependencies and do not require elevation. Authenticode signing is supported by the packaging script when the owner supplies a code-signing certificate; this repository cannot truthfully claim signed output without that certificate.
