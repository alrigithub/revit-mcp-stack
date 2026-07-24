using System.Text;
using System.Text.Json;
using RevitMcp.Contracts;
using RevitMcp.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("frame handles partial reads", FramePartialReads),
    ("frame rejects oversize before allocation", FrameSizeLimit),
    ("ledger deduplicates request and idempotency IDs", LedgerDeduplication),
    ("ledger bounds retained terminal records", LedgerRetention),
    ("queue enforces capacity and terminal cancellation", QueueBounds),
    ("deadlines transition before execution", Deadline),
    ("document generations reject replaced documents", DocumentGeneration),
    ("transaction modes are explicit", TransactionModes),
    ("DTOs are bounded and name omissions", DtoBounds),
    ("errors redact sensitive content", RedactionTest),
    ("discovery rejects PID reuse", PidReuse),
    ("coordinator retains work after denied raise", LostWakeup)
};

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failures} failed={failures}");
return failures == 0 ? 0 : 1;

static async Task FramePartialReads()
{
    var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
    var buffer = new MemoryStream();
    await FrameCodec.WriteAsync(buffer, payload, 1024, default);
    var partial = new PartialReadStream(buffer.ToArray(), 1);
    var actual = await FrameCodec.ReadAsync(partial, 1024, default);
    Equal(Encoding.UTF8.GetString(payload), Encoding.UTF8.GetString(actual));
}

static async Task FrameSizeLimit()
{
    var bytes = BitConverter.GetBytes((uint)2048);
    await Throws<ProtocolException>(async () => await FrameCodec.ReadAsync(new MemoryStream(bytes), 100, default));
}

static Task LedgerDeduplication()
{
    var ledger = new RequestLedger();
    var one = Admission("a", "same");
    var two = Admission("b", "same");
    var first = ledger.Admit(one);
    var duplicate = ledger.Admit(two);
    True(first.Created); True(!duplicate.Created); Equal("a", duplicate.Record.Admission.RequestId);
    return Task.CompletedTask;
}

static Task LedgerRetention()
{
    var ledger = new RequestLedger(2);
    foreach (var id in new[] { "a", "b", "c" })
    {
        var record = ledger.Admit(Admission(id, "key-" + id)).Record;
        record.Transition(RequestState.Queued);
        record.Transition(RequestState.Running);
        record.Transition(RequestState.Succeeded);
    }
    ledger.Admit(Admission("d", null));
    True(ledger.Snapshot().Count <= 3); // two retained terminal records plus the live admission
    True(!ledger.TryGet("a", out _));
    return Task.CompletedTask;
}

static Task QueueBounds()
{
    var ledger = new RequestLedger(); var queue = new BoundedRequestQueue(1);
    var first = ledger.Admit(Admission("a", null)).Record;
    var second = ledger.Admit(Admission("b", null)).Record;
    True(queue.TryEnqueue(first)); True(!queue.TryEnqueue(second));
    Equal(1, queue.CancelQueued(_ => true, RequestState.CancelledBridgeOff));
    Equal(RequestState.CancelledBridgeOff, first.State);
    return Task.CompletedTask;
}

static Task Deadline()
{
    var admission = Admission("a", null) with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
    var record = new RequestLedger().Admit(admission).Record;
    record.Transition(RequestState.Queued);
    if (record.Admission.DeadlineUtc <= DateTimeOffset.UtcNow) record.Transition(RequestState.ExpiredBeforeStart);
    Equal(RequestState.ExpiredBeforeStart, record.State);
    return Task.CompletedTask;
}

static Task DocumentGeneration()
{
    var registry = new DocumentGenerationRegistry();
    Equal(1L, registry.Observe("doc", "path:a"));
    Equal(1L, registry.Observe("doc", "path:a"));
    Equal(2L, registry.Observe("doc", "path:b"));
    True(!registry.Matches("doc", 1)); True(registry.Matches("doc", 2));
    return Task.CompletedTask;
}

static Task TransactionModes()
{
    Equal("transaction_mode_required", RequestValidation.ValidateTransactionMode("run_python", null));
    Equal("invalid_transaction_mode", RequestValidation.ValidateTransactionMode("run_csharp", "magic"));
    Equal(null, RequestValidation.ValidateTransactionMode("run_csharp", "auto"));
    return Task.CompletedTask;
}

static Task DtoBounds()
{
    var input = JsonSerializer.SerializeToElement(new { values = Enumerable.Range(0, 20).ToArray(), text = new string('x', 40) });
    var bounded = RequestValidation.BoundJson(input, 3, 10, out var omitted);
    True(omitted.Length == 2); Equal(3, bounded.GetProperty("values").GetArrayLength()); Equal(10, bounded.GetProperty("text").GetString()!.Length);
    return Task.CompletedTask;
}

static Task RedactionTest()
{
    Equal("InvalidOperationException: [redacted]", Redaction.Error(new InvalidOperationException("source contains model data")));
    return Task.CompletedTask;
}

static Task PidReuse()
{
    var record = new DiscoveryRecord(99, 123, "2026", "0.1", "pipe", "on", "nonce", DateTimeOffset.UtcNow);
    True(!DiscoveryStore.IsLive(record, _ => 456)); True(DiscoveryStore.IsLive(record, _ => 123));
    return Task.CompletedTask;
}

static async Task LostWakeup()
{
    var work = true;
    var raiser = new FakeRaiser();
    await using var coordinator = new ExternalEventCoordinator(raiser, () => work);
    coordinator.NotifyWork();
    await Eventually(() => raiser.Count >= 2, TimeSpan.FromSeconds(2));
    coordinator.HandlerStarted(); work = false; coordinator.HandlerExited();
    Equal(CoordinatorState.Idle, coordinator.State);
}

static RequestAdmission Admission(string id, string? key) => new(id, key, "run_csharp", "doc", 1, DateTimeOffset.UtcNow.AddMinutes(1), null, "read", JsonSerializer.SerializeToElement(new { }), 2);
static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
static async Task Eventually(Func<bool> condition, TimeSpan timeout) { var end = DateTime.UtcNow + timeout; while (DateTime.UtcNow < end) { if (condition()) return; await Task.Delay(10); } throw new TimeoutException(); }

sealed class PartialReadStream(byte[] bytes, int maxChunk) : Stream
{
    private int _offset;
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => bytes.Length; public override long Position { get => _offset; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { var take = Math.Min(Math.Min(count, maxChunk), bytes.Length - _offset); if (take <= 0) return 0; Array.Copy(bytes, _offset, buffer, offset, take); _offset += take; return take; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var take = Math.Min(Math.Min(buffer.Length, maxChunk), bytes.Length - _offset); if (take <= 0) return ValueTask.FromResult(0); bytes.AsMemory(_offset, take).CopyTo(buffer); _offset += take; return ValueTask.FromResult(take); }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}

sealed class FakeRaiser : IExternalEventRaiser
{
    private int _count; public int Count => _count;
    public RaiseResult Raise() => Interlocked.Increment(ref _count) == 1 ? RaiseResult.Denied : RaiseResult.Accepted;
}
