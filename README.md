# 3XN RevitMCP

A small local bridge between Claude Code and Revit. Agents can inspect a model, run C# or Python, and get useful results, errors and PNGs. Company tools attach separately without rebuilding the bridge.

## Install for colleagues

1. Install **Revit 2025**, **Claude Code**, and **pyRevit 6.4** for Python support. Windows x64 is required.
2. Extract `RevitMcp-v0.2.0-Revit2025.zip` to a local folder. Close Revit and Claude Code.
3. Double-click **Install.cmd**. It installs for the current user and registers the `revit` MCP server in Claude Code. No administrator access, separate Python installation or dependency download is needed.
4. Open Revit. If Revit asks about the unsigned 3XN add-in, load it. In **3XN RevitMCP**, turn **Bridge ON** and **Python ON**. These reset to OFF each Revit session.
5. Open Claude Code. Ask it to list Revit instances and inspect the open model.

For agent-written modeling scripts, enable **Settings → Allow arbitrary code**. Fresh installs leave this OFF; built-in inspection/capture tools and enabled saved tools work without it. Run only trusted scripts: dynamic code runs with your Revit/Windows permissions.

The built release ZIP is in `dist/` in the prepared repository. A source-only clone needs a release build before installation; colleagues should use the ZIP.

## Everyday use

- **Activity** shows requests and changes. **Settings** controls execution policy and tool visibility.
- **Building Overview** creates four elevations and four axonometrics as two labelled PNG sheets.
- **Floor Views** captures selected floors with their associated elements.
- **Inspect Selection** shows a close-up with context, an optional isolated view, and middle cuts.
- **Section Views** creates horizontal or long/short vertical cuts.

The ribbon helpers also work with the bridge and Python OFF. They create reusable views named `3XN MCP - …` and PNGs under `%LOCALAPPDATA%\RevitMcp\captures`. The working view and selection stay in place. Save the model to retain helper views.

The agent decides what needs checking. An ordinary edit does not trigger an automatic model-wide audit. See [tools and contracts](docs/tools.md).

## Troubleshooting and removal

Run `./doctor.ps1` in PowerShell. If discovery is empty, turn Bridge ON. If Python is unavailable, check pyRevit and turn Python ON. If a request is queued, finish any Revit dialog/edit mode; resolve its status before retrying a mutation.

If Claude Code was installed later, run `./scripts/register-claude.ps1`. Other MCP clients can use `%LOCALAPPDATA%\RevitMcp\mcp\client-config.json`.

To remove: close Revit/Claude Code and run `./uninstall.ps1`. Settings, captures and custom tools remain. Remove Claude's registration with `claude mcp remove --scope user revit`.

## Maintain and extend

[Release scope](docs/release.md) · [Tool contract](docs/tools.md) · [Third-party notices](THIRD-PARTY.md)

Source maintainers: start with `CLAUDE.md`; add bespoke scripts using `saved-tools/README.md`. The bridge uses same-user named pipes with a per-process nonce; no bridge network listener or telemetry exporter. Your chosen AI client's data policies still apply.

The core is designed to change rarely. Autodesk compatibility, defects, dependency security fixes and MCP client changes can still require updates.
