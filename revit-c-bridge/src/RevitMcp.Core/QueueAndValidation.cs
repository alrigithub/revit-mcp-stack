using System.Text.Json;

namespace RevitMcp.Core;

public sealed class BoundedRequestQueue
{
    private readonly object _gate = new();
    private readonly Queue<RequestRecord> _queue = new();
    public BoundedRequestQueue(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }
    public int Capacity { get; }
    public int Count { get { lock (_gate) return _queue.Count; } }
    public bool TryEnqueue(RequestRecord request)
    {
        lock (_gate)
        {
            if (_queue.Count >= Capacity) return false;
            request.Transition(RequestState.Queued);
            _queue.Enqueue(request);
            return true;
        }
    }
    public bool TryDequeue(out RequestRecord? request)
    {
        lock (_gate)
        {
            if (_queue.Count == 0) { request = null; return false; }
            request = _queue.Dequeue(); return true;
        }
    }
    public int CancelQueued(Func<RequestRecord, bool> predicate, RequestState terminal)
    {
        lock (_gate)
        {
            var cancelled = 0;
            var count = _queue.Count;
            for (var index = 0; index < count; index++)
            {
                var record = _queue.Dequeue();
                if (predicate(record)) { record.Transition(terminal); cancelled++; }
                else _queue.Enqueue(record);
            }
            return cancelled;
        }
    }
}

public static class RequestValidation
{
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "read", "auto", "manual", "group" };
    private static readonly HashSet<string> DynamicTools = new(StringComparer.Ordinal) { "run_python", "run_csharp" };

    public static string? ValidateTransactionMode(string tool, string? mode)
    {
        if (DynamicTools.Contains(tool) && string.IsNullOrWhiteSpace(mode)) return "transaction_mode_required";
        if (mode is not null && !Modes.Contains(mode)) return "invalid_transaction_mode";
        return null;
    }

    public static JsonElement BoundJson(JsonElement input, int maxItems, int maxStringChars, out string[] omitted)
    {
        var omittedPaths = new List<string>();
        var bounded = Bound(input, "$", maxItems, maxStringChars, omittedPaths);
        omitted = omittedPaths.ToArray();
        return JsonSerializer.SerializeToElement(bounded);
    }

    private static object? Bound(JsonElement value, string path, int maxItems, int maxStringChars, List<string> omitted)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                var properties = value.EnumerateObject().ToArray();
                foreach (var property in properties.Take(maxItems)) map[property.Name] = Bound(property.Value, path + "." + property.Name, maxItems, maxStringChars, omitted);
                if (properties.Length > maxItems) omitted.Add(path + ".<remaining-properties>");
                return map;
            case JsonValueKind.Array:
                var values = value.EnumerateArray().ToArray();
                if (values.Length > maxItems) omitted.Add(path + "[remaining]");
                return values.Take(maxItems).Select((item, index) => Bound(item, $"{path}[{index}]", maxItems, maxStringChars, omitted)).ToArray();
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (text.Length <= maxStringChars) return text;
                omitted.Add(path + "<truncated>");
                return text[..maxStringChars];
            case JsonValueKind.Number: return value.TryGetInt64(out var integer) ? integer : value.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            default: return null;
        }
    }
}

public static class Redaction
{
    private static readonly string[] Sensitive = ["source", "result", "model", "environment", "token", "secret"];
    public static string Error(Exception exception)
    {
        var message = exception.Message;
        foreach (var word in Sensitive)
            if (message.Contains(word, StringComparison.OrdinalIgnoreCase)) return $"{exception.GetType().Name}: [redacted]";
        return $"{exception.GetType().Name}: {message[..Math.Min(message.Length, 512)]}";
    }
}
