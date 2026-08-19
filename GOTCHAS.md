# MCP Bridge Gotchas

Hard-won operational notes from live sessions. Read this before driving the bridge for real work.

## Connection

- **Stale document reference** = every doc-level call fails with
  `InvalidObjectException: referenced object is not valid, possibly deleted`.
  Bridge toggle off/on and provider reloads **do not clear it**.
  → **Restart Revit.** (Happens after Revit is left running overnight / doc closed.)
- **`doc.EditFamily()` poisons the bridge.** Opening + closing a family doc via
  EditFamily leaves a dead document handle in the bridge's session registry —
  the *next* call (even `get_active_context`) fails instantly with the
  InvalidObjectException above, and only a full Revit restart clears it.
  → Never EditFamily through the bridge. Carry profile/geometry data as raw
  coordinates instead. `app.NewFamilyDocument()` + `LoadFamily` + `Close(False)`
  is safe — it's specifically EditFamily that breaks.
- **Family-doc regen failures raise MODAL error dialogs** ("Line is too short",
  Error - cannot be ignored) even for API-created background family docs. The
  dialog blocks the whole bridge until the user dismisses it in Revit. Constraint
  experiments in family docs are therefore not "invisible" — a broken flex
  interrupts the user.

## Family documents (via NewFamilyDocument)

- `fdoc.Close(False)` **throws if any transaction is open** ("Close is not
  allowed...") and that masks the real exception. Pattern: track t/t2, on except
  roll back whichever `HasStarted() and not HasEnded()`, close in a guarded
  finally.
- `TransactionGroup.RollBack()` also throws while a member transaction is open —
  roll back inner transactions first or the bridge reports
  `manual_transaction_left_open`.
- `fdoc.LoadFamily(doc)` **cannot run inside an open project transaction** —
  pre-create every family BEFORE starting the placement transaction.
- Silence the overwrite popup with an `IFamilyLoadOptions` implementation
  (IronPython classes can implement the interface; set `.Value = True` on the
  out-params and return True).
- Healthy bridge but failing calls ≠ bridge problem. Check `get_capabilities`
  (works without the doc queue) to tell the two apart.
- `list_revit_instances` gives the live PID; a new PID = a fresh session.

## Transactions

- `transaction_mode` is **required** and pinned per saved tool:
  - `read` — no transaction (queries, exports)
  - `auto` — bridge opens ONE transaction; **do not open your own**
  - `manual` — script owns and closes every transaction
  - `group` — one assimilated undo item
- One script = one undo step. Batch related work into ONE call — each call waits on
  Revit's single UI thread.

## IronPython 2.7 traps

- **`MissingMemberException: Name`** — reading `.Name` on an element *type*
  (FamilySymbol, ElementType) fails in IronPython 2.7. Read it through the base
  class instead: `Element.Name.GetValue(elem)`. Names on instances, levels,
  views, categories, and materials are fine.
- **`DecoderFallbackException: Unable to translate bytes [B0]...`** — a script or
  value carried a special character (0xB0 is the degree sign °) that hit a text
  encoding mismatch. Keep scripts ASCII-only; if you need a symbol, use a unicode
  escape like `u"°"`.
- No f-strings, `%`/`.format()` only; JSON-safe `_result`; `int()` around
  `ElementId.Value`.

## Saved tools

- Registry read **per call** → edits are live immediately, no restart.
- Manifest `description` is capped at **500 chars** (silent-ish failure:
  `description must be a non-empty string of at most 500 chars`).
- Params reach the script as the `request` dict; return JSON-safe data via `_result`.
- Promote a proven script by dropping `<name>.json` + `<name>.py` in the registry.
- Extra read-only search roots via `saved_tools_paths` in settings.json; first
  root wins on duplicate IDs, disabled never falls through to a later root.

## Image export

- `ExportRange.VisibleRegionOfCurrentView` = whatever is on screen (fragile).
  `ExportRange.SetOfViews` + `SetViewsAndSheets(ids)` = deterministic. Prefer it.
- Revit names files `<prefix> - <ViewType> - <ViewName>.png` — match tiles by
  view-name substring, and diff the folder before/after to find what was written.
- File writes are **not Revit-undoable**.
