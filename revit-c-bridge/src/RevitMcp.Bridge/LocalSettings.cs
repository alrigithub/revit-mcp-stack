using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RevitMcp.Bridge;

internal sealed record LocalSettings(string SavedToolsRoot, HashSet<string> DisabledMcpTools, string? Error = null);

internal static partial class LocalSettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string BaseRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcp");
    public static string SettingsPath => Path.Combine(BaseRoot, "settings.json");
    public static string DefaultSavedToolsRoot => Path.Combine(BaseRoot, "tools");

    public static LocalSettings Load()
    {
        lock (Gate)
        {
            if (!File.Exists(SettingsPath)) return new(DefaultSavedToolsRoot, []);
            try
            {
                var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath));
                var configured = string.IsNullOrWhiteSpace(stored?.SavedToolsRoot)
                    ? DefaultSavedToolsRoot
                    : Environment.ExpandEnvironmentVariables(stored.SavedToolsRoot);
                if (!Path.IsPathFullyQualified(configured)) throw new InvalidDataException("saved_tools_root must be an absolute path.");
                var root = Path.GetFullPath(configured);
                var disabledNames = stored?.DisabledMcpTools ?? [];
                if (disabledNames.Any(name => !ToolNamePattern().IsMatch(name)))
                    throw new InvalidDataException("disabled_mcp_tools contains an invalid tool name.");
                var disabled = disabledNames.ToHashSet(StringComparer.Ordinal);
                return new(root, disabled);
            }
            catch (Exception ex)
            {
                return new(DefaultSavedToolsRoot, [], ex.Message);
            }
        }
    }

    public static void SetSavedToolsRoot(string value)
    {
        var configured = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathFullyQualified(configured)) throw new InvalidDataException("Enter an absolute folder path.");
        var root = Path.GetFullPath(configured);
        Directory.CreateDirectory(root);
        var settings = Load();
        Save(new(root, settings.DisabledMcpTools));
    }

    public static void SetMcpToolEnabled(string name, bool enabled)
    {
        if (!ToolNamePattern().IsMatch(name)) throw new InvalidDataException("Invalid MCP tool name.");
        var settings = Load();
        var disabled = new HashSet<string>(settings.DisabledMcpTools, StringComparer.Ordinal);
        if (enabled) disabled.Remove(name);
        else disabled.Add(name);
        Save(new(settings.SavedToolsRoot, disabled));
    }

    public static bool GroupsEnabled(string root, string directory)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        while (!string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(current, ".disabled"))) return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || !IsBelowRoot(rootFull, parent)) return false;
            current = parent.TrimEnd(Path.DirectorySeparatorChar);
        }
        return true;
    }

    public static void SetGroupEnabled(string directory, bool enabled) =>
        SetMarker(Path.Combine(directory, ".disabled"), enabled);

    public static void SetSavedToolEnabled(string manifestPath, bool enabled) =>
        SetMarker(Path.ChangeExtension(manifestPath, ".disabled"), enabled);

    private static void SetMarker(string marker, bool enabled)
    {
        if (enabled)
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "");
        }
    }

    private static bool IsBelowRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void Save(LocalSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(BaseRoot);
            var stored = new StoredSettings(settings.SavedToolsRoot, settings.DisabledMcpTools.Order().ToList());
            var staging = SettingsPath + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(staging, SettingsPath, true);
        }
    }

    private sealed record StoredSettings(
        [property: JsonPropertyName("saved_tools_root")] string? SavedToolsRoot,
        [property: JsonPropertyName("disabled_mcp_tools")] List<string>? DisabledMcpTools);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();
}
