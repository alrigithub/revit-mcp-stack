from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import statistics
import time

from revit_mcp.client import BridgeClient


def percentile(values, percentile_value):
    if not values:
        return None
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round((percentile_value / 100) * (len(ordered) - 1))))
    return ordered[index]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    jsonl = args.output / "live-results.jsonl"
    records = []
    latencies = []
    client = BridgeClient()

    def run(name, action):
        started = time.perf_counter()
        try:
            response = action()
            passed = response.get("state") == "succeeded"
            error = response.get("error")
        except Exception as exc:
            response, passed, error = None, False, {"type": type(exc).__name__, "message": str(exc)}
        elapsed = (time.perf_counter() - started) * 1000
        latencies.append(elapsed)
        record = {"scenario": name, "timestamp_utc": dt.datetime.now(dt.timezone.utc).isoformat(), "verified": True, "passed": passed, "client_roundtrip_ms": elapsed, "response": response, "error": error}
        records.append(record)
        with jsonl.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, separators=(",", ":")) + "\n")
        return response or {}

    try:
        capabilities = run("capabilities", lambda: client.call(args.pid, "get_capabilities"))
        documents = run("list_documents", lambda: client.call(args.pid, "list_documents"))
        docs = ((documents.get("result") or {}).get("documents") or [])
        active = next((doc for doc in docs if doc.get("is_active")), docs[0] if docs else None)
        if active:
            session, generation = active["document_session"], active["document_generation"]
            run("warnings_read", lambda: client.call(args.pid, "get_warnings", {}, document_session=session, document_generation=generation, transaction_mode="read"))
            run("ironpython_read", lambda: client.call(args.pid, "run_python", {"source": "_result = {'ok': True}"}, document_session=session, document_generation=generation, transaction_mode="read"))
            run("csharp_read", lambda: client.call(args.pid, "run_csharp", {"source": "return JsonSerializer.Serialize(new { ok = true });"}, document_session=session, document_generation=generation, transaction_mode="read"))
        else:
            records.append({"scenario": "document_required", "verified": True, "passed": False, "error": "Open and activate a disposable validation model."})
    finally:
        client.close()

    summary = {
        "generated_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "pid": args.pid,
        "verified_passed": sum(1 for item in records if item.get("verified") and item.get("passed")),
        "verified_failed": sum(1 for item in records if item.get("verified") and not item.get("passed")),
        "client_roundtrip_ms": {"p50": percentile(latencies, 50), "p95": percentile(latencies, 95), "p99": percentile(latencies, 99)},
        "note": "Modal/busy behavior is not a latency promise. UI-dispatch and queue per-hop percentiles require the stress checklist/log export.",
    }
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    lines = ["# Live Revit baseline", "", f"- PID: {args.pid}", f"- Passed: {summary['verified_passed']}", f"- Failed: {summary['verified_failed']}", f"- Client roundtrip p50/p95/p99 ms: {summary['client_roundtrip_ms']}", "", "Only rows recorded by this run are verified. Complete LIVE-REVIT-CHECKLIST.md before certification."]
    (args.output / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return 0 if summary["verified_failed"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
