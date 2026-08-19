# MCP Bridge Gotchas

Hard-won operational notes from live sessions. Read this before driving the bridge for real work.

## Connection

- **Stale document reference — FIXED in bridge 0.1.10.** Closed documents
  (overnight sessions, `doc.EditFamily()` leftovers) used to poison the session
  registry until a full Revit restart: Revit's `Document.Equals`/`GetHashCode`
  throw `InvalidObjectException` on dead handles, so one dead dictionary key
  broke every later call. The registry now purges dead handles on every call
  and skips invalid documents when enumerating. Verified live: EditFamily →
  register → close → next call succeeds. If a bound document closes, calls
  return a clean `document_closed` — `list_documents` again to re-bind.
- **`doc.EditFamily()` is safe through the bridge since 0.1.10.** Scripts should
  still close family docs they open (`fdoc.Close(False)` in a guarded finally);
  an fdoc left open is a real open document and stays in `list_documents`.
- **Modal dialogs no longer block the bridge** (0.1.10+, "Bypass dialogs" ON —
  the default, toggle in ribbon **Settings**). During bridge calls: warnings are
  committed and their full text logged; blocking errors roll the transaction
  back and the call fails with `Revit reported: …`; stray popups are
  auto-answered (task dialogs → Cancel, message boxes → OK) and recorded.
  Note: overlap-type conflicts (walls, room separation lines) are WARNINGS in
  Revit 2025 — they commit. The error-severity auto-rollback branch has not yet
  fired in a live session; first real constraint failure will exercise it.
  With the bypass OFF, stock dialogs return — and a popup then freezes the
  queue until a human dismisses it.

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
- Modifying the document with no open transaction returns
  `modification_without_transaction` with the fix in the message (0.1.11+).
- **In `manual` mode, check what `Commit()` returns.** When failure processing
  rolls a transaction back (blocking error under the dialog bypass), `Commit()`
  returns `RolledBack` instead of raising — a script that ignores the status
  continues with its changes silently gone.
- Warnings suppressed by the dialog bypass are deleted at commit; audit the
  document's warning state with `get_warnings`, and read the suppressed texts
  in the Activity log / `get_logs_tail`.

## Execution policy (ribbon Settings, 0.1.11+)

- **Allow arbitrary code** (default OFF on fresh installs): when off,
  `run_python`/`run_csharp` accept only source that content-matches an enabled
  saved-tool script on disk in the configured roots — `run_saved_tool` and all
  read-only bridge tools keep working; anything else fails with
  `arbitrary_code_disabled`.
- Both settings are read per call: flipping them needs no restart and no
  bridge toggle.

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
