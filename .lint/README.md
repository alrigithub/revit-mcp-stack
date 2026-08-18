# Revit Python checks

This is development-harness tooling, not part of the MCP runtime.

```powershell
./.lint/setup-stubs.ps1
```

`pyrightconfig.json` uses the Revit 2025 stubs for completion and static checks. Revit 2026 stubs are installed separately for compatibility work. Claude's post-edit hook runs Pyright when its CLI is available and always checks IronPython scripts for common Python 3-only syntax.
