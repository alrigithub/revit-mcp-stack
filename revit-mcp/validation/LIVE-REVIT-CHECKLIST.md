# Live Revit 2026 v0.0 checklist

Use a disposable local model and screen recording. Record machine/Revit/pyRevit/engine/EDR versions, package SHA-256 manifest, focus-nudge setting (default disabled), timestamps, request IDs, document session/generation, pass/fail, and evidence path. Never mark an unchecked row passed.

## Baseline

1. Install the reviewed Revit 2026 bridge and pyRevit packages without elevation. Start Revit: Bridge and Python must both visibly show actual OFF; only ON actions are valid.
2. Open/activate the disposable model. Click Bridge ON. Verify green icon, OFF action becomes valid, current-user pipe/discovery appears, and no TCP listener appears.
3. Reload pyRevit 6.4+ with optional Routes running unrelated traffic. Verify bridge/ribbon/shutdown health. Click Python ON; verify self-test, green state, current generation, and OFF action becomes valid.
4. Run `validation/run-live.ps1 -RevitYear 2026 -Pid <pid>`. Preserve JSONL, `summary.json`, and `summary.md`.

## Scheduler, deadlines, and identity

- Measure focused idle, unfocused, busy, modal dialog, failure dialog, and clean shutdown. Repeat focus behavior with nudge disabled and experimental enabled; keep disabled afterward.
- Switch and close the bound document after admission. Confirm no request runs against the later active document and closed/replaced generations fail.
- Use two clients. Inject duplicates by request ID and idempotency key, drop a response, then query status. Confirm one mutation only and original ledger record returned.
- Queue 100 writes with short deadlines behind a modal dialog. Confirm expired queued writes never mutate later and every admitted ID reaches exactly one terminal state.
- Stress enqueue at handler exit and non-accepted `Raise()` states. Confirm no lost wakeup and no spin/high idle CPU.

## Transactions and verification

- Force Python/C# failures in `auto` and `group`; confirm rollback. Run a ten-step atomic batch; confirm exactly one Revit undo item. Test explicit non-atomic mode and per-step status.
- Confirm no transaction remains open across calls. Export only after commit; note file output is not undoable.
- Validate bounded identity/type/category/session, instance/type parameters, storage/units/raw/display, model/view boxes, compact geometry, paginated host/type/level/view/dependent relationships, worksharing, phase/design option, preflights, and warning delta. Confirm every response names omitted/deferred fields.
- Confirm the bridge reports only its own request/transaction state plus `Document.IsModifiable`, never other add-ins' transactions.

## Provider lifecycle and soak

- Reload pyRevit externally with Python work queued/running. Running call finishes; queued old-generation calls become `provider_reloaded_before_start`; controls show the newly registered generation/state.
- Use bridge-controlled reload. Confirm admission quiesces, running finishes, queued work is terminally cancelled, self-test passes, generation swaps atomically, and admission reopens.
- Run 1,000 Python and 1,000 C# calls. Record process working set, managed heap if available, loaded assembly counts, cache hits, and weak-reference collectible-context observations before/after. Treat unload as experimental.
- Click Python OFF: fixed/C# tools remain healthy and `run_python` reports unavailable. Click Bridge OFF with queued/running work: discovery/admission stop, queued work is terminal, running work reaches its real outcome, ledger remains queryable until shutdown.

## Recovery, ACC, EDR, and versions

- Kill Revit. Confirm stale discovery is rejected by PID/start identity and cleanup works. Restart: both gates default OFF.
- On ACC/workshared test data, validate undo expectations and close-without-sync. Never infer that file export is undone and never automate sync/close decisions.
- Run the AV/EDR runbook on the firm's actual stack. Record alerts/exclusions by signed hash; never disable protection or enable HTTP fallback.
- Co-install and repeat ribbon/engine/optional-Routes/reload/bridge/shutdown checks on Revit 2025, 2026, and 2027 before certification.

## Metrics

Publish p50/p95/p99 for client roundtrip, pipe service, queue wait, UI dispatch, and provider execution from the same request IDs. Separate focused/idle from modal/busy distributions; never state a modal/busy latency promise.
