# MCP 2.x Upgrade + Bridge Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade the Python MCP server from mcp 1.10.1 to 2.1.1 and add three probe-validated bridge/tooling improvements (edit-mode reporting, warning severity names, doctor.ps1 add-in health).

**Architecture:** The Python server port follows the probe-validated recipe in `docs/mcp-2.x-upgrade-plan.md` (rename + `context` param + `input_schema`). Bridge improvements use reflection probes in `RevitMcp.Core` (no compile-time dependency on post-2025 Revit APIs) so one net8 DLL serves 2025 and lights up on 2025.3+/2026+. All work lands on branch `mcp2-and-bridge-improvements` cut from `main`.

**Tech Stack:** Python 3.12 (bundled runtime at `%LOCALAPPDATA%\RevitMcp\mcp\runtime\Scripts\python.exe` — NEVER system Python), mcp 2.1.1, C# / net8.0-windows, PowerShell 7.

**Spec:** `docs/mcp-2.x-upgrade-plan.md` (Python tasks), `docs/revit-api-2026-2027-findings.md` §"Improvement opportunities" items 1, 3, 5 (bridge tasks), `docs/revit-dotnet10-2025-2026-impact.md` (constraint: TFM stays net8).

## Global Constraints

- TFM stays `net8.0-windows` for 2025/2026 — do NOT bump to net10 (blocked on machine updates, see dotnet10 impact doc).
- `mcp` pin is exact: `mcp==2.1.1` (no ranges — hash-locked dep posture).
- IronPython 2.7 dialect rules for anything under `saved-tools/` are unchanged by this plan.
- Bundled runtime for ALL Python commands: `$py = "$env:LOCALAPPDATA\RevitMcp\mcp\runtime\Scripts\python.exe"`.
- C# tests run via `revit-c-bridge/scripts/test.ps1` (custom runner, currently 12 tests); bridge compile via `revit-c-bridge/scripts/build.ps1 -RevitYear 2025`. Bridge behavior cannot be live-tested without a Revit restart — compile + unit tests are the gate; live smoke happens at the next install.
- Commit messages end with the repo's standard Claude co-author trailer.
- Work happens on branch `mcp2-and-bridge-improvements`; do not push until the final task.

---

### Task 0: Branch setup

**Files:** none (git only)

- [ ] **Step 1: Create the branch from main**

```bash
cd /c/Users/ritiv/Desktop/RevitMCP && git checkout main && git pull origin main && git checkout -b mcp2-and-bridge-improvements
```

Expected: `Switched to a new branch 'mcp2-and-bridge-improvements'`.

---

### Task 1: Port server.py to mcp 2.1.1 and bump the pin

**Files:**
- Modify: `revit-mcp/pyproject.toml:10`
- Modify: `revit-mcp/src/revit_mcp/server.py:8,52-63,66,250`
- Test: existing suite `revit-mcp/tests/` (no new test files; `tests/test_runtime_settings.py:79` already exercises the disabled-tool `call_tool` gate)

**Interfaces:**
- Consumes: mcp 2.1.1 `MCPServer` — `call_tool(self, name: str, arguments: dict, context=None)`, `list_tools(self)`, `Tool.input_schema` (probe-verified signatures).
- Produces: `ConfigurableMCPServer` class (renamed from `ConfigurableFastMCP`); module-level `mcp` instance unchanged in name; `write_tools_manifest()` behavior unchanged (same JSON shape).

- [ ] **Step 1: Install mcp 2.1.1 into the bundled runtime**

```powershell
$py = "$env:LOCALAPPDATA\RevitMcp\mcp\runtime\Scripts\python.exe"
& $py -m pip install "mcp==2.1.1"
& $py -c "import mcp; from mcp.server import MCPServer; print('mcp 2.1.1 ok')"
```

Expected: `mcp 2.1.1 ok`. (The MCP server process currently running keeps old modules in memory; deployed source is synced in Task 7.)

- [ ] **Step 2: Run the suite to see the current state fail against 2.1.1**

```powershell
cd revit-mcp; $env:PYTHONPATH = "src"; & $py -m unittest discover -s tests
```

Expected: FAIL/ERROR — `ModuleNotFoundError: No module named 'mcp.server.fastmcp'` (the v1 import is gone in 2.x). This is the failing-test baseline for the port.

- [ ] **Step 3: Port server.py (three edits)**

Edit 1 — `revit-mcp/src/revit_mcp/server.py:8`:

```python
# before
from mcp.server.fastmcp import FastMCP
# after
from mcp.server import MCPServer
```

Edit 2 — the subclass (`server.py:52-63`). Replace the whole class and the constructor call at line 66:

```python
class ConfigurableMCPServer(MCPServer):
    async def list_all_tools(self):
        return await super().list_tools()

    async def list_tools(self):
        disabled = load_settings().disabled_mcp_tools
        return [tool for tool in await self.list_all_tools() if tool.name not in disabled]

    async def call_tool(self, name: str, arguments: dict[str, Any], context=None):
        # v2 added the third `context` parameter; it must be accepted and forwarded
        if name in load_settings().disabled_mcp_tools:
            raise PermissionError("MCP tool %r is disabled in Revit MCP settings" % name)
        return await super().call_tool(name, arguments, context)


mcp = ConfigurableMCPServer("revit-mcp-local", instructions=AGENT_INSTRUCTIONS + "\n" + _environment_note())
```

Edit 3 — `write_tools_manifest` (`server.py:250`), snake_case rename:

```python
# before
"params": list((tool.inputSchema or {}).get("properties", {}))} for tool in tools],
# after
"params": list((tool.input_schema or {}).get("properties", {}))} for tool in tools],
```

- [ ] **Step 4: Bump the pin**

`revit-mcp/pyproject.toml:10`:

```toml
dependencies = ["mcp==2.1.1"]
```

- [ ] **Step 5: Run all Python suites**

```powershell
cd revit-mcp; $env:PYTHONPATH = "src"; & $py -m unittest discover -s tests
cd ../revit-pyrevit-extention; & $py -m unittest discover -s tests
```

Expected: all PASS, zero failures. If `test_runtime_settings.py:79` fails on the `call_tool` signature, the override in Step 3 doesn't match `super()` — fix the override, not the test.

- [ ] **Step 6: Verify the manifest writer end-to-end**

```powershell
cd revit-mcp; $env:PYTHONPATH = "src"
& $py -c "from revit_mcp.server import write_tools_manifest; import json; p = write_tools_manifest(); d = json.load(open(p, encoding='utf-8')); assert d['tools'] and all(t['params'] or t['name'] in ('list_revit_instances','list_saved_tools') for t in d['tools']), d; print('manifest ok:', len(d['tools']), 'tools')"
```

Expected: `manifest ok: 22 tools` (count must be > 0 and `params` populated — this catches a botched `input_schema` rename, which would yield empty params everywhere).

- [ ] **Step 7: Commit**

```bash
git add revit-mcp/pyproject.toml revit-mcp/src/revit_mcp/server.py
git commit -m "Upgrade MCP SDK to 2.1.1: MCPServer port, context param, input_schema"
```

---

### Task 2: Regenerate requirements.lock for mcp 2.1.1

**Files:**
- Modify: `revit-mcp/requirements.lock` (fully regenerated)

**Interfaces:**
- Consumes: mcp==2.1.1 from Task 1.
- Produces: hash-locked `requirements.lock` consumed verbatim by `revit-mcp/scripts/package.ps1` (`pip download --require-hashes` then `pip install --no-index --require-hashes`).

- [ ] **Step 1: Generate the lock from pip's install report**

```powershell
$py = "$env:LOCALAPPDATA\RevitMcp\mcp\runtime\Scripts\python.exe"
cd revit-mcp
& $py -m pip install --dry-run --ignore-installed --quiet --report "$env:TEMP\mcp2-report.json" "mcp==2.1.1"
& $py -c @"
import json
report = json.load(open(r'$env:TEMP\mcp2-report.json', encoding='utf-8'))
rows = []
for item in report['install']:
    meta = item['metadata']
    sha = item['download_info']['archive_info']['hashes']['sha256']
    rows.append((meta['name'].lower(), '%s==%s --hash=sha256:%s' % (meta['name'].lower(), meta['version'], sha)))
rows.sort(key=lambda r: (r[0] != 'mcp', r[0]))  # mcp first, then alphabetical
header = [
    '# Windows x64 / CPython 3.12 lock. Generated from pip''s install report on 2026-08-31.',
    '# mcp 2.x: pywin32 is an accepted Windows dependency; bump the pin deliberately and regenerate this file.',
]
open('requirements.lock', 'w', encoding='utf-8', newline='\n').write('\n'.join(header + [r[1] for r in rows]) + '\n')
print('wrote', len(rows), 'locked packages')
"@
```

Expected: `wrote N locked packages` with N ≥ 15 (mcp 2.1.1 pulls mcp-types, httpx2, jsonschema, opentelemetry-api, pyjwt, cryptography, python-multipart, pywin32, sse-starlette, starlette, uvicorn, pydantic, anyio, typing-extensions, typing-inspection and their deps).

- [ ] **Step 2: Verify the lock is installable exactly as package.ps1 uses it**

```powershell
& $py -m pip download --only-binary=:all: --require-hashes -r requirements.lock -d "$env:TEMP\mcp2-wheelhouse"
```

Expected: exit 0, every wheel downloads with hash verification. If any package has no wheel for win/cp312, pip fails here — resolve by pinning a nearby version of that dependency in the lock, never by dropping `--only-binary`.

- [ ] **Step 3: Sanity-check the lock content**

```powershell
Select-String -Path requirements.lock -Pattern '^mcp==2\.1\.1 ', '^pywin32==' | ForEach-Object Line
```

Expected: both lines print (mcp pinned at 2.1.1; pywin32 present with a hash).

- [ ] **Step 4: Commit**

```bash
git add revit-mcp/requirements.lock
git commit -m "Regenerate requirements.lock for mcp 2.1.1 (pywin32 accepted)"
```

---

### Task 3: Update CLAUDE.md pin policy and Dependabot ignore

**Files:**
- Modify: `CLAUDE.md` (the `revit-mcp/` bullet in "What this is")
- Modify: `.github/dependabot.yml` (pip ignore block)

**Interfaces:**
- Consumes: nothing from other tasks (text-only).
- Produces: policy text other agents read; Dependabot resumes proposing `mcp` updates.

- [ ] **Step 1: Replace the stale pin note in CLAUDE.md**

Find (in the `revit-mcp/` bullet):

```
MCP SDK intentionally pinned to `mcp==1.10.1` (1.11+ drags in pywin32); don't upgrade casually.
```

Replace with:

```
MCP SDK pinned exactly (`mcp==2.1.1`); pywin32 is an accepted Windows dependency since 2.x. Bump deliberately: change the pin, regenerate requirements.lock from pip's install report, run the suite.
```

- [ ] **Step 2: Remove the mcp ignore from .github/dependabot.yml**

Delete these lines from the pip block (keep the block itself):

```yaml
    ignore:
      # mcp is intentionally pinned to 1.10.1 — 1.11+ pulls in pywin32,
      # which the pure-ctypes pipe client deliberately avoids.
      - dependency-name: "mcp"
```

The pip entry keeps `package-ecosystem`, `directory`, and `schedule` only.

- [ ] **Step 3: Validate the YAML still parses**

```powershell
$py = "$env:LOCALAPPDATA\RevitMcp\mcp\runtime\Scripts\python.exe"
& $py -c "import json,sys; import yaml" 2>$null; if ($LASTEXITCODE -ne 0) { & $py -c "text = open('.github/dependabot.yml').read(); assert 'ignore' not in text.split('pip')[1]; print('pip ignore removed')" } else { & $py -c "import yaml; d = yaml.safe_load(open('.github/dependabot.yml')); pip = [u for u in d['updates'] if u['package-ecosystem']=='pip'][0]; assert 'ignore' not in pip; print('yaml ok, pip ignore removed')" }
```

Expected: `pip ignore removed` (or `yaml ok, ...` if PyYAML is present).

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md .github/dependabot.yml
git commit -m "CLAUDE.md + dependabot: mcp pin policy moves to 2.1.1"
```

---

### Task 4: ReflectionProbes in Core with tests

**Files:**
- Create: `revit-c-bridge/src/RevitMcp.Core/ReflectionProbes.cs`
- Modify: `revit-c-bridge/tests/RevitMcp.Core.Tests/Program.cs` (tests array + 2 test methods + fake types at file bottom)

**Interfaces:**
- Consumes: nothing (pure reflection, no Revit references — Core must stay Revit-free).
- Produces: `RevitMcp.Core.ReflectionProbes` with EXACTLY these signatures, used by Tasks 5 and 6:
  - `public static string? ActiveEditMode(object? document)` — mode name (e.g. `"None"`, `"SketchEdit"`); `"unknown"` when only the bool API exists and reports editing; `null` when the document is null or neither API exists (pre-2025.3 Revit).
  - `public static string? FailureSeverityName(Type? labelUtilsType, object? severity)` — localized severity name via static `GetFailureSeverityName`; `null` when the method is absent (pre-2026 Revit).

- [ ] **Step 1: Add the two failing tests**

In `Program.cs`, append to the `tests` array (after `("coordinator retains work after denied raise", LostWakeup)`):

```csharp
    ("edit mode probe adapts to available document APIs", EditModeProbeTest),
    ("failure severity probe uses LabelUtils when present", SeverityProbeTest)
```

Add the test methods next to the other `static` methods:

```csharp
static Task EditModeProbeTest()
{
    Equal("SketchEdit", ReflectionProbes.ActiveEditMode(new FakeDocumentWithEditMode()));
    Equal("None", ReflectionProbes.ActiveEditMode(new FakeDocumentBoolProbe(false)));
    Equal("unknown", ReflectionProbes.ActiveEditMode(new FakeDocumentBoolProbe(true)));
    True(ReflectionProbes.ActiveEditMode(new object()) is null);
    True(ReflectionProbes.ActiveEditMode(null) is null);
    return Task.CompletedTask;
}

static Task SeverityProbeTest()
{
    Equal("Localized Warning", ReflectionProbes.FailureSeverityName(typeof(FakeLabelUtils), FakeSeverity.Warning));
    True(ReflectionProbes.FailureSeverityName(typeof(object), FakeSeverity.Warning) is null);
    True(ReflectionProbes.FailureSeverityName(null, FakeSeverity.Warning) is null);
    True(ReflectionProbes.FailureSeverityName(typeof(FakeLabelUtils), null) is null);
    return Task.CompletedTask;
}
```

Add the fake types at the bottom of `Program.cs` with the other helper classes:

```csharp
enum FakeSeverity { Warning }

sealed class FakeDocumentWithEditMode
{
    public string GetActiveEditMode() => "SketchEdit";
}

sealed class FakeDocumentBoolProbe(bool inEdit)
{
    public bool IsInEditMode() => inEdit;
}

static class FakeLabelUtils
{
    public static string GetFailureSeverityName(FakeSeverity severity) => severity == FakeSeverity.Warning ? "Localized Warning" : "?";
}
```

- [ ] **Step 2: Run tests to verify they fail**

```powershell
cd revit-c-bridge; ./scripts/test.ps1
```

Expected: compile error `The name 'ReflectionProbes' does not exist` — the failing state for a not-yet-written class.

- [ ] **Step 3: Implement ReflectionProbes**

Create `revit-c-bridge/src/RevitMcp.Core/ReflectionProbes.cs`:

```csharp
namespace RevitMcp.Core;

// Reflection guards for Revit APIs newer than the compile-time reference (2025).
// A null result means "this Revit build does not have the API" — callers keep
// their 2025 behavior and light up on 2025.3+/2026+ without a rebuild.
public static class ReflectionProbes
{
    // Document.GetActiveEditMode() (2025.3+) returns EditModeType; falls back to
    // Document.IsInEditMode() (also 2025.3+, kept separate in case one is trimmed).
    public static string? ActiveEditMode(object? document)
    {
        if (document is null) return null;
        try
        {
            var type = document.GetType();
            var get = type.GetMethod("GetActiveEditMode", Type.EmptyTypes);
            if (get is not null) return get.Invoke(document, null)?.ToString();
            if (type.GetMethod("IsInEditMode", Type.EmptyTypes)?.Invoke(document, null) is bool inEdit)
                return inEdit ? "unknown" : "None";
        }
        catch
        {
            // A probe must never take down a request; absence and failure look the same.
        }
        return null;
    }

    // LabelUtils.GetFailureSeverityName(FailureSeverity) (2026+).
    public static string? FailureSeverityName(Type? labelUtilsType, object? severity)
    {
        if (labelUtilsType is null || severity is null) return null;
        try
        {
            var method = labelUtilsType.GetMethod("GetFailureSeverityName", [severity.GetType()]);
            return method?.Invoke(null, [severity])?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```powershell
cd revit-c-bridge; ./scripts/test.ps1
```

Expected: `RESULT total=14 passed=14 failed=0`.

- [ ] **Step 5: Commit**

```bash
git add revit-c-bridge/src/RevitMcp.Core/ReflectionProbes.cs revit-c-bridge/tests/RevitMcp.Core.Tests/Program.cs
git commit -m "Core: reflection probes for edit mode (2025.3+) and severity names (2026+)"
```

---

### Task 5: Report edit_mode from get_active_context

**Files:**
- Modify: `revit-c-bridge/src/RevitMcp.Bridge/RevitRequestHandler.cs:196`
- Modify: `revit-mcp/src/revit_mcp/server.py:102` (docstring only)

**Interfaces:**
- Consumes: `ReflectionProbes.ActiveEditMode(object?)` from Task 4 (returns `string?`).
- Produces: `get_active_context` response gains `edit_mode` (string or null). Agents read: `"None"` = safe to mutate; any other string = an edit mode is open (family/sketch/group editing — mutations will misbehave, see GOTCHAS); null = Revit build predates the API.

- [ ] **Step 1: Wire the probe into ActiveContext**

`RevitRequestHandler.cs:196` — the active-document return in `ActiveContext`:

```csharp
// before
return new { active = true, document = runtime.Documents.Describe(document, true), view_id = uidoc.ActiveView.Id.Value, view_name = uidoc.ActiveView.Name };
// after
return new { active = true, document = runtime.Documents.Describe(document, true), view_id = uidoc.ActiveView.Id.Value, view_name = uidoc.ActiveView.Name, edit_mode = ReflectionProbes.ActiveEditMode(document) };
```

`RevitMcp.Bridge` already references `RevitMcp.Core`; add `using RevitMcp.Core;` only if the file lacks it (check the header — `RequestLedger` etc. are already used, so it is present).

- [ ] **Step 2: Compile**

```powershell
cd revit-c-bridge; ./scripts/build.ps1 -RevitYear 2025
```

Expected: build succeeds, zero warnings introduced.

- [ ] **Step 3: Teach agents about the field (Python docstring)**

`revit-mcp/src/revit_mcp/server.py:102`, extend `get_active_context`'s docstring:

```python
    """Return the active Revit document/view context. `edit_mode` reports Revit's active edit mode: "None" means no edit mode is open; any other value (family/sketch/group editing) means mutations will misbehave until the user exits it; null means this Revit build (pre-2025.3) cannot report it."""
```

- [ ] **Step 4: Run the Python suite (docstring edits can still break imports)**

```powershell
$py = "$env:LOCALAPPDATA\RevitMcp\mcp\runtime\Scripts\python.exe"
cd revit-mcp; $env:PYTHONPATH = "src"; & $py -m unittest discover -s tests
```

Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add revit-c-bridge/src/RevitMcp.Bridge/RevitRequestHandler.cs revit-mcp/src/revit_mcp/server.py
git commit -m "Bridge: report edit_mode in get_active_context via reflection probe"
```

---

### Task 6: Localized severity names in get_warnings

**Files:**
- Modify: `revit-c-bridge/src/RevitMcp.Bridge/VerificationService.cs:41-47`

**Interfaces:**
- Consumes: `ReflectionProbes.FailureSeverityName(Type?, object?)` from Task 4.
- Produces: each warning DTO gains `severity_name` (string or null). Existing `severity` (enum name) is unchanged — consumers keep working.

- [ ] **Step 1: Add severity_name to the warning DTO**

`VerificationService.cs:41-47`:

```csharp
public static object[] Warnings(Document document) => document.GetWarnings().Take(1000).Select(w => (object)new
{
    failure_definition_id = w.GetFailureDefinitionId().Guid.ToString("D"),
    severity = w.GetSeverity().ToString(),
    severity_name = ReflectionProbes.FailureSeverityName(typeof(LabelUtils), w.GetSeverity()),
    description = w.GetDescriptionText(),
    involved_element_ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().Take(200).Select(id => id.Value).ToArray()
}).ToArray();
```

Add `using RevitMcp.Core;` to the file header if not present. `LabelUtils` is in `Autodesk.Revit.DB`, already imported in this file.

- [ ] **Step 2: Compile and run C# tests**

```powershell
cd revit-c-bridge; ./scripts/build.ps1 -RevitYear 2025; ./scripts/test.ps1
```

Expected: build OK; `RESULT total=14 passed=14 failed=0` (the DTO-bounds test covers Core DTOs, not this anonymous type — no test changes needed).

- [ ] **Step 3: Commit**

```bash
git add revit-c-bridge/src/RevitMcp.Bridge/VerificationService.cs
git commit -m "Bridge: severity_name in get_warnings via LabelUtils probe (2026+)"
```

---

### Task 7: doctor.ps1 add-in manager health check

**Files:**
- Modify: `doctor.ps1` (insert a new section after the "Bridge install" section, i.e. after line 18)

**Interfaces:**
- Consumes: `Autodesk.RevitAddIns.Manager.AddInsManagerSettings` from `RevitAddInUtility.dll` (2025.3+; every probe guarded).
- Produces: console section "Add-in manager"; no exit-code change (doctor stays read-only, `$ErrorActionPreference = 'Stop'` respected via try/catch).

- [ ] **Step 1: Insert the section**

After the three `Show` lines of the "Bridge install" section (after line 18), insert:

```powershell
Write-Host 'Add-in manager (Revit 2025.3+)'
$addinUtility = Join-Path $env:ProgramFiles "Autodesk/Revit $RevitYear/RevitAddInUtility.dll"
if (Test-Path -LiteralPath $addinUtility) {
    try {
        Add-Type -LiteralPath $addinUtility -ErrorAction Stop
        $managerType = [Type]::GetType('Autodesk.RevitAddIns.Manager.AddInsManagerSettings, RevitAddInUtility')
        if ($null -eq $managerType) {
            Write-Host '  add-in manager API not in this Revit build (pre-2025.3); skipped.'
        }
        else {
            $manager = $managerType::Get()
            if ($manager.DisableAllAddIns) { Write-Host '  ! DisableAllAddIns is ON - NO add-ins load next session.' }
            $items = @($manager.GetAllAddInItemSettings() | Where-Object { $_.Name -match 'RevitMcp|3XN' })
            if ($items.Count -eq 0) {
                Write-Host '  bridge not registered with the add-in manager yet (normal before its first load).'
            }
            foreach ($item in $items) {
                $state = if ($item.Disabled) { 'DISABLED' } else { 'enabled ' }
                Write-Host ("  {0} {1} (vendor {2}, last load {3})" -f $state, $item.Name, $item.Vendor, $item.LoadTime)
            }
        }
    }
    catch { Write-Host "  add-in manager query failed: $($_.Exception.Message)" }
}
else {
    Write-Host "  RevitAddInUtility.dll not found for Revit $RevitYear; skipped."
}
```

- [ ] **Step 2: Run doctor to verify the section renders and nothing else regressed**

```powershell
cd /c/Users/ritiv/Desktop/RevitMCP  # repo root, PowerShell: cd C:\Users\ritiv\Desktop\RevitMCP
./doctor.ps1
```

Expected: the new "Add-in manager" section prints one of its guarded outcomes (a bridge row, "not registered", "not in this Revit build", or "not found"), every pre-existing section still prints, exit code 0. Any thrown error = the guard is wrong; fix the try/catch, don't loosen `$ErrorActionPreference`.

- [ ] **Step 3: Commit**

```bash
git add doctor.ps1
git commit -m "doctor.ps1: report bridge add-in disabled state and load time"
```

---

### Task 8: Full verification, deploy sync, merge, push

**Files:**
- Modify: `docs/mcp-2.x-upgrade-plan.md` (status note at top)

**Interfaces:**
- Consumes: everything above.
- Produces: merged main; deployed MCP source in sync with the upgraded runtime.

- [ ] **Step 1: Run every suite from a clean state**

```powershell
$py = "$env:LOCALAPPDATA\RevitMcp\mcp\runtime\Scripts\python.exe"
cd revit-c-bridge; ./scripts/build.ps1 -RevitYear 2025; ./scripts/test.ps1
cd ../revit-mcp; $env:PYTHONPATH = "src"; & $py -m unittest discover -s tests
cd ../revit-pyrevit-extention; & $py -m unittest discover -s tests
```

Expected: C# `RESULT total=14 passed=14 failed=0`; both Python suites all PASS.

- [ ] **Step 2: Sync deployed copies (CRITICAL — runtime already has mcp 2.1.1, deployed server.py must match)**

```powershell
cd C:\Users\ritiv\Desktop\RevitMCP; ./sync.ps1; ./doctor.ps1
```

Expected: sync copies `revit_mcp` sources; doctor reports "revit-mcp server: in sync." Without this step, the next MCP client start runs old FastMCP-importing source against the 2.x runtime and crashes.

- [ ] **Step 3: Mark the upgrade plan done**

Add under the title of `docs/mcp-2.x-upgrade-plan.md`:

```markdown
> **STATUS (2026-08-31): DONE** — executed via `docs/superpowers/plans/2026-08-31-mcp2-and-bridge-improvements.md`. The rollout checklist below is kept for reference; CLAUDE.md carries the current pin policy.
```

- [ ] **Step 4: Commit, merge to main, push**

```bash
git add docs/mcp-2.x-upgrade-plan.md
git commit -m "docs: mark mcp 2.x upgrade plan executed"
git checkout main && git merge --no-ff mcp2-and-bridge-improvements -m "Merge mcp 2.1.1 upgrade + bridge improvements"
git push origin main
git checkout Reope && git merge --ff-only main
```

Expected: merge commit on main, pushed; Reope fast-forwarded. Note for the operator: restart any running MCP client session to pick up mcp 2.1.1 + new server source; bridge DLL changes reach Revit at the next package + install (Revit closed).

---

## Self-review notes

- Spec coverage: upgrade-plan doc steps 1–7 map to Tasks 1,2,3,8 (step 2 hash lock → Task 2; step 6 CLAUDE.md → Task 3; step 7 dependabot ignore removal → Task 3). Findings doc items #1/#3/#5 map to Tasks 5/6/7. Findings item #4 (SetTitle) is 2027-only and item #2 (isolation) needs a live pyRevit test — both intentionally out of scope, matching the findings doc's own sequencing.
- Version bump / sbom: per CLAUDE.md versioning policy these move at packaging milestones (`package.ps1`), not in this plan — the operator packages when ready to install into Revit.
- Type consistency: `ReflectionProbes.ActiveEditMode(object?)` and `FailureSeverityName(Type?, object?)` are defined in Task 4 and consumed with identical signatures in Tasks 5 and 6. `ConfigurableMCPServer` name used only in Task 1.
