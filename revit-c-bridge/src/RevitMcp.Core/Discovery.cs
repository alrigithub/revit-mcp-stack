using System.Diagnostics;
using System.Text.Json;

namespace RevitMcp.Core;

public sealed record DiscoveryRecord(int Pid, long ProcessStartUtcTicks, string RevitYear, string ProtocolVersion, string PipeName, string BridgeState, string InstanceNonce, DateTimeOffset WrittenUtc);

public static class DiscoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcp", "instances");
    public static string PathFor(int pid) => Path.Combine(Root, $"{pid}.json");
    public static void WriteAtomic(DiscoveryRecord record)
    {
        Directory.CreateDirectory(Root);
        var target = PathFor(record.Pid);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(temporary, target, true);
    }
    public static void Remove(int pid)
    {
        var path = PathFor(pid);
        if (File.Exists(path)) File.Delete(path);
    }
    public static bool IsLive(DiscoveryRecord record, Func<int, long?>? processStartTicks = null)
    {
        processStartTicks ??= pid =>
        {
            try { return Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks; }
            catch { return null; }
        };
        return processStartTicks(record.Pid) == record.ProcessStartUtcTicks;
    }
}

public sealed record DocumentIdentity(string SessionId, long Generation, string Title, string? Path, bool IsActive, bool IsModifiable);

public sealed class DocumentGenerationRegistry
{
    private readonly Dictionary<string, (string Fingerprint, long Generation)> _entries = new(StringComparer.Ordinal);
    public long Observe(string sessionId, string fingerprint)
    {
        if (!_entries.TryGetValue(sessionId, out var existing)) { _entries[sessionId] = (fingerprint, 1); return 1; }
        if (existing.Fingerprint == fingerprint) return existing.Generation;
        var next = checked(existing.Generation + 1);
        _entries[sessionId] = (fingerprint, next);
        return next;
    }
    public bool Matches(string sessionId, long generation) => _entries.TryGetValue(sessionId, out var item) && item.Generation == generation;
    public void Close(string sessionId) => _entries.Remove(sessionId);
}
