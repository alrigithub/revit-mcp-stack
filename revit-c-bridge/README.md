# revit-c-bridge

C# `IExternalApplication` add-in — the Revit-side half of the stack. In the request path it is everything between the named pipe and the Revit API: it accepts framed JSON from the Python MCP server, checks the per-process nonce, admits requests into a bounded queue with a ledger, marshals them onto Revit's UI thread through one long-lived `ExternalEvent`, opens the transaction the request's `transaction_mode` asks for, and dispatches to a provider (IronPython via the pyRevit extension, or the isolated Roslyn C# provider). It also owns the ribbon and the Activity dockable pane.

Targets Revit 2025/2026 on .NET 8 and Revit 2027 on .NET 10. Builds restore compile-only Revit API packages, so no installed Revit is needed to build and no API assemblies ship with the add-in. Only 2025 is live-certified today.

The **Activity** ribbon button shows a native dockable pane that follows Revit's light/dark theme and displays bridge/provider/document/queue state plus the bounded operational log. It never logs source code or model results.

## Runtime architecture

- Bridge ON creates a current-user-only named pipe with a random per-process nonce and an atomic PID discovery record; Bridge OFF stops admission, terminally cancels queued work, and disposes the listener.
- One bounded FIFO feeds one long-lived `ExternalEvent`; a semaphore-driven coordinator is the only caller of `Raise()`.
- Requests bind document session/generation at admission; closed/reopened documents get a new session, and UI-only tools require that document to be active.
- The ledger deduplicates request/idempotency IDs and keeps explicit terminal states with bounded history.
- Transaction modes `read`/`auto`/`manual`/`group` are explicit; atomic batches use one `TransactionGroup` with per-step transactions, then `Assimilate()`.
- Roslyn 4.11.0 is the only third-party runtime family, isolated under `providers/roslyn/1` in a collectible load context with an explicit compile-reference allowlist and SHA-256 cache keys.

Core projects reference only Revit API/UI and the .NET BCL. No Newtonsoft.Json, DI container, third-party logger, telemetry, or network listener — keep every change inside that posture.

## Build, test, install

```powershell
./scripts/test.ps1                       # C# test suite
./scripts/build.ps1 -RevitYear 2025      # compile only (2025/2026/2027)
./scripts/package.ps1 -RevitYear 2025    # build + stage artifacts/2025, then bumps version.txt
./scripts/install.ps1 -RevitYear 2025    # Revit MUST be closed — the DLL is locked while Revit runs
```

Revit 2027 builds need the .NET 10 SDK. `-RevitApiDir` points builds at locally installed API assemblies on offline machines. `-CertificateThumbprint` Authenticode-signs staged DLLs with a CurrentUser certificate; unsigned output is never labeled signed.

## Cost of a change

Every bridge change costs a Revit restart: package → close Revit → install → reopen. Both capabilities (Bridge ON, Python ON) come back OFF after the restart, by design. Batch bridge changes so one install carries several; prefer saved tools or provider edits when they can do the job.

## Versioning

`version.txt` is the single source of truth. `Directory.Build.props` stamps it into the DLLs, and the Activity pane footer shows the *loaded* assembly's version. `package.ps1` ships the current number then auto-advances the file — commit the bump together with the code it shipped so versions stay traceable.

## Known live-only gates

Revit must validate ribbon rendering/enabled state, ExternalEvent modal/busy behavior, real transaction rollback/undo, document close/switch, PDF export, Python ABI, provider reload, Revit shutdown, memory/assembly behavior, co-install with pyRevit 6.4+, ACC close-without-sync, and EDR. The repository does not claim those passed.
