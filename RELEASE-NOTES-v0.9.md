# Revit MCP v0.9

V0.9 is the first live-tested Revit 2025 preview release.

## Included

- Per-user, no-elevation installation with reviewed prebuilt artifacts.
- Direct local named-pipe bridge with bounded Revit UI-thread dispatch.
- Explicit document binding and read/auto/manual/group transaction modes.
- Dynamic C# through isolated Roslyn and IronPython through pyRevit.
- Bridge ON/OFF and Python ON/OFF controls.
- Native Revit dockable Activity pane with bridge/provider/document/queue state and bounded recent activity.
- Activity pane follows Revit light/dark theme changes with matched native spacing, typography, controls, and status colors.
- Verified live creation, rollback, provider reload, view navigation, and Revit 2025 model automation.
- Saved tools: manifest+script pairs under `%LOCALAPPDATA%\RevitMcp\tools\`, grouped by subfolder, live at the next call.

## Not included yet

- External Windows UI Automation for add-in dialogs.
- Revit 2026/2027 live certification.

Close Revit, run `install-v0.9.ps1`, restart Revit, and use **Revit MCP > Activity** to open the pane.
