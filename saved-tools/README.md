# saved-tools

Prebuilt saved tools for the Revit MCP stack. A saved tool is a proven script promoted to a named MCP tool: the assistant calls it through `run_saved_tool` instead of pasting the script into `run_python` every time. No rebuild, no reconnect — the files on disk are the tool.

This folder is the repo copy. Tools only run from the deployed root, `%LOCALAPPDATA%\RevitMcp\tools\` by default (change it in **Activity → Settings**).

## Deploy

From the repo root:

```powershell
./sync.ps1 -Tools
```

Or copy the folders into `%LOCALAPPDATA%\RevitMcp\tools\` by hand. Either way, new or changed tools are live at the next call.

## The contract

Each tool is two files with the same stem: a manifest (`name.json`) and a script (`name.py` for IronPython 2.7, `name.cs` for Roslyn C#). Validation lives in `revit-mcp/src/revit_mcp/saved_tools.py`; the manifest needs:

- `manifest_version`: `1`
- `name`: must equal the filename stem, lowercase `[a-z][a-z0-9_]*`, max 64 chars
- `description`: non-empty, max 500 chars — this is what the assistant reads, so say what the tool does and returns
- `engine`: `python` or `csharp`
- `transaction_mode`: `read`, `auto`, `manual`, or `group` — pinned here, the caller cannot override it
- `timeout_ms`: 1–600000 (default 30000)
- `params`: list of `{name, type, description, required}` (optional `default`); types are `string`, `integer`, `number`, `boolean`, `array`, `object`

Validated params reach the script as its `request` object. Python scripts follow the IronPython 2.7 dialect: no f-strings, `%`/`.format()` only, JSON-safe `_result`, `int()` around `ElementId.Value`.

## Multiple roots

Besides the primary root, `%LOCALAPPDATA%\RevitMcp\settings.json` can list extra read-only search paths, pyRevit-style:

```json
{
  "saved_tools_root": "C:\\Users\\me\\AppData\\Local\\RevitMcp\\tools",
  "saved_tools_paths": ["D:\\team\\revit-tools", "\\\\server\\share\\revit-tools"]
}
```

- Search order is `saved_tools_root` first, then `saved_tools_paths` in listed order.
- On duplicate tool IDs the first root wins; `list_saved_tools` reports the hidden copies under `shadowed`.
- A disabled tool does not fall through to a later root's copy — disabling always disables.
- New tools are still created in the primary root; that's also where `sync.ps1 -Tools` deploys and what **Activity → Settings** edits. Extra paths are edited in `settings.json` directly.

## Groups and enable/disable

- Subfolders are groups (`annotation/tag_rooms` is tool `tag_rooms` in group `annotation`); folder names follow the same name pattern.
- `name.disabled` next to a manifest disables that one tool.
- A `.disabled` file inside a group folder disables everything below it.
- **Activity → Saved** in Revit toggles both without touching files by hand.

## Ported tools

Ported from mcp-servers-for-revit (MIT License, (c) 2026 sparx-fire) and rewritten for IronPython 2.7 and this stack's saved-tool contract.

**Status: lint-clean, not yet live-tested.** These pass the IronPython 2.7 lint check (`.lint/check_ipy27.py`) and load cleanly through `saved_tools.py` validation, but have not been run against a live Revit model. Test on a disposable model first. License text for the ported logic: [LICENSE-THIRD-PARTY.md](LICENSE-THIRD-PARTY.md).

| Tool | Mode | What it does |
| --- | --- | --- |
| `data_extraction/material_quantities` | read | Material takeoff: per-material area_m2/volume_m3/element_count across the model, one category (OST_ or plain name), or the current selection, sorted by area with totals; paint materials excluded. |
| `data_extraction/model_statistics` | read | Model overview in one pass: element/type/family/view/sheet totals, per-category and per-level counts (elevation in mm), health numbers (warnings, Revit links, CAD imports), optional top-50 family/type breakdown via `include_types`. |
| `data_extraction/export_room_data` | read | All placed rooms with id, name, number, level, area_m2, volume_m3, perimeter_mm, unbounded_height_mm, department, occupancy, phase, comments; `include_unplaced` adds zero-area rooms and `level` filters by exact level name. |
| `annotation/tag_rooms` | auto | Tags every placed room visible in the active view at its location point, skipping already-tagged and unplaced rooms; optional leader and explicit tag type; returns tag ids with room name/number and locations in mm. |
| `annotation/tag_walls` | auto | Tags every wall visible in the active view at its midpoint with a horizontal IndependentTag, skipping walls already tagged; falls back from wall tags to multi-category tags; returns tag/wall ids and locations in mm. |
| `annotation/color_splash` | auto | Colors elements of one category in the active view with a distinct solid-fill override per value of a named parameter (instance first, then type), deterministic palette; `reset=true` clears the overrides instead. |
| `annotation/create_dimensions` | auto | Creates one linear dimension across two or more elements in the active view, auto-picking the planar face best aligned with the dimension direction per element, with the line offset perpendicular by `offset_mm`. |
| `architecture/create_grid` | auto | Creates a rectangular grid system from bay-spacing arrays (mm) in two directions with alphabetic/numeric naming inferred from start labels, duplicate-name suffixing, and configurable origin/extension; returns ids, names, and positions. |
| `architecture/create_framing_system` | auto | Creates a BeamSystem filling a rectangular boundary (mm) on a level with fixed center-to-center spacing along X or Y, beam type by substring with auto-fallback and justification option; returns beam system id, member beam ids, and count. |
