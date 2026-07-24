# AV/EDR runbook

The bridge opens one byte-mode named pipe under the current Windows user and writes atomic JSON discovery records under `%LOCALAPPDATA%\RevitMcp\instances`. It opens no network port and starts no child process.

Before rollout, submit the signed package hashes to the firm's security team. Capture product name/version/policy, whether pipe creation/connect is allowed, any alert ID, process tree, and per-hop timing. Never disable endpoint protection as a workaround.

If a client receives a `pipe_or_edr` error:

1. Confirm the Revit ribbon reports Bridge ON and the discovery PID/start identity matches the running process.
2. Confirm Revit and the MCP process use the same Windows user/integrity boundary.
3. Run `scripts/status.ps1` and inspect only bounded operational logs; source/model/results are not logged.
4. Ask security to review named-pipe events for the exact signed hashes and random pipe prefix `revit-mcp-`.
5. Record the outcome in the live validation JSONL. Do not enable HTTP or Routes fallback.

Crash-kill recovery is discovery-driven: stale records are rejected by PID/start identity and may be removed with `cleanup-discovery.ps1`. A clean shutdown removes discovery. Bridge/Python default OFF after restart.
