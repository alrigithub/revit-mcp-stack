using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitMcp.Bridge;

// When "Allow arbitrary code" is off, incoming script source may still run if it
// is content-identical to an enabled saved-tool script on disk: the operator
// vetted those files, so matching content — not a spoofable request flag — is
// the proof that a request is a saved tool rather than agent-authored code.
internal static class CodeGate
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Modes = ["read", "auto", "manual", "group"];
    private static readonly HashSet<string> ParamTypes = ["string", "integer", "number", "boolean", "array", "object"];

    public static bool IsVetted(LocalSettings settings, string tool, string source)
    {
        if (settings.Error is not null || tool is not ("run_python" or "run_csharp")) return false;
        var extension = tool == "run_python" ? ".py" : ".cs";
        var normalized = Normalize(source);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in settings.SearchRoots)
        {
            var manifests = SafeEnumerate(root, "*.json");
            if (manifests is null) return false; // An unreadable higher root cannot safely fall through.
            foreach (var manifest in manifests)
            {
                var id = Path.ChangeExtension(Path.GetRelativePath(root, manifest), null);
                // Ownership is established even by a disabled or invalid manifest.
                if (!owned.Add(id)) continue;
                if (settings.IsPathDisabled(root) || File.Exists(Path.ChangeExtension(manifest, ".disabled"))) continue;
                if (!LocalSettingsStore.GroupsEnabled(root, Path.GetDirectoryName(manifest)!)) continue;
                try
                {
                    if (!ValidManifest(manifest, id, extension)) continue;
                    var script = Path.ChangeExtension(manifest, extension);
                    if (!File.Exists(script) || new FileInfo(script).Length > 100_000) continue;
                    var content = File.ReadAllText(script, new UTF8Encoding(false, true));
                    if (Normalize(content) == normalized) return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException) { }
            }
        }
        return false;
    }

    private static bool ValidManifest(string path, string id, string extension)
    {
        if (id.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => !NamePattern.IsMatch(part))) return false;
        using var json = JsonDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
        var raw = json.RootElement;
        if (raw.ValueKind != JsonValueKind.Object) return false;
        if (!raw.TryGetProperty("manifest_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1) return false;
        if (Text(raw, "name") != Path.GetFileNameWithoutExtension(path)) return false;
        var description = Text(raw, "description");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 500) return false;
        if (Text(raw, "engine") != (extension == ".py" ? "python" : "csharp")) return false;
        if (!Modes.Contains(Text(raw, "transaction_mode") ?? "")) return false;
        if (raw.TryGetProperty("timeout_ms", out var timeout)
            && (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out var ms) || ms < 1 || ms > 600_000)) return false;
        if (!raw.TryGetProperty("params", out var parameters)) return true;
        if (parameters.ValueKind != JsonValueKind.Array) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.ValueKind != JsonValueKind.Object) return false;
            var name = Text(parameter, "name");
            if (name is null || !NamePattern.IsMatch(name) || !names.Add(name)) return false;
            if (!ParamTypes.Contains(Text(parameter, "type") ?? "") || string.IsNullOrWhiteSpace(Text(parameter, "description"))) return false;
            if (!parameter.TryGetProperty("required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        }
        return true;
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string[]? SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // The Python MCP server reads scripts in text mode (universal newlines) while
    // this side preserves CRLF, so equality must ignore BOM and line-ending style.
    private static string Normalize(string text) =>
        text.TrimStart('\uFEFF').Replace("\r\n", "\n").TrimEnd();
}
