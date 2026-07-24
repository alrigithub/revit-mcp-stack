using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Bridge;

public delegate string PythonExecuteDelegate(UIApplication uiApplication, Document document, UIDocument? uiDocument, string requestJson);
public delegate string PythonCompileDelegate(string source);

public sealed class PythonProviderDescriptor
{
    public string AbiVersion { get; set; } = string.Empty;
    public string CompanionBuildHash { get; set; } = string.Empty;
    public string EngineName { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string PyRevitVersion { get; set; } = string.Empty;
    public string ProviderGeneration { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool SelfTestPassed { get; set; }
    public string SelfTestMessage { get; set; } = string.Empty;
}

public static class PythonRegistrationService
{
    public static void Register(PythonProviderDescriptor descriptor, PythonCompileDelegate compiler, PythonExecuteDelegate executor, Action reload) => BridgeRuntime.Require().Providers.Register(descriptor, compiler, executor, reload);
    public static void SetEnabled(bool enabled) => BridgeRuntime.Require().Providers.SetEnabled(enabled);
    public static string GetStatusJson() => System.Text.Json.JsonSerializer.Serialize(BridgeRuntime.Require().Providers.Status());
}

public sealed class PythonProviderRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly BoundedRequestQueue _queue;
    private readonly OperationalLog _log;
    private PythonProviderDescriptor? _descriptor;
    private PythonCompileDelegate? _compiler;
    private PythonExecuteDelegate? _executor;
    private Action? _reload;
    private DateTimeOffset _registeredUtc;
    public PythonProviderRegistry(BoundedRequestQueue queue, OperationalLog log) { _queue = queue; _log = log; }
    public string? CurrentGeneration { get { lock (_gate) return _descriptor?.ProviderGeneration; } }
    public string? PyRevitVersion { get { lock (_gate) return string.IsNullOrWhiteSpace(_descriptor?.PyRevitVersion) ? null : _descriptor!.PyRevitVersion; } }
    public string Capability
    {
        get
        {
            lock (_gate)
            {
                if (_descriptor is null) return "expected_but_not_registered";
                if (_descriptor.AbiVersion != RevitMcp.Contracts.ProtocolConstants.PythonAbi) return "incompatible_abi";
                if (!_descriptor.SelfTestPassed) return "self_test_failed";
                if (DateTimeOffset.UtcNow - _registeredUtc > TimeSpan.FromHours(24)) return "stale";
                return _descriptor.Enabled ? "available" : "disabled";
            }
        }
    }
    public void Register(PythonProviderDescriptor descriptor, PythonCompileDelegate compiler, PythonExecuteDelegate executor, Action reload)
    {
        ArgumentNullException.ThrowIfNull(descriptor); ArgumentNullException.ThrowIfNull(compiler); ArgumentNullException.ThrowIfNull(executor); ArgumentNullException.ThrowIfNull(reload);
        string? prior;
        lock (_gate)
        {
            prior = _descriptor?.ProviderGeneration;
            _descriptor = descriptor; _compiler = compiler; _executor = executor; _reload = reload; _registeredUtc = DateTimeOffset.UtcNow;
        }
        if (prior is not null && prior != descriptor.ProviderGeneration)
            _queue.CancelQueued(r => r.Admission.Tool == "run_python" && r.Admission.ProviderGeneration == prior, RequestState.ProviderReloadedBeforeStart);
        _log.Add(new(DateTimeOffset.UtcNow, null, null, "python_registered", "run_python", Capability, null, null,
            descriptor.EngineName + " " + descriptor.EngineVersion + (string.IsNullOrWhiteSpace(descriptor.PyRevitVersion) ? "" : " · pyRevit " + descriptor.PyRevitVersion), null));
    }
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_descriptor is null) throw new InvalidOperationException("Python provider has not registered.");
            _descriptor.Enabled = enabled;
        }
        if (!enabled) _queue.CancelQueued(r => r.Admission.Tool == "run_python", RequestState.ProviderDisabledBeforeStart);
    }
    public string Execute(string pinnedGeneration, UIApplication uiapp, Document doc, UIDocument? uidoc, string requestJson)
    {
        PythonExecuteDelegate executor;
        lock (_gate)
        {
            if (Capability != "available") throw new InvalidOperationException($"Python capability is {Capability}.");
            if (_descriptor!.ProviderGeneration != pinnedGeneration) throw new ProviderGenerationException();
            executor = _executor!;
        }
        return executor(uiapp, doc, uidoc, requestJson);
    }
    public void Prepare(string pinnedGeneration, string source)
    {
        PythonCompileDelegate compiler;
        lock (_gate)
        {
            if (Capability != "available") throw new RequestDispatchException("capability_unavailable", $"Python capability is {Capability}.");
            if (_descriptor!.ProviderGeneration != pinnedGeneration) throw new ProviderGenerationException();
            compiler = _compiler!;
        }
        var result = compiler(source);
        if (!string.Equals(result, "ok", StringComparison.Ordinal)) throw new RequestDispatchException("python_compile_error", result);
    }
    public object Reload()
    {
        Action reload;
        string? old;
        lock (_gate)
        {
            if (_reload is null || _descriptor is null) throw new RequestDispatchException("capability_unavailable", "Python provider is not registered.");
            old = _descriptor.ProviderGeneration;
            _descriptor.Enabled = false;
            reload = _reload;
        }
        _queue.CancelQueued(r => r.Admission.Tool == "run_python", RequestState.ProviderReloadedBeforeStart);
        reload();
        lock (_gate)
        {
            if (_descriptor?.ProviderGeneration == old) throw new RequestDispatchException("provider_reload_failed", "Reload did not register a new provider generation.");
            return Status();
        }
    }
    public object Status() { lock (_gate) return new { capability = Capability, descriptor = _descriptor, registered_utc = _registeredUtc }; }
    public void Dispose() { lock (_gate) { _compiler = null; _executor = null; _reload = null; _descriptor = null; } }
}

public sealed class ProviderGenerationException : Exception { public ProviderGenerationException() : base("Pinned Python provider generation is no longer current.") { } }
