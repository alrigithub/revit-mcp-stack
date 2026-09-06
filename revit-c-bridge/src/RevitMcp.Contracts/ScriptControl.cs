namespace RevitMcp.Contracts;

/// <summary>Optional cooperative hooks. Never interrupts a Revit API call.</summary>
public static class ScriptControl
{
    private sealed record Hooks(Action<string, double?> Progress, Func<bool> Cancelled);
    private static readonly AsyncLocal<Hooks?> Current = new();
    public static IDisposable Bind(Action<string, double?> progress, Func<bool> cancelled)
    {
        var previous = Current.Value;
        Current.Value = new(progress, cancelled);
        return new Scope(() => Current.Value = previous);
    }
    public static void ReportProgress(string message, double? percent = null)
    {
        ThrowIfCancellationRequested();
        Current.Value?.Progress(message, percent);
    }
    public static void ThrowIfCancellationRequested()
    {
        if (Current.Value?.Cancelled() == true) throw new OperationCanceledException("Request cancellation was observed at a cooperative checkpoint.");
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}

public sealed class ScriptFailureException(string diagnosticsJson) : Exception("Script execution failed. See receipt.details.diagnostics.")
{
    public string DiagnosticsJson { get; } = diagnosticsJson;
}
