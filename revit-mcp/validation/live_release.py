"""Focused release validation through real MCP stdio. Use a disposable saved model copy."""
from __future__ import annotations
import argparse
import asyncio
import json
import sys
import uuid
from pathlib import Path
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def run(args):
    args.output.mkdir(parents=True, exist_ok=True)
    parameters = StdioServerParameters(command=sys.executable, args=["-B", "-m", "revit_mcp.server"])
    results = []
    binding = {}
    async with stdio_client(parameters) as (reader, writer):
        async with ClientSession(reader, writer) as session:
            await session.initialize()

            async def call(name, values=None, label=None):
                payload = {**binding, **(values or {})}
                if name in {"list_revit_instances"}:
                    payload = values or {}
                elif name in {"get_capabilities", "list_documents", "get_active_context", "get_request_status"}:
                    payload = {"pid": binding.get("pid"), **(values or {})}
                result = await session.call_tool(name, payload, read_timeout_seconds=650)
                value = result.structured_content
                if value is None:
                    value = json.loads(next(c.text for c in result.content if c.type == "text"))
                if isinstance(value, dict) and set(value) == {"result"}:
                    value = value["result"]
                if label:
                    (args.output / (label + ".json")).write_text(json.dumps(value, indent=2), encoding="utf-8")
                return value, sum(c.type == "image" for c in result.content)

            def check(name, passed, detail=None):
                results.append({"name": name, "passed": bool(passed), "detail": detail})
                (args.output / ("checks-" + args.phase + ".json")).write_text(json.dumps(results, indent=2), encoding="utf-8")
                print(("PASS " if passed else "FAIL ") + name, flush=True)
                if not passed:
                    raise RuntimeError(name + ": " + str(detail))

            def success(value):
                if value.get("state") != "succeeded":
                    raise RuntimeError(json.dumps(value))
                return value["result"]

            instances, _ = await call("list_revit_instances")
            candidates = [i for i in instances if args.pid is None or i["pid"] == args.pid]
            if len(candidates) != 1:
                raise RuntimeError("Choose exactly one live Revit process using --pid.")
            binding["pid"] = candidates[0]["pid"]
            caps, _ = await call("get_capabilities", label="capabilities")
            check("loaded release", success(caps)["product_version"] == args.version, caps)
            docs, _ = await call("list_documents")
            doc = next((d for d in success(docs)["documents"] if Path(d["path"]).resolve() == args.model.resolve()), None)
            check("explicit disposable model binding", doc is not None)
            binding.update(document_session=doc["document_session"], document_generation=doc["document_generation"])
            context, _ = await call("get_active_context")
            view_before = success(context)["view_id"]
            facts, _ = await call("run_csharp", {"transaction_mode": "read", "label": "Read release fixture IDs", "source":
                "return JsonSerializer.Serialize(new { panels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_CurtainWallPanels).WhereElementIsNotElementType().Take(3).Select(e => e.Id.Value).ToArray(), columns = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().Take(2).Select(e => e.Id.Value).ToArray(), levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Take(2).Select(l => l.Id.Value).ToArray(), selection = uidoc!.Selection.GetElementIds().Select(id => id.Value).ToArray() });"}, label="fixture")
            fixture = success(facts)
            if args.phase == "contracts":
                q, _ = await call("query_elements", {"include_types": True, "limit": 2, "fields": ["identity"]}, "query")
                first = success(q)
                q2, _ = await call("query_elements", {"include_types": True, "limit": 2, "after_id": first["next_after_id"], "fields": ["identity"]})
                check("stable ID cursor", min(e["identity"]["element_id"] for e in success(q2)["elements"]) > first["next_after_id"])
                check("field selection stays compact", all("instance_parameters" not in e for e in first["elements"]))
                parameters, _ = await call("get_parameters", {"element_ids": [fixture["panels"][0]]}, "parameters")
                parameter_row = success(parameters)["elements"][0]
                check("parameter projection reports its bounds", parameter_row["total_parameters"] >= len(parameter_row["parameters"]) and "has_more" in parameter_row, parameter_row)
                py, _ = await call("run_python", {"transaction_mode": "read", "source": "# mapped runtime error\n_result = 1 / 0", "label": "Test Python line diagnostic"}, "python-error")
                frames = ((py.get("receipt") or {}).get("details") or {}).get("diagnostics", {}).get("frames", [])
                check("Python runtime line", py.get("state") == "failed" and any(f["file"] == "agent.py" and f["line"] == 2 for f in frames), py)
                cs, _ = await call("run_csharp", {"transaction_mode": "auto", "source": "var x = Level.Create(doc, 400);\nthrow new InvalidOperationException(\"release probe\");", "label": "Test C# rollback and line diagnostic"}, "csharp-error")
                details = cs.get("receipt", {}).get("details", {})
                check("C# exception rollback", details.get("model_outcome") == "rolled_back", cs)
                check("C# runtime line", any(f["file"] == "agent.cs" and f["line"] == 2 for f in (details.get("diagnostics") or {}).get("frames", [])), cs)
                prefix = "MCP_RELEASE_" + uuid.uuid4().hex[:10]
                source = 'var level = Level.Create(doc, 400); level.Name = "' + prefix + '"; return JsonSerializer.Serialize(new { element_ids = new[] { level.Id.Value } });'
                verify = {"transaction_mode": "auto", "action": {"tool": "run_csharp", "arguments": {"source": source}},
                          "element_ids": [], "checks": [{"kind": "count", "expected": 2}], "rollback_on_failure": True, "fields": ["identity"]}
                failed, _ = await call("execute_and_verify", verify, "failed-check")
                check("optional assertion rollback", failed.get("error", {}).get("code") == "verification_failed" and failed["receipt"]["details"]["model_outcome"] == "rolled_back", failed)
                remaining, _ = await call("query_elements", {"name_contains": prefix, "fields": ["identity"]})
                check("failed assertion left no element", not success(remaining)["elements"])
                verify["checks"] = [{"kind": "exists"}, {"kind": "count", "expected": 1}]
                verify["request_id"] = uuid.uuid4().hex
                verify["idempotency_key"] = uuid.uuid4().hex
                good, _ = await call("execute_and_verify", verify, "passed-check")
                check("result IDs verified and committed", success(good)["verification"]["passed"] is True and good["receipt"]["details"]["model_outcome"] == "committed")
                duplicate, _ = await call("execute_and_verify", verify)
                check("retry deduplicates mutation", duplicate["result"] == good["result"] and duplicate["receipt"]["completed_utc"] == good["receipt"]["completed_utc"])
                created = success(good)["execution"]["element_ids"][0]
                cleaned, _ = await call("run_csharp", {"source": "doc.Delete(new ElementId(" + str(created) + "L)); return \"{}\";", "transaction_mode": "auto", "label": "Remove release probe"})
                success(cleaned)
                request_id = uuid.uuid4().hex
                pending = asyncio.create_task(call("run_python", {"request_id": request_id, "transaction_mode": "auto", "timeout_ms": 30000,
                    "label": "Test cooperative cancellation",
                    "source": "from System.Threading import Thread\nfrom Autodesk.Revit.DB import Level\nprobe = Level.Create(doc, 450)\nfor i in range(100):\n    report_progress('checkpoint', float(i))\n    Thread.Sleep(100)\n_result = {'done': True}"}))
                status = None
                for _ in range(50):
                    await asyncio.sleep(0.1)
                    status, _ = await call("get_request_status", {"request_id": request_id})
                    if (status.get("result") or {}).get("state") == "running":
                        break
                check("status available during execution", status and (status.get("result") or {}).get("state") == "running", status)
                observed = status["result"]
                check("status exposes queue position and event age", "queue_position" in observed and "accepted_event_wait_ms" in observed["readiness"], observed)
                revision = observed["receipt"]["revision"]
                changed, _ = await call("get_request_status", {"request_id": request_id, "after_revision": revision, "wait_ms": 1000})
                check("status wait returns a newer revision", changed["result"]["receipt"]["revision"] > revision, changed)
                cancelled, _ = await call("get_request_status", {"request_id": request_id, "request_cancel": True})
                final, _ = await pending
                check("cooperative cancellation reports rollback", final.get("error", {}).get("code") == "cooperative_cancelled" and final["receipt"]["details"]["model_outcome"] == "rolled_back", final)
                (args.output / "cancellation.json").write_text(json.dumps(final, indent=2))
            elif args.phase == "extras":
                source = 'var wall = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement(); var ids = ElementTransformUtils.CopyElement(doc, wall.Id, XYZ.Zero); return JsonSerializer.Serialize(new { element_ids = ids.Select(id => id.Value).ToArray() });'
                copied, _ = await call("run_csharp", {"source": source, "transaction_mode": "auto", "label": "Test commit warning retention"}, "commit-warning")
                ids = success(copied)["element_ids"]
                notices = copied["receipt"]["details"]["notices"]
                check("commit warnings retained before suppression", any(n.get("severity") == "Warning" and n.get("suppressed") and n.get("involved_element_ids") for n in notices), notices)
                cleanup = 'using var payload=JsonDocument.Parse(requestJson); foreach(var id in JsonSerializer.Deserialize<long[]>(payload.RootElement.GetProperty("ids").GetRawText())!) { var element=doc.GetElement(new ElementId(id)); if(element!=null) doc.Delete(element.Id); } return "{}";'
                cleaned, _ = await call("run_csharp", {"source": cleanup, "request": {"ids": ids}, "transaction_mode": "auto", "label": "Remove warning probe"})
                success(cleaned)
                batch, _ = await call("execute_batch", {"atomic": False, "steps": [
                    {"tool": "run_csharp", "transaction_mode": "auto", "arguments": {"source": 'return "{}";'}},
                    {"tool": "run_csharp", "transaction_mode": "auto", "arguments": {"source": 'throw new InvalidOperationException("batch probe");'}}]}, "non-atomic")
                steps = success(batch)["steps"]
                check("non-atomic outcomes are per step", steps[0]["model_outcome"] == "committed" and steps[1]["model_outcome"] == "rolled_back" and batch["receipt"]["details"]["model_outcome"] == "partial_possible", batch)
                inspected, _ = await call("get_elements", {"element_ids": [fixture["panels"][0]], "fields": ["identity", "materials", "bounding_boxes"]}, "materials")
                element = success(inspected)["elements"][0]
                check("material and transformed bounds available", bool(element["materials"]) and "transform" in element["bounding_boxes"]["model"], element)
                exported, _ = await call("export_view", {"view_id": view_before, "output_directory": str(args.output / "exports"), "file_name": "working-view", "format": "pdf"}, "export-pdf")
                check("existing view PDF emitted", all(Path(p).stat().st_size > 100 for p in success(exported)["artifacts"]))
            else:
                presets = [args.preset] if args.preset else ["building", "floors", "element", "vertical"]
                for preset in presets:
                    values = {"preset": preset, "pixel_size": 1200, "output_directory": str(args.output / "captures"), "timeout_ms": 600000}
                    if preset == "element":
                        values.update(element_ids=[fixture["panels"][0]], margin_m=1.0)
                    if preset == "vertical":
                        values.update(element_ids=[fixture["columns"][0]], axis="short", margin_m=1.0)
                    if preset == "floors":
                        values["level_ids"] = fixture["levels"]
                    capture, image_count = await call("capture_model", values, "capture-" + preset)
                    value = success(capture)
                    check(preset + " PNGs exist", all(Path(v["image"]).stat().st_size > 100 for v in value["views"]))
                    check(preset + " inline MCP images", image_count == min(2, len(value["contact_sheets"])), image_count)
                    print(json.dumps({"preset": preset, "sheets": value["contact_sheets"]}), flush=True)
            after, _ = await call("get_active_context")
            check("working view preserved", success(after)["view_id"] == view_before)
            selected, _ = await call("run_csharp", {"transaction_mode": "read", "source": "return JsonSerializer.Serialize(uidoc!.Selection.GetElementIds().Select(id=>id.Value).ToArray());", "label": "Check selection preservation"})
            check("selection preserved", sorted(success(selected)) == sorted(fixture["selection"]))
            print("LIVE RELEASE PASS " + args.phase, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True, help="Exact path of a disposable saved copy; this harness creates and deletes test elements and helper views.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--phase", choices=["contracts", "captures", "extras"], required=True)
    parser.add_argument("--preset", choices=["building", "elevations", "axo", "floors", "element", "horizontal", "vertical"])
    parser.add_argument("--pid", type=int)
    parser.add_argument("--version", default="0.2.0")
    args = parser.parse_args()
    asyncio.run(run(args))


if __name__ == "__main__":
    main()
