# Requests for Reope

What I need done on the Revit MCP stack, why, and how to tell each item is finished. Ranked by pain. Background reading: `CLAUDE.md` (architecture), `GOTCHAS.md` (the live-session failures behind items 1-4).

Constraints that apply to everything:

- Keep the security posture: current-user-only pipe, per-process nonce, bounded frames, redacted errors, no listeners.
- Bridge changes cost a Revit restart to test, so batch them.
- Tests must pass with the bundled runtime, not system Python (commands in `CLAUDE.md`).

## 1. Bridge must survive dead document handles (highest pain)

**What happens now.** Two triggers leave the bridge permanently broken until Revit is restarted:

- Any script that calls `doc.EditFamily()`. The family document opens and closes, but the bridge's document registry keeps a dead handle. The next call — even `get_active_context` — fails with `InvalidObjectException: referenced object is not valid, possibly deleted`.
- Revit left running overnight or the bound document closed. Same permanent failure.

Bridge OFF/ON and provider reloads do not clear it. Only a full Revit restart does. This is the single most disruptive defect: it turns a one-line mistake into a several-minute restart plus reloading the model.

**What I need.** The bridge detects an invalid document handle, drops the stale registration, and recovers on its own. A failed call may return a clear "list documents again" error once; the call after that must work. `RevitDocumentRegistry.cs` already prunes `!IsValidObject` entries in `Get()`, but something in the request path still touches the dead handle first — find and fix that path, don't just add another prune.

**Done when:** a script runs `doc.EditFamily()` + close, and the very next `get_active_context` and `list_documents` succeed without restarting Revit. Same for a closed-and-reopened project document.

## 2. Modal Revit dialogs must not freeze the bridge

**What happens now.** When API work raises a Revit error dialog (family regen failure like "Line is too short", "Error - cannot be ignored"), the dialog blocks Revit's UI thread. Every queued bridge request stalls until a human clicks the dialog away. This happens even for background family documents the user never sees.

**What I need.** While the bridge executes a request it owns, failure dialogs are intercepted and turned into failed results with the dialog's message in the error. Two Revit mechanisms cover it: a failure preprocessor on bridge-owned transactions, and a `DialogBoxShowing` handler scoped to request execution (registered on request start, removed on completion — never a global always-on hook).

**Done when:** a script that deliberately breaks a family flex returns `execution_failed` with the Revit failure text, and no dialog appears; a request sent right after completes normally.

## 3. Non-ASCII text must round-trip (degree sign crash)

**What happens now.** Scripts or values containing characters like `°` (byte 0xB0) can fail with `DecoderFallbackException: Unable to translate bytes [B0] ... to Unicode`. Dimension and area work in metric models hits `°` and `²` constantly.

**What I need.** UTF-8 enforced at every point where script text or results cross a boundary: pipe framing, provider handoff, IronPython compile, result serialization, compile-cache files. Reproduce first with a script containing `°` in a literal and in a returned string, then fix where it actually breaks.

**Done when:** `run_python` with `u"45.0°"` in source and in `_result` succeeds, and the same through a saved tool.

## 4. Blunt the IronPython `.Name` trap

**What happens now.** Reading `.Name` on an element type (`FamilySymbol`, `ElementType`) throws `MissingMemberException: Name` under IronPython 2.7. The workaround is `Element.Name.GetValue(elem)`. Three shipped saved tools had this bug (fixed in commit `e20f1a5`); every future script is one `.Name` away from the same crash, and the transaction rolls back at the end after all the work is done.

**What I need.** The Python provider injects a helper into script scope — `elem_name(element)` — that works for every element, instances and types alike. Document it in the saved-tools contract and in the `run_python` tool description so scripts get steered to it.

**Done when:** `elem_name(some_family_symbol)` works in `run_python` without imports, and the saved-tools README documents it.

## 5. Revit 2026 and 2027 live certification

**Why.** Only Revit 2025 is live-certified. The office is moving, and Autodesk is shipping the .NET 10 update to Revit 2025 and 2026 as in-place updates — the stack must not be pinned to one year when that lands.

**What I need.**

- Certify the bridge on Revit 2026 and 2027 against the existing test list (build, install, Bridge ON/OFF, Python ON, saved tools, Roslyn provider, Activity pane).
- Plan the .NET 10 move as a single target-framework bump, not per-year multi-targeting. `RoslynProviderHost.ReferenceManifest()` already self-adapts to the runtime directory.
- Known risk to watch: pyRevit's IronPython support lagging the .NET 10 update. If pyRevit blocks, say so early — the fallback discussion (different Python hosting) is a bigger decision I want raised, not made silently.

**Done when:** the certification checklist passes on 2026 and 2027, and there is a written one-page plan for the .NET 10 update with the pyRevit risk assessed.

## 6. LSP + Revit API stubs for script authoring

**Why.** Scripts for `run_python` and saved tools are written blind: no autocomplete, no type checking, IronPython 2.7 quirks found only at runtime in Revit. Items 3 and 4 exist because errors surface at the worst possible time — mid-transaction in a live model.

**What I need.** Editor support for the scripting dialect: Revit API stubs that match what the provider actually exposes (`doc`, `uidoc`, `uiapp`, `request`, `_result`), wired so an editor or the existing lint step (`.lint/check_ipy27.py`) catches wrong API use before a script ever reaches Revit.

**Done when:** writing a saved tool in VS Code gives completion on `doc.` and flags `.Name` on a `FamilySymbol` (or at minimum, the lint step does).

## Working agreements

- Repo is source of truth; deployed copies sync via `./sync.ps1`, drift shows in `./doctor.ps1`.
- Prefer changes in this order: saved tools (no restart), Python provider (Python ON click), Roslyn provider (reload), bridge (restart). Batch bridge work.
- Commit at milestones together with the `version.txt` bump.
- New saved tools follow the contract in `saved-tools/README.md` and must pass the IronPython lint.
