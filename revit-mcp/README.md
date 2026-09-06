# MCP server

Python 3.12 stdio server exposing the [built-in tools](../docs/tools.md). Named-pipe I/O uses a model lane and a separate status/control lane. The locked MCP SDK supplies the client-facing protocol.

`./scripts/test.ps1` runs unit tests. `./scripts/package.ps1` creates a portable embedded runtime from `runtime-lock.json` and `requirements.lock`, checks imports and real stdio tool discovery, and hashes the package. Builds need an installed bundled runtime or explicit `-BuildPython`; end-user installation needs no Python or network download.

`validation/live_release.py` runs focused contracts/capture tests through actual MCP stdio. Pass the exact path of an open disposable test model and an output directory outside the repository. See [CLAUDE.md](../CLAUDE.md).
