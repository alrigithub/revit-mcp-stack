using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RevitMcp.Bridge;

internal sealed record LocalSettings(string SavedToolsRoot, List<string> SavedToolsPaths, HashSet<string> DisabledMcpTools,
    bool BypassDialogs = true, bool AllowArbitraryCode = false, HashSet<string>? DisabledToolPaths = null,
    Dictionary<string, JsonElement>? Extra = null, string? Error = null)
{
    /// <summary>Ordered search roots: the primary (writable) root first, then saved_tools_paths, deduplicated.</summary>
    public IReadOnlyList<string> SearchRoots =>
        new[] { SavedToolsRoot }.Concat(SavedToolsPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool IsPathDisabled(string root) =>
        DisabledToolPaths is not null && DisabledToolPaths.Contains(Path.GetFullPath(root));
}

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
            if (!File.Exists(SettingsPath)) return new(DefaultSavedToolsRoot, [], []);
            try
            {
                var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath));
                var configured = string.IsNullOrWhiteSpace(stored?.SavedToolsRoot)
                    ? DefaultSavedToolsRoot
                    : Environment.ExpandEnvironmentVariables(stored.SavedToolsRoot);
                if (!Path.IsPathFullyQualified(configured)) throw new InvalidDataException("saved_tools_root must be an absolute path.");
                var root = Path.GetFullPath(configured);
                var paths = new List<string>();
                foreach (var entry in stored?.SavedToolsPaths ?? [])
                {
                    if (string.IsNullOrWhiteSpace(entry)) throw new InvalidDataException("saved_tools_paths entries must be non-empty absolute paths.");
                    var expanded = Environment.ExpandEnvironmentVariables(entry);
                    if (!Path.IsPathFullyQualified(expanded)) throw new InvalidDataException("saved_tools_paths entries must be absolute paths.");
                    paths.Add(Path.GetFullPath(expanded));
                }
                var disabledNames = stored?.DisabledMcpTools ?? [];
                if (disabledNames.Any(name => !ToolNamePattern().IsMatch(name)))
                    throw new InvalidDataException("disabled_mcp_tools contains an invalid tool name.");
                var disabled = disabledNames.ToHashSet(StringComparer.Ordinal);
                var disabledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in stored?.DisabledToolPaths ?? [])
                {
                    if (string.IsNullOrWhiteSpace(entry)) throw new InvalidDataException("disabled_tool_paths entries must be non-empty absolute paths.");
                    var expanded = Environment.ExpandEnvironmentVariables(entry);
                    if (!Path.IsPathFullyQualified(expanded)) throw new InvalidDataException("disabled_tool_paths entries must be absolute paths.");
                    disabledPaths.Add(Path.GetFullPath(expanded));
                }
                return new(root, paths, disabled, stored?.BypassDialogs ?? true, stored?.AllowArbitraryCode ?? false, disabledPaths, stored?.Extra);
            }
            catch (Exception ex)
            {
                return new(DefaultSavedToolsRoot, [], [], Error: ex.Message);
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
        Save(settings with { SavedToolsRoot = root });
    }

    public static void SetSavedToolsPaths(IReadOnlyList<string> values)
    {
        var paths = new List<string>();
        foreach (var value in values)
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!Path.IsPathFullyQualified(expanded)) throw new InvalidDataException("Every extra tools path must be an absolute folder path.");
            paths.Add(Path.GetFullPath(expanded));
        }
        var settings = Load();
        Save(settings with { SavedToolsPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
    }

    public static void SetToolPathEnabled(string path, bool enabled)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        var settings = Load();
        var disabled = new HashSet<string>(settings.DisabledToolPaths ?? [], StringComparer.OrdinalIgnoreCase);
        if (enabled) disabled.Remove(full);
        else disabled.Add(full);
        Save(settings with { DisabledToolPaths = disabled });
    }

    public static void SetBypassDialogs(bool enabled)
    {
        var settings = Load();
        Save(settings with { BypassDialogs = enabled });
    }

    public static void SetAllowArbitraryCode(bool enabled)
    {
        var settings = Load();
        Save(settings with { AllowArbitraryCode = enabled });
    }

    public static void SetMcpToolEnabled(string name, bool enabled)
    {
        if (!ToolNamePattern().IsMatch(name)) throw new InvalidDataException("Invalid MCP tool name.");
        var settings = Load();
        var disabled = new HashSet<string>(settings.DisabledMcpTools, StringComparer.Ordinal);
        if (enabled) disabled.Remove(name);
        else disabled.Add(name);
        Save(settings with { DisabledMcpTools = disabled });
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
            var stored = new StoredSettings(settings.SavedToolsRoot, settings.SavedToolsPaths, settings.DisabledMcpTools.Order().ToList(),
                settings.BypassDialogs, settings.AllowArbitraryCode, settings.DisabledToolPaths?.Order().ToList()) { Extra = settings.Extra };
            var staging = SettingsPath + ".tmp";
            File.WriteAllText(staging, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(staging, SettingsPath, true);
        }
    }

    // Extra round-trips keys this build does not know about, so a settings write
    // from the pane can never wipe a newer or Python-side key.
    private sealed record StoredSettings(
        [property: JsonPropertyName("saved_tools_root")] string? SavedToolsRoot,
        [property: JsonPropertyName("saved_tools_paths")] List<string>? SavedToolsPaths,
        [property: JsonPropertyName("disabled_mcp_tools")] List<string>? DisabledMcpTools,
        [property: JsonPropertyName("bypass_dialogs")] bool? BypassDialogs = null,
        [property: JsonPropertyName("allow_arbitrary_code")] bool? AllowArbitraryCode = null,
        [property: JsonPropertyName("disabled_tool_paths")] List<string>? DisabledToolPaths = null)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; init; }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();
}
