# pyRevit execution provider

Persistent IronPython 2.7 provider, tested with pyRevit 6.4. No additional Python packages or Routes server. Startup registers disabled; the native ribbon's Python button enables/reloads it.

Source edits need `sync.ps1` plus `reload_python_provider` or a Python OFF/ON cycle. The bounded compile cache is populated before bridge-owned transactions. Runtime failures return logical script lines; optional progress/cancellation hooks come from bridge contracts.

Run `scripts/test.ps1`; package with `scripts/package.ps1`. Colleagues install through the root release ZIP. [Operational notes](../GOTCHAS.md).
