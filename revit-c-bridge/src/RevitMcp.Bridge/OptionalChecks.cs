using Autodesk.Revit.DB;
using System.Text.Json;

namespace RevitMcp.Bridge;

internal static class OptionalChecks
{
    public static void Validate(JsonElement[] checks)
    {
        if (checks.Length > 100) throw new RequestDispatchException("too_many_checks", "At most 100 optional checks are supported.");
        foreach (var check in checks)
        {
            var kind = check.GetProperty("kind").GetString();
            if (kind is not ("exists" or "bounds" or "count" or "parameter")) throw new RequestDispatchException("unsupported_check", "Supported checks: exists, bounds, count, parameter.");
            if (kind == "count") _ = check.GetProperty("expected").GetInt32();
            if (kind == "parameter")
            {
                _ = check.GetProperty("element_id").GetInt64(); _ = check.GetProperty("parameter_id").GetInt64(); _ = check.GetProperty("expected");
            }
        }
    }
    public static object[] Evaluate(Document doc, JsonElement[] checks, ElementId[] defaults, out bool passed)
    {
        var allPassed = true;
        var results = checks.Select(check =>
        {
            var kind = check.GetProperty("kind").GetString();
            var ids = check.TryGetProperty("element_ids", out var specified) ? specified.EnumerateArray().Select(x => new ElementId(x.GetInt64())).Take(1000).ToArray() : defaults;
            var elements = ids.Select(doc.GetElement).ToArray();
            bool ok;
            object? actual;
            switch (kind)
            {
                case "count": actual = elements.Count(e => e is not null); ok = (int)actual == check.GetProperty("expected").GetInt32(); break;
                case "parameter":
                    var element = doc.GetElement(new ElementId(check.GetProperty("element_id").GetInt64()));
                    var parameterId = check.GetProperty("parameter_id").GetInt64();
                    var p = element?.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.Value == parameterId);
                    actual = p is null ? null : VerificationService.Raw(p);
                    var expected = check.GetProperty("expected");
                    var tolerance = check.TryGetProperty("tolerance", out var t) ? Math.Abs(t.GetDouble()) : 1e-8;
                    ok = p is not null && (expected.ValueKind == JsonValueKind.Number && actual is double or int or long
                        ? Math.Abs(Convert.ToDouble(actual) - expected.GetDouble()) <= tolerance
                        : JsonSerializer.SerializeToElement(actual).GetRawText() == expected.GetRawText());
                    break;
                default:
                    var failedIds = ids.Where((id, i) => elements[i] is null || (kind == "bounds" && elements[i]!.get_BoundingBox(null) is null)).Select(id => id.Value).ToArray();
                    actual = new { checked_count = ids.Length, failed_ids = failedIds };
                    ok = ids.Length > 0 && failedIds.Length == 0; break;
            }
            allPassed &= ok;
            return (object)new { kind, passed = ok, actual };
        }).ToArray();
        passed = allPassed;
        return results;
    }
}

internal sealed class VerificationFailedException(JsonElement details) : Exception("An optional check failed; the bridge-owned transaction was rolled back.")
{
    public JsonElement Details { get; } = details;
}
