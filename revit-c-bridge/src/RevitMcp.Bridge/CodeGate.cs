using System.IO;

namespace RevitMcp.Bridge;

// When "Allow arbitrary code" is off, incoming script source may still run if it
// is content-identical to an enabled saved-tool script on disk: the operator
// vetted those files, so matching content — not a spoofable request flag — is
// the proof that a request is a saved tool rather than agent-authored code.
internal static class CodeGate
{
    public static bool IsVetted(LocalSettings settings, string tool, string source)
    {
        var extension = tool == "run_python" ? ".py" : ".cs";
        var normalized = Normalize(source);
        foreach (var root in settings.SearchRoots)
        {
            foreach (var script in SafeEnumerate(root, "*" + extension))
            {
                if (!File.Exists(Path.ChangeExtension(script, ".json"))) continue;
                if (File.Exists(Path.ChangeExtension(script, ".disabled"))) continue;
                if (!LocalSettingsStore.GroupsEnabled(root, Path.GetDirectoryName(script)!)) continue;
                string content;
                try { content = File.ReadAllText(script); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (Normalize(content) == normalized) return true;
            }
        }
        return false;
    }

    private static string[] SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // The Python MCP server reads scripts in text mode (universal newlines) while
    // this side preserves CRLF, so equality must ignore BOM and line-ending style.
    private static string Normalize(string text) =>
        text.TrimStart('\uFEFF').Replace("\r\n", "\n").TrimEnd();
}
