# revit-pyrevit-extention

Zero-third-party pyRevit companion hosting the IronPython 2.7 execution provider for 3XN-RevitMCP. It is the last hop of a `run_python` request: the bridge dispatches admitted Python work to the delegate this extension registers inside pyRevit's persistent IronPython engine; source is compiled and cached on Revit's UI thread before any bridge-owned transaction opens. Results must be bounded JSON-safe primitives/containers; live Revit objects are rejected.

Naming notes:

- The "extention" misspelling is intentional and baked into paths. Do not fix it.
- The extension ships **no ribbon UI**. The 3XN-RevitMCP tab belongs to the C# bridge, whose Python toggle enables, disables, and reloads this provider through the registered delegates. (The old `Revit MCP.tab` name-merge constraint is gone.)

## Lifecycle

`startup.py` resolves the already loaded `RevitMcp.Bridge` assembly and registers a disabled persistent delegate with ABI, build hash, engine version, random generation, and self-test result. It never imports or enables pyRevit Routes. The ribbon Python toggle (C#) enables the registered generation or disables admission; disabling terminally cancels queued-not-started Python work (fixed bridge tools and C# stay available). A pyRevit reload registers a new generation, so the bridge cancels queued old-generation requests instead of silently running them on the new delegate.

Provider module edits take effect after the Python toggle is clicked ON (or `reload_python_provider` is called): the registered reload delegate re-reads the module from disk before re-registering; otherwise the persistent engine keeps the cached import.

All Python here is IronPython 2.7: no f-strings, use `%`/`.format()`, JSON-safe `_result`, wrap `ElementId.Value` in `int()`.

## Test, package, install

```powershell
./scripts/test.ps1
./scripts/package.ps1     # runs tests, stages artifacts/RevitMCP.extension
./scripts/install.ps1     # copies to %APPDATA%\pyRevit\Extensions, no elevation
```

Pass `-ExtensionsPath` to install into a differently configured pyRevit extensions directory.

## Deploy and cost of a change

Day-to-day edits skip package/install: run `../sync.ps1` from the repo root to mirror `RevitMCP.extension` to `%APPDATA%\pyRevit\Extensions\RevitMCP.extension`, then click the Python toggle ON in Revit to reload the provider. No Revit restart. Fresh machines use package + install, then a pyRevit reload; Python defaults OFF every Revit session by design.
