# Endpoint troubleshooting

The bridge uses a current-user named pipe (`revit-mcp-…`) and discovery records under `%LOCALAPPDATA%\RevitMcp\instances`. The bridge transport opens no network port. Scripts run with the host user's privileges and can use other APIs.

For `pipe_or_edr` errors, confirm Bridge ON, matching Windows user, and a live PID/start identity. Run the root `doctor.ps1`, then inspect bounded `get_logs_tail` output. Stale discovery can be removed with `scripts/cleanup-discovery.ps1`.

The package includes SHA-256 hashes and component SBOMs. This release is unsigned. Use those exact hashes and any alert ID when asking IT to review a block; do not disable endpoint protection or introduce an HTTP fallback.
