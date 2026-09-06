# Revit execution notes

- Python is **IronPython 2.7**: no f-strings, annotations or Python 3 modules. Set JSON-safe `_result`; convert `ElementId.Value` with `int()`. Use `Element.Name.GetValue(element_type)` when `.Name` fails. Use explicit Unicode escapes for non-ASCII script literals.
- `auto` owns one transaction; do not nest your own. `group` wraps that transaction in an assimilated group. `manual` owns and closes every transaction itself. Check `Commit() == TransactionStatus.Committed`; a rolled-back commit need not throw.
- Family loading into a project requires no open target transaction. Close/rollback inner transactions before rolling back a group or closing a family document. Guard cleanup so it does not hide the original error.
- Receipts distinguish queue time from execution. A responsive capability call does not mean Revit is ready to dispatch. Modal UI and edit modes can delay work. Do not steal focus or blindly resubmit mutations.
- Cancellation is cooperative: Python `check_cancelled()` / `report_progress(...)`; C# `RevitMcp.Contracts.ScriptControl.ThrowIfCancellationRequested()` / `ReportProgress(...)`. It cannot interrupt a native API call.
- Suppressed warnings remain in `receipt.details.notices`; later `get_warnings` does not include deleted warnings. Counts alone do not establish geometric correctness.
- Raw parameters and bounds use Revit internal units. Apply a bounding box's full transform before treating its corners as model coordinates. Prefer stable parameter IDs over localized names.
- PNG capture creates persistent helper views. Revit ignores temporary isolation during export, so helpers use permanent visibility in their own views. Curtain panels need their wall container visible. A 3D section box also needs its 2D crop fitted.
- Sections under `3XN MCP - …` may be replaced; keep working drawings out of that reserved namespace. Exit an active helper section before recreating it. Files are outside Revit Undo/rollback.
