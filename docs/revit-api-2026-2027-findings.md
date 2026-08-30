# Revit API 2026/2027 deep search — findings for the RevitMCP stack

Researched 2026-08-30 against the official Revit API documentation database
(rvtdocs MCP: per-version pages, availability, diffs, What's New notes for
2020–2027). Method: extracted the bridge's actual API surface from
`revit-c-bridge/src`, then checked every load-bearing member's availability
2025→2027 and read the full 2026/2027 What's New notes for risks and
opportunities.

## Headline: the 2026/2027 certification is API-cheap

Every API the bridge's plumbing depends on is **unchanged from 2025 through
2027** (verified member-by-member):

| Member | 2025 → 2027 |
|---|---|
| `UI.ExternalEvent` (create/raise pattern) | unchanged since 2020 |
| `DB.TransactionGroup.Assimilate` | unchanged since 2020 |
| `UI.UIApplication.DialogBoxShowing` | unchanged since 2020 |
| `ApplicationServices.ControlledApplication.FailuresProcessing` | unchanged since 2020 |
| `ApplicationServices.ControlledApplication.DocumentChanged` | unchanged since 2020 |
| `UI.DockablePaneProviderData` (pane registration) | unchanged since 2020 |
| `DB.PDFExportOptions` (export_view) | unchanged since 2025 |

The whole `Autodesk.Revit.UI` namespace gained only 8 classes 2025→2027 (all
ExternalDataManager, unrelated to us) and lost none. The real certification
work is runtime, not API: .NET 10 for 2027 (single-TFM plan already noted) and
pyRevit/IronPython keeping up.

## Breaking changes checked against our code

- **2026 removed `ElementId(int)` (32-bit ctor) and `ElementId.IntegerValue`.**
  Audited the repo: the bridge uses only `new ElementId(long)` (the Int64
  overload, which stays — `RevitRequestHandler.cs`) and `.Value`; zero
  `IntegerValue` hits anywhere including saved tools. **Clean.** Keep the
  GOTCHAS rule (`int(ElementId.Value)` in IronPython) as the standing
  convention for new saved tools.
- **2026 behavioral change:** `DocumentClosing`/`DocumentClosed` no longer fire
  when a `DocumentOpening` was canceled. The bridge subscribes to neither
  (document tracking is generation-checked per call). **No impact.**
- **2026 parameter *display-name* renames** (OmniClass→Classification,
  beam/column "Length"→"System Length", reference labels). BuiltInParameter /
  ForgeTypeId access is unaffected — but any saved tool or agent script that
  looks up parameters **by display name** will mismatch across 2025 vs 2026
  docs. Convention worth adding to `saved-tools/README.md`: prefer
  `BuiltInParameter`/`ForgeTypeId` lookups over name matching.
- **2027 moves the all-users add-in folder** from `C:\ProgramData\Autodesk\
  Revit\Addins\20XX` to `C:\Program Files\Autodesk\Revit\Addins\20XX`
  (security hardening). Our `install.ps1` targets the per-user location, which
  is still supported — **no change needed**, but `doctor.ps1` drift reporting
  should not assume ProgramData if it ever grows an all-users check.
- **2026 removed CefSharp from Revit's install.** We never depended on it.
  Net positive: fewer DLL-conflict sources in-process.

## Improvement opportunities (ranked)

### 1. Edit-mode awareness for admission/busy reporting — `Document.IsInEditMode()` / `GetActiveEditMode()` (2025.3+)

The strongest find. The bridge currently can't tell "Revit is in a sketch /
family / group edit mode" from "ready", yet GOTCHAS documents exactly those
traps (family-doc transactions, stale doc). `GetActiveEditMode()` returns an
`EditModeType` enum (`None` when idle). Uses:
- `get_capabilities` / `get_active_context` could report the active edit mode
  so agents stop guessing why a mutation was refused.
- The request handler could refuse or annotate mutations admitted during an
  edit mode with a precise `revit_busy` reason.

Availability caveat: exists from **2025.3**, and we build against the 2025
package. Options: call via reflection with graceful fallback (works on any
build), or bump the 2025 target to the 2025.3 API package if live machines are
on 2025.3+.

### 2. Add-in dependency isolation (2026 manifest option, refined in 2027)

`<UseRevitContext>False</UseRevitContext>` (+ `ContextName`) loads the add-in
in its own AssemblyLoadContext — ending version conflicts with other add-ins'
dependencies. 2027 adds `PublicAssemblies`/`Dependencies`/`ClientIdDependency`
for controlled cross-context sharing.

**Adopt with care, not by default.** The bridge↔pyRevit provider handshake
(delegates registered from IronPython into bridge types) depends on both sides
resolving the same bridge assembly. Isolating the bridge into its own context
without declaring the shared surface could break that registration. For the
2026/2027 certification: test with isolation OFF first (default), then trial
`UseRevitContext=false` + `PublicAssemblies` for the contracts DLL as a
hardening milestone. The 12 C# tests won't catch this; it needs a live
pyRevit-toggle test.

### 3. Human-readable warning severity — `LabelUtils.GetFailureSeverityName()` (2026+)

`get_warnings` returns severity today; from 2026 the API can localize/name it.
Trivial guarded enhancement (reflection or version check) when 2026
certification lands.

### 4. Live pane title — `DockablePane.SetTitle()` (2027+)

The Activity pane could show state in its caption ("RevitMCP — Bridge ON ·
Python ON · v0.1.17"). Cosmetic, 2027-only, zero risk behind a version guard.

### 5. Programmatic add-in health for `doctor.ps1` — `AddInsManagerSettings` (2026+, RevitAddInUtility.dll)

2026 lets users disable add-ins per-user; a disabled bridge looks identical to
a broken install. `doctor.ps1` could read
`AddInsManagerSettings.GetAllAddInItemSettings()` (works outside Revit via
RevitAddInUtility) and report "bridge add-in disabled by user" — plus
`LoadTime`, a free metric for our own startup cost.

### 6. Deferred, noted for the roadmap

- **2026 `Curve.Intersect` overloads deprecated** in favor of
  `CurveIntersectResult` — only matters if saved tools grow geometry QA;
  the old overloads still work in 2027.
- **2027 `AnnotationLabel`**, **2026 `SpatialElement.GetDefaultLocation()` /
  `Recenter()`**, **2026 `Wall.GetAttachmentIds()`/`AddAttachment()`**,
  **2026 `Ceiling.GetCeilingGridLine()`** — new read/write surfaces that make
  good future saved tools; none require bridge changes (dynamic code reaches
  them as soon as the session runs that Revit version).

## Version stats for context

- 2026 vs 2025.3: 615 new pages, 115 removed, 113 newly obsolete.
- 2027 vs 2026: 1,012 new, 330 removed, 99 newly obsolete; .NET 10 runtime.

## Suggested sequencing

1. Now (no restart cost): saved-tools README note on parameter-name matching.
2. Next bridge batch: edit-mode reporting via reflection (#1), warning
   severity names (#3) behind the same guard.
3. 2026 certification: run with default (non-isolated) context; add
   `doctor.ps1` add-in-settings check (#5).
4. 2027 certification: .NET 10 TFM bump (planned), `SetTitle` (#4), then
   evaluate isolation (#2) as its own milestone with a live pyRevit handshake
   test.
