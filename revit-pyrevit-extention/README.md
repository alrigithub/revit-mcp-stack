# revit-pyrevit-extention

Zero-third-party pyRevit companion hosting the IronPython 2.7 execution provider — the last hop of a `run_python` request. The bridge dispatches admitted Python work to the delegate this extension registers inside pyRevit's persistent IronPython engine; source is compiled and cached on Revit's UI thread before any bridge-owned transaction opens. Results must be bounded JSON-safe primitives/containers; live Revit objects are rejected.

Two naming constraints are load-bearing:

- The "extention" misspelling is intentional and baked into paths. Do not fix it.
- The tab folder must stay exactly `Revit MCP.tab` (with the space) — that exact title merges it with the C# ribbon tab.

`assets/` holds the source SVG art for the pushbutton `icon.png` files.

## Lifecycle

`startup.py` resolves the already loaded `RevitMcp.Bridge` assembly and registers a disabled persistent delegate with ABI, build hash, engine version, random generation, and self-test result. It never imports or enables pyRevit Routes. Python ON enables the registered generation; Python OFF disables admission and asks the bridge to terminally cancel queued-not-started Python work (fixed bridge tools and C# stay available). A pyRevit reload registers a new generation, so the bridge cancels queued old-generation requests instead of silently running them on the new delegate.

Provider module edits only take effect after clicking Python ON — the button `reload()`s the module; otherwise the persistent engine keeps the cached import.

All Python here is IronPython 2.7: no f-strings, use `%`/`.format()`, JSON-safe `_result`, wrap `ElementId.Value` in `int()`.

## Test, package, install

```powershell
./scripts/test.ps1
./scripts/package.ps1     # runs tests, stages artifacts/RevitMCP.extension
./scripts/install.ps1     # copies to %APPDATA%\pyRevit\Extensions, no elevation
```

Pass `-ExtensionsPath` to install into a differently configured pyRevit extensions directory.

## Deploy and cost of a change

Day-to-day edits skip package/install: run `../sync.ps1` from the repo root to mirror `RevitMCP.extension` to `%APPDATA%\pyRevit\Extensions\RevitMCP.extension`, then click Python ON in Revit to reload the provider. No Revit restart. Fresh machines use package + install, then a pyRevit reload; Python defaults OFF every Revit session by design.
