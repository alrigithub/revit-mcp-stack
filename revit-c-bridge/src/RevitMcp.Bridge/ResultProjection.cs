using RevitMcp.Core;
using RevitMcp.Contracts;
using System.Text;
using System.Text.Json;

namespace RevitMcp.Bridge;

internal static class ResultProjection
{
    // Freeze provider results so pre-commit validation and the response see the same data.
    public static JsonElement Materialize(object value)
    {
        var raw = JsonSerializer.SerializeToElement(value);
        _ = Prepare(raw);
        return raw;
    }

    public static JsonElement Prepare(object value)
    {
        var raw = JsonSerializer.SerializeToElement(value);
        var bounded = RequestValidation.BoundJson(raw, 1000, 4096, out var omitted);
        if (omitted.Length > 0)
            bounded = JsonSerializer.SerializeToElement(new { value = bounded, omitted_fields = omitted, deferred_fields = VerificationService.DeferredFields });
        if (Encoding.UTF8.GetByteCount(bounded.GetRawText()) > ProtocolConstants.MaxResultBytes)
            throw new RequestDispatchException("result_too_large", "Bounded result exceeded the byte limit; narrow the query.");
        return bounded;
    }
}
