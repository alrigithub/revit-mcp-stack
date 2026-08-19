using System.Text.Json;

namespace RevitMcp.Bridge;

public sealed record OperationalLogEntry(DateTimeOffset TimestampUtc, string? RequestId, string? DocumentSession, string Event, string? Tool, string? State, int? Bytes, long? ElapsedMs, string? Provider, string? RedactedError, string? TransactionMode = null, string? Summary = null, string? Label = null);

public sealed class OperationalLog
{
    private readonly object _gate = new();
    private readonly Queue<OperationalLogEntry> _entries = new();
    private readonly int _capacity;
    public OperationalLog(int capacity = 2000) => _capacity = capacity;
    public void Add(OperationalLogEntry entry)
    {
        lock (_gate) { _entries.Enqueue(entry); while (_entries.Count > _capacity) _entries.Dequeue(); }
    }
    public OperationalLogEntry[] Entries(int count)
    {
        lock (_gate) return _entries.TakeLast(Math.Clamp(count, 1, 500)).ToArray();
    }
    public object[] Tail(int count) => Entries(count).Cast<object>().ToArray();
    public void Clear() { lock (_gate) _entries.Clear(); }
    public string ExportJsonLines() { lock (_gate) return string.Join(Environment.NewLine, _entries.Select(entry => JsonSerializer.Serialize(entry))); }
}
