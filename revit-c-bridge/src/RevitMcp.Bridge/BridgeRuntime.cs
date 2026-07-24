using Autodesk.Revit.UI;
using RevitMcp.Contracts;
using RevitMcp.Core;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace RevitMcp.Bridge;

public enum BridgeState { Off, Starting, On, Stopping }

public sealed class BridgeRuntime : IDisposable
{
    public static readonly string ProductVersion =
        typeof(BridgeRuntime).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.00";
    private readonly object _gate = new();
    private readonly int _pid = Environment.ProcessId;
    private readonly long _startTicks = Process.GetCurrentProcess().StartTime.ToFileTimeUtc();
    private ExternalEvent? _externalEvent;
    private ExternalEventCoordinator? _coordinator;
    private PipeServer? _pipe;
    private PushButton? _onButton;
    private PushButton? _offButton;
    private bool _disposed;

    private BridgeRuntime(string revitYear)
    {
        RevitYear = revitYear;
        InstanceNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        PipeName = $"revit-mcp-{Environment.UserName.GetHashCode(StringComparison.Ordinal):x8}-{_pid}-{InstanceNonce[..12]}";
        Ledger = new RequestLedger();
        Queue = new BoundedRequestQueue(256);
        Documents = new RevitDocumentRegistry();
        Providers = new PythonProviderRegistry(Queue, Log);
        Roslyn = new RoslynProviderHost(revitYear, Log);
    }

    public static BridgeRuntime? Current { get; private set; }
    public static BridgeRuntime Create(string revitYear) => Current = new BridgeRuntime(revitYear);
    public static BridgeRuntime Require() => Current ?? throw new InvalidOperationException("Bridge runtime is not initialized.");
    public string RevitYear { get; }
    public string InstanceNonce { get; }
    public string PipeName { get; }
    public BridgeState State { get; private set; } = BridgeState.Off;
    public bool AdmissionEnabled => State == BridgeState.On;
    public RequestLedger Ledger { get; }
    public BoundedRequestQueue Queue { get; }
    public RevitDocumentRegistry Documents { get; }
    public PythonProviderRegistry Providers { get; }
    public RoslynProviderHost Roslyn { get; }
    public OperationalLog Log { get; } = new();

    public void AttachExternalEvent(ExternalEvent externalEvent)
    {
        _externalEvent = externalEvent;
        _coordinator = new ExternalEventCoordinator(new RevitEventRaiser(externalEvent), () => Queue.Count > 0);
    }
    public void AttachButtons(PushButton on, PushButton off) { _onButton = on; _offButton = off; }

    public void Enable()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == BridgeState.On) return;
            State = BridgeState.Starting; RefreshRibbon();
            try
            {
                _pipe = new PipeServer(this);
                _pipe.Start();
                State = BridgeState.On;
                PublishDiscovery();
                Log.Add(new(DateTimeOffset.UtcNow, null, null, "bridge_enabled", null, "on", null, null, null, null));
            }
            catch { State = BridgeState.Off; _pipe?.Dispose(); _pipe = null; throw; }
            finally { RefreshRibbon(); }
        }
    }

    public void Disable()
    {
        PipeServer? pipe;
        lock (_gate)
        {
            if (State == BridgeState.Off) return;
            State = BridgeState.Stopping; RefreshRibbon();
            DiscoveryStore.Remove(_pid);
            Queue.CancelQueued(_ => true, RequestState.CancelledBridgeOff);
            Roslyn.DiscardAllPrepared();
            pipe = _pipe; _pipe = null;
            State = BridgeState.Off;
            Log.Add(new(DateTimeOffset.UtcNow, null, null, "bridge_disabled", null, "off", null, null, null, null));
            RefreshRibbon();
        }
        // Listener disposal is outside the state lock. A UI-thread request already marked
        // Running is intentionally untouched and reaches its real terminal state.
        pipe?.Dispose();
    }

    public void RefreshRibbon()
    {
        if (_onButton is null || _offButton is null) return;
        var isOff = State == BridgeState.Off;
        _onButton.Enabled = isOff;
        _offButton.Enabled = State == BridgeState.On;
        _onButton.ToolTip = $"Start the local Revit MCP bridge. Actual state: {State.ToString().ToUpperInvariant()}. Default: OFF each Revit session.";
        _offButton.ToolTip = $"Stop admission and discovery; queued work is terminally cancelled. Actual state: {State.ToString().ToUpperInvariant()}.";
        _onButton.LongDescription = $"Current bridge state is {State}. Named pipe is {(State == BridgeState.On ? PipeName : "not listening")}.";
        _offButton.LongDescription = _onButton.LongDescription;
    }

    internal void NotifyWork() => _coordinator?.NotifyWork();
    internal void HandlerStarted() => _coordinator?.HandlerStarted();
    internal void HandlerExited() => _coordinator?.HandlerExited();

    internal object Capabilities() => new
    {
        protocol_version = ProtocolConstants.Version,
        product_version = ProductVersion,
        bridge = State.ToString().ToLowerInvariant(),
        revit_year = RevitYear,
        python = Providers.Capability,
        roslyn = Roslyn.Capability,
        transaction_modes = new[] { "read", "auto", "manual", "group" },
        security = new { trust = "same_windows_user_v0", current_user_pipe = true, authentication = false },
        deferred_projections = VerificationService.DeferredFields
    };

    private void PublishDiscovery() => DiscoveryStore.WriteAtomic(new(_pid, _startTicks, RevitYear, ProtocolConstants.Version, PipeName, "on", InstanceNonce, DateTimeOffset.UtcNow));

    public void Dispose()
    {
        if (_disposed) return;
        Disable(); _disposed = true;
        Providers.Dispose(); Roslyn.Dispose();
        if (_coordinator is not null) _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _externalEvent?.Dispose();
        Current = null;
    }

    private sealed class RevitEventRaiser(ExternalEvent externalEvent) : IExternalEventRaiser
    {
        public RaiseResult Raise() => externalEvent.Raise() switch
        {
            ExternalEventRequest.Accepted => RaiseResult.Accepted,
            ExternalEventRequest.Pending => RaiseResult.Pending,
            ExternalEventRequest.Denied => RaiseResult.Denied,
            ExternalEventRequest.TimedOut => RaiseResult.TimedOut,
            _ => RaiseResult.Denied
        };
    }
}
