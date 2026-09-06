# Native Revit bridge

C# add-in owning same-user pipe admission, document identity, Revit UI-thread execution, transactions, receipts, native PNG inspection and ribbon controls.

Build/package with `scripts/build.ps1` / `scripts/package.ps1 -RevitYear 2025`; run `scripts/test.ps1`. Install requires Revit closed. Use the root release ZIP for colleagues.

Targets: Revit 2025/2026 on .NET 8; 2027 on .NET 10. Live release validation currently covers 2025. `version.txt` is the stack version; packaging never changes it. Roslyn lives in a reloadable isolated provider.

[Tool contract](../docs/tools.md) · [Maintainer instructions](../CLAUDE.md) · [Endpoint troubleshooting](AV-EDR-RUNBOOK.md)
