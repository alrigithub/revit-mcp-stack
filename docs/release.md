# Release 0.2.0

Prepared for **Windows x64, Revit 2025.4, pyRevit 6.4 and Claude Code**. Use the Revit 2025 ZIP in `dist/`. Revit 2026/2027 build targets remain available to maintainers, but have not received this release's live validation.

## Included

- 23 generic MCP tools; independently attached saved tools keep company modeling logic outside the bridge.
- Clear execution receipts, retained warnings and source-line errors, separate queue/execution timing, responsive status, progress and cooperative cancellation.
- Compact element queries and optional assertions that can roll back bridge-owned transactions.
- Shared ribbon/MCP PNG helpers for building elevations, corner axonometrics, floors, element context/isolation and straight middle cuts.
- A portable bundled Python runtime, pinned dependencies, package checksums and a per-user installer that registers Claude Code.

## Validation

68 automated tests cover the core, transport lifecycle and Python provider. Live MCP tests on a disposable copy of the 3XN benchmark model covered commit/rollback, retry deduplication, cancellation, runtime diagnostics, suppressed warnings, material/bounds queries, PDF export and PNG capture. Representative building, floor and element sheets were inspected visually; the working view and selection were preserved. The ribbon capture also worked with Bridge and Python OFF.

The installer is exercised from an extracted release folder using Windows PowerShell, and Claude Code's MCP connection is checked. This is a tested Revit 2025 release, not certification for every company model, add-in combination or endpoint-security policy.

## Practical limits

- Bridge and Python start OFF. Fresh settings allow saved scripts only; enable arbitrary code explicitly for agent-written scripts.
- Cancellation needs a script checkpoint. A timeout never proves rollback. Manual scripts and file exports can leave effects outside a Revit transaction.
- Automatic capture scope excludes links and terrain. Select a building explicitly in multi-building models. Elevations follow project north; phases/design options follow Revit visibility.
- Helper views remain in the model until removed. Only reserved `3XN MCP - …` helper views are reused or replaced.
- The core should change rarely, but Autodesk compatibility, defects, dependency security fixes and MCP client changes may require maintenance.

See [installation and everyday use](../README.md) and [tool contracts](tools.md).
