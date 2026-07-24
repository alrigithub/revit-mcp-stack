using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RevitMcp.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RevitMcp.RoslynProvider;

public sealed class RoslynCompilerProvider : IRoslynCompilerProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CompileResult> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    public string AbiVersion => ProtocolConstants.RoslynAbi;
    public string ProviderVersion => "0.9.0+roslyn-4.11.0";

    public CompileResult Compile(CompileRequest request)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(request.Source);
        if (sourceBytes.Length > ProtocolConstants.MaxSourceBytes)
            return new(false, null, null, "[{\"code\":\"source_too_large\"}]", string.Empty);
        var manifest = string.Join("\n", request.ReferencePaths.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Source + "\n" + manifest + "\n" + request.RevitYear + "\n" + ProviderVersion + "\n" + AbiVersion + "\n" + request.CacheKeySeed))).ToLowerInvariant();
        lock (_gate) if (_cache.TryGetValue(key, out var cached)) return cached;

        var syntax = CSharpSyntaxTree.ParseText(Wrap(request.Source), new CSharpParseOptions(LanguageVersion.CSharp12), path: "generated-entry.cs", encoding: Encoding.UTF8);
        var references = request.ReferencePaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(Path.GetFullPath(path))).ToArray();
        var compilation = CSharpCompilation.Create("RevitMcp.Dynamic." + key, [syntax], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: false, deterministic: true, nullableContextOptions: NullableContextOptions.Enable));
        using var pe = new MemoryStream(); using var pdb = new MemoryStream();
        var emit = compilation.Emit(pe, pdb, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));
        var diagnostics = JsonSerializer.Serialize(emit.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).Take(200).Select(d =>
        {
            var span = d.Location.GetMappedLineSpan();
            return new { id = d.Id, severity = d.Severity.ToString().ToLowerInvariant(), message = d.GetMessage(), file = span.Path, line = span.StartLinePosition.Line + 1, column = span.StartLinePosition.Character + 1 };
        }));
        var result = emit.Success ? new CompileResult(true, pe.ToArray(), pdb.ToArray(), diagnostics, key) : new CompileResult(false, null, null, diagnostics, key);
        lock (_gate)
        {
            _cache[key] = result; _order.Enqueue(key);
            while (_order.Count > 64) _cache.Remove(_order.Dequeue());
        }
        return result;
    }

    private static string Wrap(string source) => $$"""
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace RevitMcp.Dynamic
{
    public static class EntryPoint
    {
        public static string Run(UIApplication uiapp, Document doc, UIDocument? uidoc, string requestJson)
        {
#line 1 "agent.cs"
{{source}}
#line default
        }
    }
}
""";
}
