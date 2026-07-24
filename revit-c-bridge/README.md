# revit-c-bridge

Direct `IExternalApplication` add-in for Revit 2025–2027. Revit 2025/2026 target .NET 8; Revit 2027 targets .NET 10. Builds restore exact compile-only Revit API packages, so installed Revit DLL paths are not required and the API assemblies are never shipped with the add-in.

The Revit MCP ribbon includes an **Activity** button that shows or hides a native dockable pane. The compact pane follows Revit's active light/dark UI theme at runtime and displays actual bridge/provider/document/queue state plus the bounded operational log; it does not log source code or model results.

## Runtime architecture

- Bridge ON creates a current-user-only byte-mode named pipe, random nonce, and atomic PID/start-identity discovery record. Bridge OFF first stops admission/removes discovery, terminally cancels queued work, preserves a running UI request's real outcome, and disposes the listener.
- One bounded FIFO feeds one long-lived `ExternalEvent`. A semaphore-driven coordinator is the only caller of `Raise()`, with retained signals/exponential backoff for non-accepted raises and handler-exit races.
- Requests bind document session/generation at admission. Identity fingerprint changes advance generation; closed/reopened documents receive a new session. UI-only tools additionally require that exact document to be active.
- The ledger deduplicates request/idempotency IDs, bounds retained terminal history, and has explicit terminal states for expiry, bridge-off, provider reload/disable, success/failure, and abandonment.
- `read`, `auto`, `manual`, and `group` transaction modes are explicit. Atomic batches use one `TransactionGroup` and per-step transactions, then `Assimilate()`.
- Roslyn 4.11.0 is the only third-party C# runtime family. It lives under `providers/roslyn/1` in a custom load context; the core has no static Roslyn reference. Compilation uses an explicit framework/Revit/contract allowlist, deterministic compilation, SHA-256 cache keys, `#line agent.cs`, and bounded caches.

Core projects reference only Revit API/UI and .NET BCL/framework assemblies. There is no Newtonsoft.Json, WinRT, Windows SDK package, Python.NET, DI container, third-party logger, telemetry, or network listener.

## Build and package

```powershell
./scripts/test.ps1
./scripts/build.ps1 -RevitYear 2025
./scripts/build.ps1 -RevitYear 2026
./scripts/package.ps1 -RevitYear 2025
```

Revit 2027 builds require the .NET 10 SDK. For an offline build machine, `-RevitApiDir` remains available as an explicit fallback to locally installed API assemblies.

Use `-CertificateThumbprint` to Authenticode-sign every staged DLL with an owner-provided CurrentUser certificate. Unsigned output is never labeled signed.

## Known live-only gates

Revit must validate ribbon rendering/enabled state, ExternalEvent modal/busy behavior, real transaction rollback/undo, document close/switch, PDF export, Python ABI, provider reload, Revit shutdown, memory/assembly behavior, co-install with pyRevit 6.4+, ACC close-without-sync, and EDR. The repository does not claim those passed.
