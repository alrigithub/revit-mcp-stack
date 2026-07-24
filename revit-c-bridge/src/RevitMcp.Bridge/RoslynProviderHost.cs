using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Contracts;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace RevitMcp.Bridge;

public sealed class RoslynProviderHost : IDisposable
{
    private sealed record PreparedCompilation(CompileResult Result, DateTimeOffset PreparedUtc);
    private const int PreparedLimit = 256;
    private static readonly TimeSpan PreparedLifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly string _revitYear;
    private readonly OperationalLog _log;
    private readonly Dictionary<string, PreparedCompilation> _prepared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompiledEntry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _entryOrder = new();
    private ProviderLoadContext? _context;
    private IRoslynCompilerProvider? _provider;
    private string? _error;
    public RoslynProviderHost(string revitYear, OperationalLog log) { _revitYear = revitYear; _log = log; TryLoad(); }
    public string Capability => _provider is not null ? "available" : "provider_not_loaded";

    public CompileResult Prepare(string requestId, string source)
    {
        lock (_gate)
        {
            PrunePrepared();
            if (_provider is null) return new(false, null, null, _error ?? "Roslyn provider is not packaged.", string.Empty);
            var result = _provider.Compile(new(source, _revitYear, ReferenceManifest(), "bridge-0.9"));
            if (result.Success)
            {
                _prepared[requestId] = new(result, DateTimeOffset.UtcNow);
                PrunePrepared();
            }
            return result;
        }
    }

    public string Invoke(string requestId, UIApplication uiapp, Document document, UIDocument? uidoc, string requestJson)
    {
        CompiledEntry entry;
        lock (_gate)
        {
            PrunePrepared();
            if (!_prepared.Remove(requestId, out var prepared)) throw new RequestDispatchException("compiled_request_missing", "C# source was not prepared before Revit dispatch.");
            var compiled = prepared.Result;
            if (!_entries.TryGetValue(compiled.CacheKey, out entry!))
            {
                entry = CompiledEntry.Load(compiled);
                _entries[compiled.CacheKey] = entry; _entryOrder.Enqueue(compiled.CacheKey);
                while (_entryOrder.Count > 64) { var key = _entryOrder.Dequeue(); if (_entries.Remove(key, out var old)) old.Dispose(); }
            }
        }
        try { return (string)(entry.Method.Invoke(null, [uiapp, document, uidoc, requestJson]) ?? "null"); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    public void DiscardPrepared(string requestId)
    {
        lock (_gate)
        {
            _prepared.Remove(requestId);
            foreach (var key in _prepared.Keys.Where(key => key.StartsWith(requestId + ":", StringComparison.Ordinal)).ToArray())
                _prepared.Remove(key);
        }
    }

    public void DiscardAllPrepared() { lock (_gate) _prepared.Clear(); }

    public object Reload()
    {
        lock (_gate) { Unload(); TryLoad(); return new { capability = Capability, provider_version = _provider?.ProviderVersion, error = _error }; }
    }

    private void TryLoad()
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "providers", "roslyn", "1");
            var path = Path.Combine(directory, "RevitMcp.RoslynProvider.dll");
            if (!File.Exists(path)) { _error = $"Missing isolated provider at {path}."; return; }
            _context = new ProviderLoadContext(directory);
            var assembly = _context.LoadFromAssemblyPath(path);
            var type = assembly.GetTypes().Single(t => typeof(IRoslynCompilerProvider).IsAssignableFrom(t) && !t.IsAbstract);
            _provider = (IRoslynCompilerProvider)Activator.CreateInstance(type)!;
            if (_provider.AbiVersion != ProtocolConstants.RoslynAbi) throw new InvalidOperationException("Roslyn ABI mismatch.");
            _error = null;
        }
        catch (Exception ex) { _error = RevitMcp.Core.Redaction.Error(ex); _provider = null; _context?.Unload(); _context = null; }
    }
    private static string[] ReferenceManifest()
    {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var names = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll", "System.Text.Json.dll", "System.Memory.dll", "System.Buffers.dll" };
        var references = names.Select(n => Path.Combine(runtime, n)).Concat(new[] { typeof(Document).Assembly.Location, typeof(UIApplication).Assembly.Location, typeof(ProtocolConstants).Assembly.Location });
        var adWindows = Path.Combine(Path.GetDirectoryName(typeof(Document).Assembly.Location)!, "AdWindows.dll");
        if (File.Exists(adWindows)) references = references.Append(adWindows); // ribbon access (Autodesk.Windows) for UI tools
        return references.ToArray();
    }
    private void Unload()
    {
        _provider = null; _prepared.Clear(); foreach (var entry in _entries.Values) entry.Dispose(); _entries.Clear(); _entryOrder.Clear();
        _context?.Unload(); _context = null;
    }
    public void Dispose() { lock (_gate) Unload(); }

    private void PrunePrepared()
    {
        var cutoff = DateTimeOffset.UtcNow - PreparedLifetime;
        foreach (var key in _prepared.Where(pair => pair.Value.PreparedUtc < cutoff).Select(pair => pair.Key).ToArray())
            _prepared.Remove(key);
        while (_prepared.Count > PreparedLimit)
            _prepared.Remove(_prepared.MinBy(pair => pair.Value.PreparedUtc).Key);
    }

    private sealed class ProviderLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IRoslynCompilerProvider).Assembly.GetName().Name) return typeof(IRoslynCompilerProvider).Assembly;
            var path = Path.Combine(directory, name.Name + ".dll"); return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
    private sealed class DynamicLoadContext : AssemblyLoadContext
    {
        public DynamicLoadContext() : base(isCollectible: true) { }
        protected override Assembly? Load(AssemblyName name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
    }
    private sealed class CompiledEntry(DynamicLoadContext context, MethodInfo method) : IDisposable
    {
        public MethodInfo Method { get; } = method;
        public static CompiledEntry Load(CompileResult compiled)
        {
            var context = new DynamicLoadContext(); using var pe = new MemoryStream(compiled.AssemblyBytes!); using var pdb = new MemoryStream(compiled.PdbBytes!);
            var assembly = context.LoadFromStream(pe, pdb); var method = assembly.GetType("RevitMcp.Dynamic.EntryPoint")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
            return new(context, method);
        }
        public void Dispose() => context.Unload();
    }
}
