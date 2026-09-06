using System.Collections.Concurrent;
using System.Text.Json;

namespace RevitMcp.Core;

public enum RequestState
{
    Accepted,
    Queued,
    ExpiredBeforeStart,
    Running,
    Succeeded,
    Failed,
    AbandonedAfterStart,
    ProviderReloadedBeforeStart,
    ProviderDisabledBeforeStart,
    CancelledBridgeOff,
    CancelledBeforeStart
}

public static class RequestStateRules
{
    public static bool IsTerminal(this RequestState state) => state is RequestState.ExpiredBeforeStart
        or RequestState.Succeeded or RequestState.Failed or RequestState.AbandonedAfterStart
        or RequestState.ProviderReloadedBeforeStart or RequestState.ProviderDisabledBeforeStart
        or RequestState.CancelledBridgeOff or RequestState.CancelledBeforeStart;

    public static bool CanTransition(RequestState from, RequestState to) => (from, to) switch
    {
        (RequestState.Accepted, RequestState.Queued) => true,
        (RequestState.Accepted, RequestState.Failed) => true,
        (RequestState.Accepted, RequestState.CancelledBridgeOff) => true,
        (RequestState.Queued, RequestState.Running) => true,
        (RequestState.Queued, RequestState.ExpiredBeforeStart) => true,
        (RequestState.Queued, RequestState.ProviderReloadedBeforeStart) => true,
        (RequestState.Queued, RequestState.ProviderDisabledBeforeStart) => true,
        (RequestState.Queued, RequestState.CancelledBridgeOff) => true,
        (RequestState.Queued, RequestState.CancelledBeforeStart) => true,
        (RequestState.Running, RequestState.Succeeded) => true,
        (RequestState.Running, RequestState.Failed) => true,
        (RequestState.Running, RequestState.AbandonedAfterStart) => true,
        _ => false
    };
}

public sealed record RequestAdmission(
    string RequestId,
    string? IdempotencyKey,
    string Tool,
    string? DocumentSession,
    long? DocumentGeneration,
    DateTimeOffset DeadlineUtc,
    string? ProviderGeneration,
    string? TransactionMode,
    JsonElement Arguments,
    int InputBytes);

public sealed class RequestRecord
{
    private readonly object _gate = new();
    private RequestState _state;
    private DateTimeOffset _updatedUtc = DateTimeOffset.UtcNow;
    private JsonElement? _result;
    private string? _errorCode;
    private string? _redactedError;
    private DateTimeOffset? _startedUtc, _completedUtc;
    private long _revision = 1;
    private bool _cancellationRequested;
    private string? _progress;
    private double? _percent;
    private long _lastProgressTick;
    private JsonElement? _receipt;
    public RequestRecord(RequestAdmission admission)
    {
        Admission = admission;
        _state = RequestState.Accepted;
        AcceptedUtc = DateTimeOffset.UtcNow;
    }
    public RequestAdmission Admission { get; }
    public DateTimeOffset AcceptedUtc { get; }
    public DateTimeOffset UpdatedUtc { get { lock (_gate) return _updatedUtc; } }
    public RequestState State { get { lock (_gate) return _state; } }
    public JsonElement? Result { get { lock (_gate) return _result; } }
    public string? ErrorCode { get { lock (_gate) return _errorCode; } }
    public string? RedactedError { get { lock (_gate) return _redactedError; } }
    public long Revision { get { lock (_gate) return _revision; } }
    public bool CancellationRequested { get { lock (_gate) return _cancellationRequested; } }
    public void RequestCancellation() { lock (_gate) { if (_state.IsTerminal()) return; _cancellationRequested = true; Touch(); } }
    public void Progress(string message, double? percent)
    {
        lock (_gate)
        {
            if (_state != RequestState.Running) return;
            var now = Environment.TickCount64;
            if (_progress is not null && now - _lastProgressTick < 100 && percent != 100) return;
            _lastProgressTick = now;
            _progress = message[..Math.Min(message.Length, 240)];
            _percent = percent.HasValue && double.IsFinite(percent.Value) ? Math.Clamp(percent.Value, 0, 100) : null;
            Touch();
        }
    }
    public void SetReceipt(JsonElement receipt) { lock (_gate) { _receipt = receipt; Touch(); } }
    private void Touch() { _updatedUtc = DateTimeOffset.UtcNow; _revision++; }
    public JsonElement Receipt()
    {
        lock (_gate) return JsonSerializer.SerializeToElement(new
        {
            revision = _revision, accepted_utc = AcceptedUtc, started_utc = _startedUtc, completed_utc = _completedUtc,
            queue_ms = Math.Max(0, ((_startedUtc ?? _completedUtc ?? DateTimeOffset.UtcNow) - AcceptedUtc).TotalMilliseconds),
            execution_ms = _startedUtc.HasValue ? Math.Max(0, ((_completedUtc ?? DateTimeOffset.UtcNow) - _startedUtc.Value).TotalMilliseconds) : 0,
            cancellation_requested = _cancellationRequested, progress = _progress, percent = _percent,
            details = _receipt
        });
    }

    public void Transition(RequestState next, JsonElement? result = null, string? errorCode = null, string? redactedError = null)
    {
        lock (_gate)
        {
            if (!RequestStateRules.CanTransition(_state, next))
                throw new InvalidOperationException($"Invalid request transition {_state} -> {next}.");
            _result = result;
            _errorCode = errorCode;
            _redactedError = redactedError;
            Touch();
            if (next == RequestState.Running) _startedUtc = _updatedUtc;
            if (next.IsTerminal()) _completedUtc = _updatedUtc;
            _state = next;
        }
    }
}

public sealed class RequestLedger
{
    private readonly object _admissionGate = new();
    private readonly ConcurrentDictionary<string, RequestRecord> _byRequest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly int _maxRetainedTerminalRecords;

    public RequestLedger(int maxRetainedTerminalRecords = 4096)
    {
        if (maxRetainedTerminalRecords < 1) throw new ArgumentOutOfRangeException(nameof(maxRetainedTerminalRecords));
        _maxRetainedTerminalRecords = maxRetainedTerminalRecords;
    }

    public (RequestRecord Record, bool Created) Admit(RequestAdmission admission)
    {
        // Request and idempotency indexes must change atomically, including retention.
        lock (_admissionGate) return AdmitLocked(admission);
    }

    private (RequestRecord Record, bool Created) AdmitLocked(RequestAdmission admission)
    {
        if (string.IsNullOrWhiteSpace(admission.RequestId)) throw new ArgumentException("Request ID is required.");
        TrimTerminalRecords();
        if (!string.IsNullOrEmpty(admission.IdempotencyKey) && _idempotency.TryGetValue(admission.IdempotencyKey, out var originalId))
            return (_byRequest[originalId], false);

        var created = new RequestRecord(admission);
        var actual = _byRequest.GetOrAdd(admission.RequestId, created);
        if (!ReferenceEquals(actual, created)) return (actual, false);
        if (!string.IsNullOrEmpty(admission.IdempotencyKey))
        {
            var winner = _idempotency.GetOrAdd(admission.IdempotencyKey, admission.RequestId);
            if (!string.Equals(winner, admission.RequestId, StringComparison.Ordinal))
            {
                _byRequest.TryRemove(admission.RequestId, out _);
                return (_byRequest[winner], false);
            }
        }
        TrimTerminalRecords();
        return (created, true);
    }

    public bool TryGet(string requestId, out RequestRecord? record) => _byRequest.TryGetValue(requestId, out record);
    public IReadOnlyCollection<RequestRecord> Snapshot() => _byRequest.Values.ToArray();

    private void TrimTerminalRecords()
    {
        var excess = _byRequest.Count - _maxRetainedTerminalRecords;
        if (excess <= 0) return;
        var expired = _byRequest.Values
            .Where(record => record.State.IsTerminal())
            .OrderBy(record => record.UpdatedUtc)
            .Take(excess)
            .ToArray();
        foreach (var record in expired)
        {
            if (!_byRequest.TryRemove(record.Admission.RequestId, out _)) continue;
            foreach (var mapping in _idempotency.Where(pair => pair.Value == record.Admission.RequestId).ToArray())
                _idempotency.TryRemove(mapping.Key, out _);
        }
    }
}
