namespace RevitMcp.Core;

public enum RaiseResult { Accepted, Pending, Denied, TimedOut }
public enum CoordinatorState { Idle, Raising, Accepted, Executing, Stopped }

public interface IExternalEventRaiser
{
    RaiseResult Raise();
}

public sealed class ExternalEventCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IExternalEventRaiser _raiser;
    private readonly Func<bool> _hasWork;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private CoordinatorState _state = CoordinatorState.Idle;
    private bool _retainedSignal;

    public ExternalEventCoordinator(IExternalEventRaiser raiser, Func<bool> hasWork)
    {
        _raiser = raiser;
        _hasWork = hasWork;
        _loop = Task.Run(RunAsync);
    }
    public CoordinatorState State { get { lock (_gate) return _state; } }
    public void NotifyWork()
    {
        lock (_gate)
        {
            if (_state == CoordinatorState.Stopped) return;
            _retainedSignal = true;
        }
        _signal.Release();
    }
    public void HandlerStarted()
    {
        lock (_gate)
        {
            if (_state is not (CoordinatorState.Accepted or CoordinatorState.Raising))
                throw new InvalidOperationException($"Handler started while coordinator was {_state}.");
            _state = CoordinatorState.Executing;
        }
    }
    public void HandlerExited()
    {
        var signal = false;
        lock (_gate)
        {
            if (_state != CoordinatorState.Executing) throw new InvalidOperationException("Handler exit without execution.");
            _state = CoordinatorState.Idle;
            if (_hasWork() || _retainedSignal) { _retainedSignal = true; signal = true; }
        }
        if (signal) _signal.Release();
    }

    private async Task RunAsync()
    {
        var backoffMs = 10;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                bool shouldRaise;
                lock (_gate)
                {
                    shouldRaise = _state == CoordinatorState.Idle && (_retainedSignal || _hasWork());
                    if (shouldRaise) { _state = CoordinatorState.Raising; _retainedSignal = false; }
                }
                if (!shouldRaise) continue;

                RaiseResult result;
                lock (_gate)
                {
                    // Revit can begin the handler as Raise returns. Holding this lock across the
                    // call makes Accepted bookkeeping atomic with HandlerStarted.
                    result = _raiser.Raise();
                    if (_state == CoordinatorState.Raising)
                    {
                        if (result == RaiseResult.Accepted) _state = CoordinatorState.Accepted;
                        else { _state = CoordinatorState.Idle; _retainedSignal = _hasWork(); }
                    }
                }
                if (result == RaiseResult.Accepted) { backoffMs = 10; continue; }
                if (_hasWork())
                {
                    await Task.Delay(backoffMs, _stop.Token).ConfigureAwait(false);
                    backoffMs = Math.Min(backoffMs * 2, 500);
                    _signal.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _state = CoordinatorState.Stopped;
        _stop.Cancel();
        _signal.Release();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _signal.Dispose();
        _stop.Dispose();
    }
}
