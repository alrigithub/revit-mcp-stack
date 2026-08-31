# Revit 2025.5 / 2026.5 move to .NET 10 — impact on the RevitMCP stack

Researched 2026-08-31 (Autodesk developer blog, Autodesk forums, pyRevit
issue tracker + forums, rvtdocs). Companion to
[revit-api-2026-2027-findings.md](revit-api-2026-2027-findings.md).

## What Autodesk confirmed

- **Revit 2026.5** (shipped ~first week of Aug 2026) and **Revit 2025.5**
  (planned ~second week of Sept 2026) switch the runtime from .NET 8 to
  **.NET 10**. Driver: Microsoft ends .NET 8 support on 2026-11-10.
- **Zero API changes** in these point updates — runtime only. (Matches the
  rvtdocs check: no API-page changes attributable to the runtime swap.)
- Autodesk's guidance: existing add-ins "should continue to work unless they
  are affected by .NET 10 breaking changes"; complex add-ins (native code,
  third-party deps) may need recompile/retarget. No opt-out or rollback is
  documented.
- Consequence: once machines take these updates, **2025, 2026 and 2027 all
  run .NET 10**, which validates the existing plan of a single TFM bump to
  `net10.0-windows` instead of per-year multi-targeting.

## Impact by component

### C# bridge (`net8.0-windows`) — LOW risk, sequencing matters

Pure managed code (named pipes, System.Text.Json, WPF pane, Roslyn). A
net8-targeted library loads fine on a .NET 10 host; the reverse does not.
That asymmetry dictates the order:

1. **Stay on net8.0-windows while any live machine is still on
   2025.0–2025.4 / 2026.0–2026.4.** The net8 DLL serves both runtimes.
2. Bump to `net10.0-windows` (single TFM, one `Directory.Build.props`
   change) only once every target machine has taken the .NET 10 point
   update. Nice3point API packages are year-scoped, not runtime-scoped —
   no package change needed for the point updates themselves.
3. `build.ps1 -RevitYear` maps year → TFM; after the bump the 2025/2026/2027
   rows all point at net10. No per-point-release logic needed thanks to
   forward-compat in step 1.

`RoslynProviderHost.ReferenceManifest()` resolves compile references from
`RuntimeEnvironment.GetRuntimeDirectory()` at runtime, so the C# dynamic-code
path self-adapts to whichever runtime Revit hosts (verified earlier — no
hardcoded .NET 8 paths). Roslyn 4.11 reads .NET 10 assemblies fine.

### pyRevit / IronPython provider — HIGH risk, act before updating Revit

This is where the stack can actually break, and it is not hypothetical:

- pyRevit forum thread "Revit 2026.5 Update .Net10 Issues": users report
  pyRevit dead after the 2026.5 update; a maintainer confirms the **latest
  WIP installer resolves it** (release builds at the time did not).
- pyRevit issue #3544: structural problem — pyRevit picks its runtime DLL
  **by Revit year, not build number**, so a 2026.5 (.NET 10) session can get
  attached to pyRevit's net8 runtime. Fix requires build-number-level
  detection; forward-looking, not fully landed.
- Separate ecosystem signal (RevitBatchProcessor #147): **IronPython 2.7.12
  cannot run on .NET 10** (reflection-emit/dynamic type generation breaks);
  that project migrated to IronPython 3.4.2 on net10. Whether pyRevit's WIP
  keeps an IronPython 2.7 engine functional on .NET 10 or forces the IPY3
  engine is **unverified** — and it decides whether our IronPython 2.7
  dialect contract (no f-strings, `%`/`.format()`, saved python tools)
  survives or must migrate to an IronPython 3 dialect.

**Playbook:**
1. **Hold Revit point updates** (2025.5/2026.5) on working machines until the
   Python path is validated — the bridge itself is expected to survive, but
   `run_python` and every saved Python tool may not.
2. Validate on a sandbox: Revit 2026.5 + latest pyRevit WIP + our extension;
   Python toggle ON; run the saved-tool smoke set. Specifically confirm which
   engine (IPY2.7 vs IPY3) pyRevit attaches under .NET 10.
3. If IronPython 2.7 is dead on .NET 10: the saved Python tools need a
   dialect pass (f-strings stay banned only by our own convention at that
   point; the real work is auditing 2.7-isms that IPY3 rejects) and the
   CLAUDE.md/tool-description dialect contract changes with it.
4. **The Roslyn/C# path is the designed fallback** — `run_csharp` and saved
   C# tools have no pyRevit dependency and keep working throughout. Agents
   can be steered to C# for mutations while Python is being revalidated.

### Python MCP server + pipe client — NO impact

Pure CPython 3.12, ctypes named pipes, no .NET anywhere. The mcp 2.x upgrade
track is entirely orthogonal to this.

### Timing pressure

2025.5 lands **~two weeks from this writing**. The live-certified year is
2025, so the exposure is immediate wherever Revit auto-updates. Decide per
machine: defer the update, or pre-stage latest pyRevit WIP and validate.

## Sources

- Autodesk Developer Blog: "Autodesk Desktop Products 2025/2026: .NET 10
  Updates" — blog.autodesk.io/autodesk-desktop-products-2025-2026-net-10-updates/
- Autodesk Developer Blog: "Call for Preview Testing: Revit 2026/2025
  Migration to .NET 10" — blog.autodesk.io/call-for-preview-testing-revit-2026-2025-migration-to-net-10/
- Autodesk support: "Requirements for products affected by the Microsoft
  .NET 10 transition"
- pyRevit issue #3544 (year-based runtime mapping vs 2026.5), pyRevit forum
  thread 10453 (2026.5 breakage + WIP fix confirmed by maintainer)
- RevitBatchProcessor issue #147 (IronPython 2.7.12 unusable on .NET 10;
  IronPython 3.4.2 works)
