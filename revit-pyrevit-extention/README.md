# revit-pyrevit-extention

Zero-third-party-package pyRevit companion. The agreed folder spelling is preserved.

`startup.py` resolves the already loaded `RevitMcp.Bridge` assembly and registers a disabled persistent IronPython delegate with ABI, build hash, engine/version, random generation, and self-test result. It never imports or enables pyRevit Routes.

Python ON enables the registered generation. Python OFF disables admission and asks the bridge to terminally cancel queued-not-started Python work. Fixed bridge tools and C# remain available. A pyRevit reload registers a new generation, so the bridge cancels queued old-generation requests instead of silently executing them on the new delegate.

IronPython source is compiled and cached on Revit's UI thread before a bridge-owned transaction opens. Results must be bounded JSON-safe primitives/containers; live Revit objects are rejected.

The controls use programmatic WPF green/red/grey icons and read tooltip/enabled state from `PythonRegistrationService.GetStatusJson()`. SVG source assets are included for review. Live Revit/pyRevit rendering is an explicit validation gate.

```powershell
./scripts/test.ps1
./scripts/package.ps1
./scripts/install.ps1
```

The default install target is `%APPDATA%\pyRevit\Extensions`; pass `-ExtensionsPath` for a configured user extension directory. No elevation is required.
