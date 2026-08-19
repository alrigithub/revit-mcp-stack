using Autodesk.Revit.UI;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RevitMcp.Bridge;

public sealed class ActivityPane : Page, IDockablePaneProvider
{
    public static readonly DockablePaneId PaneId = new(new Guid("DD31B077-D847-4E4E-BCF1-5B9B18A6B664"));

    private const string BackgroundKey = "RevitMcp.Background";
    private const string SurfaceKey = "RevitMcp.Surface";
    private const string SectionKey = "RevitMcp.Section";
    private const string HoverKey = "RevitMcp.Hover";
    private const string PressedKey = "RevitMcp.Pressed";
    private const string TextKey = "RevitMcp.Text";
    private const string SecondaryTextKey = "RevitMcp.SecondaryText";
    private const string MutedTextKey = "RevitMcp.MutedText";
    private const string BorderKey = "RevitMcp.Border";
    private const string AccentKey = "RevitMcp.Accent";
    private const string SuccessKey = "RevitMcp.Success";
    private const string WarningKey = "RevitMcp.Warning";
    private const string ErrorKey = "RevitMcp.Error";

    private readonly BridgeRuntime _runtime;
    private readonly TextBlock _project = ValueText();
    private readonly TextBlock _queue = ValueText();
    private readonly TextBlock _totals = ValueText();
    private readonly TextBlock _environment = ValueText();
    private readonly Ellipse _bridgeDot = StatusDot();
    private readonly Ellipse _pythonDot = StatusDot();
    private readonly Ellipse _csharpDot = StatusDot();
    private readonly ListBox _activity = new();
    private readonly Button _activityButton = new();
    private readonly Button _toolsButton = new();
    private readonly Button _savedButton = new();
    private readonly Button _settingsButton = new();
    private readonly Button _clearButton = new();
    private UITheme? _appliedTheme;
    private string? _lastDocumentTitle;
    private PaneView _view = PaneView.Activity;
    private DateTime _toolsManifestStamp;
    private DateTime _savedStamp;
    private DateTime _settingsStamp;
    private int _savedCount = -1;

    private enum PaneView { Activity, Tools, Saved, Settings }

    public ActivityPane(BridgeRuntime runtime)
    {
        _runtime = runtime;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ApplyTheme();
        SetResourceReference(BackgroundProperty, BackgroundKey);
        SetResourceReference(ForegroundProperty, TextKey);
        Content = BuildContent();
        ApplySegmentStyles();
        Refresh(null);
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = this;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
    }

    public void ApplyTheme()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(ApplyTheme);
            return;
        }

        UITheme theme;
        try { theme = UIThemeManager.CurrentTheme; }
        catch { theme = UITheme.Dark; }
        if (_appliedTheme == theme && Resources.Count > 0) return;

        _appliedTheme = theme;
        var palette = theme == UITheme.Light ? RevitPalette.Light : RevitPalette.Dark;
        Resources[BackgroundKey] = Brush(palette.Background);
        Resources[SurfaceKey] = Brush(palette.Surface);
        Resources[SectionKey] = Brush(palette.Section);
        Resources[HoverKey] = Brush(palette.Hover);
        Resources[PressedKey] = Brush(palette.Pressed);
        Resources[TextKey] = Brush(palette.Text);
        Resources[SecondaryTextKey] = Brush(palette.SecondaryText);
        Resources[MutedTextKey] = Brush(palette.MutedText);
        Resources[BorderKey] = Brush(palette.Border);
        Resources[AccentKey] = Brush(palette.Accent);
        Resources[SuccessKey] = Brush(palette.Success);
        Resources[WarningKey] = Brush(palette.Warning);
        Resources[ErrorKey] = Brush(palette.Error);
    }

    public void Refresh(string? documentTitle)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Refresh(documentTitle));
            return;
        }

        ApplyTheme();
        _lastDocumentTitle = documentTitle;
        var bridgeState = _runtime.State.ToString().ToLowerInvariant();
        var pythonState = _runtime.Providers.Capability;
        var csharpState = _runtime.Roslyn.Capability;

        ApplyStatus(_bridgeDot, bridgeState, "Bridge");
        ApplyStatus(_pythonDot, pythonState, "Python");
        ApplyStatus(_csharpDot, csharpState, "C#");
        _project.Text = documentTitle ?? "No active document";
        _project.ToolTip = _project.Text;
        var queued = _runtime.Queue.Count;
        _queue.Text = queued > 0 ? queued + " queued" : "";
        _queue.Visibility = queued > 0 ? Visibility.Visible : Visibility.Collapsed;

        var pyrevit = _runtime.Providers.PyRevitVersion;
        _environment.Text = "MCP v" + BridgeRuntime.ProductVersion + " · Revit " + _runtime.RevitYear
            + (pyrevit is null ? "" : " · pyRevit " + ShortVersion(pyrevit));
        _environment.ToolTip = pyrevit is null ? "pyRevit provider has not registered in this session." : null;

        if (_view == PaneView.Tools)
        {
            RefreshTools();
            return;
        }
        if (_view == PaneView.Saved)
        {
            RefreshSaved();
            return;
        }
        if (_view == PaneView.Settings)
        {
            RefreshSettings();
            return;
        }

        var rows = BuildRows(_runtime.Log.Entries(200));
        var finished = rows.OfType<RequestRow>().Where(row => row.Terminal is not null).ToArray();
        var execSeconds = finished.Sum(row => row.Terminal!.ElapsedMs ?? 0) / 1000.0;
        var gapSeconds = rows.OfType<RequestRow>().Where(row => row.Gap is not null).Sum(row => row.Gap!.Value.TotalSeconds);
        _totals.Text = finished.Length == 0 ? "" :
            finished.Length + " calls · exec " + FormatSeconds(execSeconds) + (gapSeconds > 0 ? " · gaps " + FormatSeconds(gapSeconds) : "");

        _activity.Items.Clear();
        foreach (var row in Enumerable.Reverse(rows).Take(40))
            _activity.Items.Add(row is RequestRow request ? RequestItem(request) : EventItem((OperationalLogEntry)row));
        if (_activity.Items.Count == 0)
            _activity.Items.Add(EmptyActivityItem());
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = ThemedBorder(SurfaceKey, new Thickness(0), new Thickness(10, 8, 10, 8));
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _project.FontSize = 13;
        _project.FontWeight = FontWeights.SemiBold;
        _project.SetResourceReference(TextBlock.ForegroundProperty, TextKey);
        headerGrid.Children.Add(_project);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        chips.Children.Add(StatusChip(_bridgeDot, "Bridge"));
        chips.Children.Add(StatusChip(_pythonDot, "Python"));
        chips.Children.Add(StatusChip(_csharpDot, "C#"));
        Grid.SetColumn(chips, 1);
        headerGrid.Children.Add(chips);
        _queue.FontSize = 11;
        _queue.Margin = new Thickness(10, 0, 0, 0);
        _queue.SetResourceReference(TextBlock.ForegroundProperty, WarningKey);
        Grid.SetColumn(_queue, 2);
        headerGrid.Children.Add(_queue);
        header.Child = headerGrid;
        root.Children.Add(header);

        var controls = ThemedBorder(SectionKey, new Thickness(0), new Thickness(10, 5, 10, 5));
        var controlsGrid = new Grid();
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _totals.FontSize = 10;
        _totals.VerticalAlignment = VerticalAlignment.Center;
        _totals.Margin = new Thickness(0, 0, 8, 0);
        _totals.SetResourceReference(TextBlock.ForegroundProperty, MutedTextKey);
        controlsGrid.Children.Add(_totals);
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ConfigureSegment(_activityButton, "Activity");
        _activityButton.Click += (_, _) => SetView(PaneView.Activity);
        Grid.SetColumn(_activityButton, 1);
        controlsGrid.Children.Add(_activityButton);
        ConfigureSegment(_toolsButton, "Tools");
        _toolsButton.ToolTip = "MCP tools and the exact descriptions the LLM receives.";
        _toolsButton.Margin = new Thickness(1, 0, 0, 0);
        _toolsButton.Click += (_, _) => SetView(PaneView.Tools);
        Grid.SetColumn(_toolsButton, 2);
        controlsGrid.Children.Add(_toolsButton);
        ConfigureSegment(_savedButton, "Saved");
        _savedButton.ToolTip = "Saved tools: proven scripts promoted to reusable named tools on disk.";
        _savedButton.Margin = new Thickness(1, 0, 0, 0);
        _savedButton.Click += (_, _) => SetView(PaneView.Saved);
        Grid.SetColumn(_savedButton, 3);
        controlsGrid.Children.Add(_savedButton);
        ConfigureSegment(_settingsButton, "Settings");
        _settingsButton.ToolTip = "Saved-tool location and runtime MCP tool controls.";
        _settingsButton.Margin = new Thickness(1, 0, 0, 0);
        _settingsButton.Click += (_, _) => SetView(PaneView.Settings);
        Grid.SetColumn(_settingsButton, 4);
        controlsGrid.Children.Add(_settingsButton);
        controls.Child = controlsGrid;
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        _activity.Background = Brushes.Transparent;
        _activity.BorderThickness = new Thickness(0);
        _activity.Padding = new Thickness(6, 2, 6, 2);
        _activity.FontSize = 11;
        _activity.ItemContainerStyle = ActivityItemStyle();
        _activity.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _activity.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        Grid.SetRow(_activity, 2);
        root.Children.Add(_activity);

        var footer = ThemedBorder(SurfaceKey, new Thickness(0, 1, 0, 0), new Thickness(10, 5, 10, 5));
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _environment.FontSize = 10;
        _environment.VerticalAlignment = VerticalAlignment.Center;
        _environment.SetResourceReference(TextBlock.ForegroundProperty, MutedTextKey);
        footerGrid.Children.Add(_environment);
        _clearButton.Content = "Clear logs";
        _clearButton.MinWidth = 64;
        _clearButton.Height = 20;
        _clearButton.Padding = new Thickness(8, 0, 8, 0);
        _clearButton.FontSize = 10;
        _clearButton.Style = FlatButtonStyle();
        _clearButton.Click += (_, _) => { _runtime.Log.Clear(); Refresh(_lastDocumentTitle); };
        Grid.SetColumn(_clearButton, 1);
        footerGrid.Children.Add(_clearButton);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private static void ConfigureSegment(Button button, string label)
    {
        button.Content = label;
        button.Width = 58;
        button.Height = 22;
        button.FontSize = 10;
        button.Padding = new Thickness(0);
    }

    private void SetView(PaneView view)
    {
        if (_view == view) return;
        _view = view;
        _toolsManifestStamp = default;
        _savedStamp = default;
        _settingsStamp = default;
        _savedCount = -1;
        ApplySegmentStyles();
        Refresh(_lastDocumentTitle);
    }

    private void ApplySegmentStyles()
    {
        _activityButton.Style = _view == PaneView.Activity ? ActiveSegmentStyle() : FlatButtonStyle();
        _toolsButton.Style = _view == PaneView.Tools ? ActiveSegmentStyle() : FlatButtonStyle();
        _savedButton.Style = _view == PaneView.Saved ? ActiveSegmentStyle() : FlatButtonStyle();
        _settingsButton.Style = _view == PaneView.Settings ? ActiveSegmentStyle() : FlatButtonStyle();
    }

    private StackPanel StatusChip(Ellipse dot, string label)
    {
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        chip.Children.Add(dot);
        var text = ThemedText(label, SecondaryTextKey);
        text.FontSize = 11;
        text.VerticalAlignment = VerticalAlignment.Center;
        chip.Children.Add(text);
        return chip;
    }

    private sealed class RequestRow
    {
        public required OperationalLogEntry Admitted { get; init; }
        public OperationalLogEntry? Terminal { get; set; }
        public TimeSpan? Gap { get; init; }
    }

    private static List<object> BuildRows(OperationalLogEntry[] entries)
    {
        var rows = new List<object>();
        var open = new Dictionary<string, RequestRow>();
        DateTimeOffset? lastTerminalUtc = null;
        foreach (var entry in entries)
        {
            if (entry.Event == "admitted" && entry.RequestId is not null)
            {
                var gap = lastTerminalUtc is null ? (TimeSpan?)null : entry.TimestampUtc - lastTerminalUtc;
                var row = new RequestRow { Admitted = entry, Gap = gap > TimeSpan.Zero ? gap : null };
                open[entry.RequestId] = row;
                rows.Add(row);
            }
            else if (entry.Event == "terminal" && entry.RequestId is not null)
            {
                if (open.Remove(entry.RequestId, out var row)) row.Terminal = entry;
                else rows.Add(new RequestRow { Admitted = entry, Terminal = entry });
                lastTerminalUtc = entry.TimestampUtc;
            }
            else rows.Add(entry);
        }
        return rows;
    }

    private ListBoxItem RequestItem(RequestRow row)
    {
        var terminal = row.Terminal;
        var state = terminal?.State ?? "queued";
        var item = new ListBoxItem();
        var panel = new StackPanel();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var symbol = ThemedText(state == "succeeded" ? "✓" : state == "queued" ? "•" : "×", StatusResource(state));
        symbol.FontWeight = FontWeights.SemiBold;
        line.Children.Add(symbol);

        var time = ThemedText(row.Admitted.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), MutedTextKey);
        Grid.SetColumn(time, 1);
        line.Children.Add(time);

        var tool = ThemedText(row.Admitted.Tool ?? "?", TextKey);
        tool.FontWeight = FontWeights.SemiBold;
        tool.TextTrimming = TextTrimming.CharacterEllipsis;
        tool.Margin = new Thickness(7, 0, 0, 0);
        tool.ToolTip = ToolTipText(row, state);
        Grid.SetColumn(tool, 2);
        line.Children.Add(tool);

        var mode = terminal?.TransactionMode ?? row.Admitted.TransactionMode;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            var modeText = ThemedText(mode, MutedTextKey);
            modeText.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(modeText, 3);
            line.Children.Add(modeText);
        }

        if (terminal?.ElapsedMs is not null)
        {
            var elapsed = ThemedText(FormatMs(terminal.ElapsedMs.Value), SecondaryTextKey);
            elapsed.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(elapsed, 4);
            line.Children.Add(elapsed);
        }

        if (row.Gap is not null)
        {
            var gap = ThemedText("+" + FormatSpan(row.Gap.Value), MutedTextKey);
            gap.Margin = new Thickness(8, 0, 0, 0);
            gap.ToolTip = "Time since the previous request finished (inference, client, or user time).";
            Grid.SetColumn(gap, 5);
            line.Children.Add(gap);
        }
        panel.Children.Add(line);

        var error = terminal?.RedactedError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            var errorText = ThemedText(error, ErrorKey);
            errorText.FontSize = 10;
            errorText.Margin = new Thickness(15, 2, 0, 0);
            errorText.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(errorText);
        }
        item.Content = panel;
        return item;
    }

    private ListBoxItem EventItem(OperationalLogEntry entry)
    {
        var item = new ListBoxItem();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());

        var symbol = ThemedText("•", StatusResource(entry.State ?? ""));
        symbol.FontWeight = FontWeights.SemiBold;
        line.Children.Add(symbol);

        var time = ThemedText(entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), MutedTextKey);
        Grid.SetColumn(time, 1);
        line.Children.Add(time);

        var text = DisplayState(entry.Event)
            + (entry.State is null ? "" : " · " + DisplayState(entry.State))
            + (string.IsNullOrWhiteSpace(entry.Provider) ? "" : " · " + entry.Provider);
        var description = ThemedText(text, SecondaryTextKey);
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        description.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetColumn(description, 2);
        line.Children.Add(description);
        item.Content = line;
        return item;
    }

    private static string ToolTipText(RequestRow row, string state)
    {
        var parts = new List<string> { row.Admitted.Tool ?? "?", DisplayState(state) };
        if (row.Terminal is not null && row.Terminal != row.Admitted)
            parts.Add("queued " + FormatSpan(row.Terminal.TimestampUtc - row.Admitted.TimestampUtc - TimeSpan.FromMilliseconds(row.Terminal.ElapsedMs ?? 0)));
        if (row.Admitted.RequestId is not null) parts.Add(row.Admitted.RequestId);
        return string.Join("  ·  ", parts);
    }

    private sealed record ToolsManifest(
        [property: System.Text.Json.Serialization.JsonPropertyName("written_utc")] string? WrittenUtc,
        [property: System.Text.Json.Serialization.JsonPropertyName("tools")] List<ManifestTool>? Tools);

    private sealed record ManifestTool(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("description")] string? Description,
        [property: System.Text.Json.Serialization.JsonPropertyName("params")] List<string>? Params);

    private static string ToolsManifestPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcp", "mcp-tools.json");

    private void RefreshTools()
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(ToolsManifestPath); }
        catch { stamp = default; }
        if (stamp == _toolsManifestStamp && _activity.Items.Count > 0) return;
        _toolsManifestStamp = stamp;

        _activity.Items.Clear();
        ToolsManifest? manifest = null;
        try
        {
            if (File.Exists(ToolsManifestPath))
                manifest = System.Text.Json.JsonSerializer.Deserialize<ToolsManifest>(File.ReadAllText(ToolsManifestPath));
        }
        catch { manifest = null; }

        if (manifest?.Tools is not { Count: > 0 })
        {
            _totals.Text = "";
            var item = new ListBoxItem();
            var message = ThemedText("No MCP tools manifest found. The MCP server writes it at startup; start or restart a client session.", SecondaryTextKey);
            message.TextWrapping = TextWrapping.Wrap;
            message.Margin = new Thickness(4, 8, 4, 8);
            item.Content = message;
            _activity.Items.Add(item);
            return;
        }

        var written = "";
        if (DateTimeOffset.TryParse(manifest.WrittenUtc, out var writtenUtc))
            written = " · updated " + writtenUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        _totals.Text = manifest.Tools.Count + " tools" + written;
        foreach (var tool in manifest.Tools)
            _activity.Items.Add(ToolItem(tool));
    }

    private ListBoxItem ToolItem(ManifestTool tool)
    {
        var item = new ListBoxItem();
        var panel = new StackPanel();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = ThemedText(tool.Name ?? "?", TextKey);
        name.FontWeight = FontWeights.SemiBold;
        line.Children.Add(name);
        if (!string.IsNullOrWhiteSpace(tool.Name))
        {
            var enabled = !LocalSettingsStore.Load().DisabledMcpTools.Contains(tool.Name);
            var toggle = SmallActionButton(enabled ? "Disable" : "Enable");
            toggle.ToolTip = enabled
                ? "Disable this MCP tool immediately. Reconnect the AI client to refresh its visible tool list."
                : "Enable this MCP tool. Reconnect the AI client to refresh its visible tool list.";
            toggle.Click += (_, _) =>
            {
                LocalSettingsStore.SetMcpToolEnabled(tool.Name, !enabled);
                _toolsManifestStamp = default;
                RefreshTools();
            };
            Grid.SetColumn(toggle, 1);
            line.Children.Add(toggle);
        }
        panel.Children.Add(line);
        if (tool.Params is { Count: > 0 })
        {
            var parameters = ThemedText(string.Join(", ", tool.Params), MutedTextKey);
            parameters.FontSize = 10;
            parameters.TextWrapping = TextWrapping.Wrap;
            parameters.Margin = new Thickness(0, 1, 0, 0);
            panel.Children.Add(parameters);
        }
        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            var description = ThemedText(tool.Description, SecondaryTextKey);
            description.FontSize = 10;
            description.TextWrapping = TextWrapping.Wrap;
            description.Margin = new Thickness(0, 3, 0, 0);
            panel.Children.Add(description);
        }
        item.Content = panel;
        return item;
    }

    private sealed record SavedManifest(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("description")] string? Description,
        [property: System.Text.Json.Serialization.JsonPropertyName("engine")] string? Engine,
        [property: System.Text.Json.Serialization.JsonPropertyName("transaction_mode")] string? TransactionMode,
        [property: System.Text.Json.Serialization.JsonPropertyName("params")] List<SavedParam>? Params);

    private sealed record SavedParam(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string? Type,
        [property: System.Text.Json.Serialization.JsonPropertyName("required")] bool Required);

    private static string SavedToolsRoot => LocalSettingsStore.Load().SavedToolsRoot;

    private void RefreshSaved()
    {
        var roots = LocalSettingsStore.Load().SearchRoots;
        var manifestsByRoot = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var markers = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                manifestsByRoot[root] = Directory.Exists(root) ? Directory.GetFiles(root, "*.json", SearchOption.AllDirectories) : [];
            }
            catch { manifestsByRoot[root] = []; }
            try { if (Directory.Exists(root)) markers.AddRange(Directory.GetFiles(root, "*.disabled", SearchOption.AllDirectories)); }
            catch { }
        }
        var allManifests = manifestsByRoot.Values.SelectMany(paths => paths).ToArray();
        var stamp = default(DateTime);
        foreach (var path in allManifests.Concat(markers).Append(LocalSettingsStore.SettingsPath))
        {
            if (!File.Exists(path)) continue;
            var written = File.GetLastWriteTimeUtc(path);
            if (written > stamp) stamp = written;
        }
        if (stamp == _savedStamp && allManifests.Length == _savedCount && _activity.Items.Count > 0) return;
        _savedStamp = stamp;
        _savedCount = allManifests.Length;

        _activity.Items.Clear();
        var shown = 0;
        foreach (var root in roots)
        {
            var manifests = manifestsByRoot[root];
            if (roots.Count > 1)
            {
                var header = new ListBoxItem { IsHitTestVisible = false };
                var label = ThemedText(root, SecondaryTextKey);
                label.TextWrapping = TextWrapping.Wrap;
                label.Margin = new Thickness(4, 8, 4, 2);
                header.Content = label;
                _activity.Items.Add(header);
            }
            foreach (var path in manifests.Where(path => string.Equals(System.IO.Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(System.IO.Path.GetFileName))
            {
                _activity.Items.Add(SavedItem(root, path, ReadSavedManifest(path)));
                shown++;
            }

            var groupSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifestPath in manifests)
            {
                var directory = System.IO.Path.GetDirectoryName(manifestPath);
                while (directory is not null && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
                {
                    groupSet.Add(directory);
                    directory = Directory.GetParent(directory)?.FullName;
                }
            }
            var groups = groupSet
                .OrderBy(directory => System.IO.Path.GetRelativePath(root, directory), StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                _activity.Items.Add(SavedGroupItem(root, group));
                foreach (var path in manifests.Where(path => string.Equals(System.IO.Path.GetDirectoryName(path), group, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(System.IO.Path.GetFileName))
                {
                    _activity.Items.Add(SavedItem(root, path, ReadSavedManifest(path)));
                    shown++;
                }
            }
        }
        _totals.Text = shown == 0 ? "" : shown + " saved tools";
        if (shown == 0)
        {
            _activity.Items.Clear();
            var item = new ListBoxItem();
            var message = ThemedText("No saved tools yet. Proven scripts land here as manifest + script pairs in " + roots[0] + " and are usable immediately.", SecondaryTextKey);
            message.TextWrapping = TextWrapping.Wrap;
            message.Margin = new Thickness(4, 8, 4, 8);
            item.Content = message;
            _activity.Items.Add(item);
        }
    }

    private static SavedManifest? ReadSavedManifest(string path)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<SavedManifest>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private ListBoxItem SavedGroupItem(string root, string directory)
    {
        var item = new ListBoxItem();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var relative = System.IO.Path.GetRelativePath(root, directory).Replace(System.IO.Path.DirectorySeparatorChar, '/');
        var inheritedEnabled = LocalSettingsStore.GroupsEnabled(root, Directory.GetParent(directory)?.FullName ?? root);
        var directlyEnabled = !File.Exists(System.IO.Path.Combine(directory, ".disabled"));
        var name = ThemedText(relative, directlyEnabled && inheritedEnabled ? TextKey : MutedTextKey);
        name.FontWeight = FontWeights.SemiBold;
        line.Children.Add(name);
        var toggle = SmallActionButton(directlyEnabled ? "Disable group" : "Enable group");
        toggle.IsEnabled = inheritedEnabled;
        toggle.Click += (_, _) =>
        {
            LocalSettingsStore.SetGroupEnabled(directory, !directlyEnabled);
            _savedCount = -1;
            RefreshSaved();
        };
        Grid.SetColumn(toggle, 1);
        line.Children.Add(toggle);
        item.Content = line;
        return item;
    }

    private ListBoxItem SavedItem(string root, string manifestPath, SavedManifest? manifest)
    {
        var item = new ListBoxItem();
        var panel = new StackPanel();
        if (manifest?.Name is null)
        {
            var broken = ThemedText(System.IO.Path.GetRelativePath(root, manifestPath) + " — invalid manifest", ErrorKey);
            broken.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(broken);
            item.Content = panel;
            return item;
        }

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var groupsEnabled = LocalSettingsStore.GroupsEnabled(root, System.IO.Path.GetDirectoryName(manifestPath)!);
        var directlyEnabled = !File.Exists(System.IO.Path.ChangeExtension(manifestPath, ".disabled"));
        var name = ThemedText(manifest.Name, TextKey);
        name.FontWeight = FontWeights.SemiBold;
        if (!groupsEnabled || !directlyEnabled) name.SetResourceReference(TextBlock.ForegroundProperty, MutedTextKey);
        line.Children.Add(name);
        var meta = ThemedText((manifest.Engine ?? "?") + " · " + (manifest.TransactionMode ?? "?"), MutedTextKey);
        meta.FontSize = 10;
        meta.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(meta, 1);
        line.Children.Add(meta);
        var toggle = SmallActionButton(directlyEnabled ? "Disable" : "Enable");
        toggle.IsEnabled = groupsEnabled;
        toggle.Click += (_, _) =>
        {
            LocalSettingsStore.SetSavedToolEnabled(manifestPath, !directlyEnabled);
            _savedCount = -1;
            RefreshSaved();
        };
        Grid.SetColumn(toggle, 2);
        line.Children.Add(toggle);
        panel.Children.Add(line);

        if (manifest.Params is { Count: > 0 })
        {
            var text = string.Join(", ", manifest.Params.Select(p => (p.Name ?? "?") + (p.Required ? "*" : "")));
            var parameters = ThemedText(text, MutedTextKey);
            parameters.FontSize = 10;
            parameters.TextWrapping = TextWrapping.Wrap;
            parameters.Margin = new Thickness(0, 1, 0, 0);
            panel.Children.Add(parameters);
        }
        if (!string.IsNullOrWhiteSpace(manifest.Description))
        {
            var description = ThemedText(manifest.Description, SecondaryTextKey);
            description.FontSize = 10;
            description.TextWrapping = TextWrapping.Wrap;
            description.Margin = new Thickness(0, 3, 0, 0);
            panel.Children.Add(description);
        }
        item.Content = panel;
        return item;
    }

    private void RefreshSettings()
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(LocalSettingsStore.SettingsPath); }
        catch { stamp = default; }
        if (stamp == _settingsStamp && _activity.Items.Count > 0) return;
        _settingsStamp = stamp;

        var settings = LocalSettingsStore.Load();
        _activity.Items.Clear();
        _totals.Text = settings.Error is null ? "" : "Invalid settings";

        var item = new ListBoxItem();
        var panel = new StackPanel();
        var title = ThemedText("Saved tools folder", TextKey);
        title.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(title);
        var note = ThemedText("The model reads this path from list_saved_tools before creating files.", SecondaryTextKey);
        note.FontSize = 10;
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 2, 0, 6);
        panel.Children.Add(note);

        var pathBox = new System.Windows.Controls.TextBox
        {
            Text = settings.SavedToolsRoot,
            Padding = new Thickness(5, 3, 5, 3),
            BorderThickness = new Thickness(1)
        };
        pathBox.SetResourceReference(Control.BackgroundProperty, SurfaceKey);
        pathBox.SetResourceReference(Control.ForegroundProperty, TextKey);
        pathBox.SetResourceReference(Control.BorderBrushProperty, BorderKey);
        panel.Children.Add(pathBox);

        var save = SmallActionButton("Save folder");
        save.HorizontalAlignment = HorizontalAlignment.Left;
        save.Margin = new Thickness(0, 6, 0, 0);
        save.Click += (_, _) =>
        {
            try
            {
                LocalSettingsStore.SetSavedToolsRoot(pathBox.Text);
                pathBox.Text = LocalSettingsStore.Load().SavedToolsRoot;
                _totals.Text = "Settings saved";
                _savedCount = -1;
                _settingsStamp = File.GetLastWriteTimeUtc(LocalSettingsStore.SettingsPath);
            }
            catch (Exception ex)
            {
                _totals.Text = "Settings error";
                pathBox.ToolTip = ex.Message;
            }
        };
        panel.Children.Add(save);

        if (settings.Error is not null)
        {
            var error = ThemedText(settings.Error, ErrorKey);
            error.TextWrapping = TextWrapping.Wrap;
            error.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(error);
        }
        var reconnect = ThemedText("Built-in MCP tool switches are on the Tools page. Reconnect the AI client after changing them so its visible tool list refreshes.", MutedTextKey);
        reconnect.FontSize = 10;
        reconnect.TextWrapping = TextWrapping.Wrap;
        reconnect.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(reconnect);
        item.Content = panel;
        _activity.Items.Add(item);
    }

    private Button SmallActionButton(string label)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 48,
            Height = 20,
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 10,
            Style = FlatButtonStyle()
        };
        return button;
    }

    private static string FormatMs(long ms) => ms < 1000 ? ms + "ms" : FormatSeconds(ms / 1000.0);

    private static string FormatSeconds(double seconds) =>
        seconds < 10 ? seconds.ToString("0.0") + "s"
        : seconds < 60 ? seconds.ToString("0") + "s"
        : (int)(seconds / 60) + "m" + ((int)seconds % 60) + "s";

    private static string FormatSpan(TimeSpan span) =>
        span < TimeSpan.Zero ? "0s" : FormatSeconds(span.TotalSeconds);

    private static string ShortVersion(string version)
    {
        var trimmed = version.Split('+')[0];
        return trimmed.Length > 16 ? trimmed[..16] : trimmed;
    }

    private ListBoxItem EmptyActivityItem()
    {
        var item = new ListBoxItem();
        var message = ThemedText("No activity yet", SecondaryTextKey);
        message.Margin = new Thickness(4, 8, 4, 8);
        item.Content = message;
        return item;
    }

    private Style FlatButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SurfaceKey)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SecondaryTextKey)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(HoverKey)));
        hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TextKey)));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(PressedKey)));
        style.Triggers.Add(pressed);
        return style;
    }

    private Style ActiveSegmentStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(AccentKey)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        return style;
    }

    private Style ActivityItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(TextKey)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(BorderKey)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 4, 4, 4)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(HoverKey)));
        style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(PressedKey)));
        style.Triggers.Add(selected);
        return style;
    }

    private Border ThemedBorder(string background, Thickness borderThickness, Thickness padding)
    {
        var result = new Border { BorderThickness = borderThickness, Padding = padding };
        result.SetResourceReference(Border.BackgroundProperty, background);
        result.SetResourceReference(Border.BorderBrushProperty, BorderKey);
        return result;
    }

    private TextBlock ThemedText(string text, string resource)
    {
        var result = new TextBlock { Text = text };
        result.SetResourceReference(TextBlock.ForegroundProperty, resource);
        return result;
    }

    private static TextBlock ValueText() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static Ellipse StatusDot() => new()
    {
        Width = 8,
        Height = 8,
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private void ApplyStatus(Ellipse dot, string state, string label)
    {
        dot.SetResourceReference(Shape.FillProperty, StatusResource(state));
        dot.ToolTip = label + ": " + DisplayState(state);
    }

    private static string StatusResource(string state) => state switch
    {
        "on" or "available" or "succeeded" => SuccessKey,
        "starting" or "stopping" or "disabled" or "expected_but_not_registered" or "queued" or "running" => WarningKey,
        "failed" or "off" => ErrorKey,
        _ => MutedTextKey
    };

    private static string DisplayState(string state) => string.Join(" ", state.Split('_')) switch
    {
        "on" => "On",
        "off" => "Off",
        var value => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value)
    };

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record RevitPalette(
        Color Background,
        Color Surface,
        Color Section,
        Color Hover,
        Color Pressed,
        Color Text,
        Color SecondaryText,
        Color MutedText,
        Color Border,
        Color Accent,
        Color Success,
        Color Warning,
        Color Error)
    {
        public static RevitPalette Dark { get; } = new(
            Color.FromRgb(42, 46, 54),
            Color.FromRgb(50, 55, 64),
            Color.FromRgb(55, 61, 71),
            Color.FromRgb(52, 66, 86),
            Color.FromRgb(38, 52, 70),
            Color.FromRgb(242, 244, 247),
            Color.FromRgb(191, 199, 210),
            Color.FromRgb(145, 156, 171),
            Color.FromRgb(76, 84, 96),
            Color.FromRgb(58, 124, 186),
            Color.FromRgb(67, 190, 105),
            Color.FromRgb(224, 168, 59),
            Color.FromRgb(220, 101, 101));

        public static RevitPalette Light { get; } = new(
            Color.FromRgb(243, 243, 243),
            Color.FromRgb(250, 250, 250),
            Color.FromRgb(229, 229, 229),
            Color.FromRgb(225, 237, 247),
            Color.FromRgb(207, 226, 241),
            Color.FromRgb(35, 38, 42),
            Color.FromRgb(75, 82, 90),
            Color.FromRgb(105, 115, 126),
            Color.FromRgb(198, 198, 198),
            Color.FromRgb(25, 118, 185),
            Color.FromRgb(46, 125, 50),
            Color.FromRgb(178, 106, 0),
            Color.FromRgb(179, 38, 30));
    }
}
