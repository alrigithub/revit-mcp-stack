# Attach bespoke tools

No modeling catalog ships with the base bridge. Add a proven script and manifest to the root returned by `list_saved_tools` (default `%LOCALAPPDATA%\RevitMcp\tools`). Subfolders group tools. Files are read at call time; no bridge rebuild or reconnect is required.

Example `read_title.json`:

```json
{"manifest_version":1,"name":"read_title","description":"Return the active bound document title.","engine":"python","transaction_mode":"read","timeout_ms":30000,"params":[]}
```

Matching `read_title.py`:

```python
_result = {"title": doc.Title}
```

Use `engine: "csharp"` with a `.cs` entry body returning a JSON string when appropriate. Python is IronPython 2.7. Parameters reach scripts as `request` (Python) or `requestJson` (C#), never the outer transport envelope.

Names/group segments: lowercase letter followed by letters, digits or underscores, at most 64 characters. Description: 1–500 characters. Source: at most 100,000 bytes. Each parameter declares `name`, `type`, `description`, `required`, and optionally `default`; types are string/integer/number/boolean/array/object. Unknown arguments are rejected. The manifest owns transaction mode and timeout.

Disable a tool with `<name>.disabled`, a group with `.disabled`, or a root in Settings. Additional `saved_tools_paths` are searched in order after the primary root. First owner wins; disabled/invalid owners never fall through. These roots contain trusted executable code, not sandboxed documents.

Keep future company tools separately versioned and tested against representative models. Use `sync.ps1 -Tools` only when deliberately copying this repository's added tools into the local registry.
