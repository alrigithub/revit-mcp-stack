using Autodesk.Revit.UI;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;

namespace RevitMcp.Bridge;

// Operator-facing configuration: execution policy and saved-tool locations.
// Lives on the ribbon (not in the Activity pane) so the pane can stay a pure
// live view of documents and work. Every control writes settings.json
// immediately; the bridge reads settings per call, so changes are live without
// a restart or bridge toggle.
public sealed class SettingsDialog : Window
{
    private readonly RevitPalette _palette;
    private readonly ListBox _paths = new();
    private readonly TextBox _rootBox = new();
    private readonly TextBox _newPathBox = new();
    private readonly TextBlock _status = new();

    public SettingsDialog(IntPtr revitWindow)
    {
        UITheme theme;
        try { theme = UIThemeManager.CurrentTheme; }
        catch { theme = UITheme.Dark; }
        _palette = theme == UITheme.Light ? RevitPalette.Light : RevitPalette.Dark;

        Title = "Revit MCP Settings";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        Background = Brush(_palette.Background);
        Foreground = Brush(_palette.Text);
        _ = new WindowInteropHelper(this) { Owner = revitWindow };

        var settings = LocalSettingsStore.Load();
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        root.Children.Add(SectionTitle("Execution policy", first: true));
        root.Children.Add(Note("Changes apply to the next bridge call immediately."));
        root.Children.Add(Toggle(
            "Bypass Revit dialogs during bridge calls",
            "Warnings are committed and logged, blocking errors roll the call back, and popups are answered automatically so a dialog can never freeze the bridge queue. Turn off to see stock Revit dialogs (a popup then blocks the queue until dismissed).",
            settings.BypassDialogs, LocalSettingsStore.SetBypassDialogs));
        root.Children.Add(Toggle(
            "Allow arbitrary code (run_python / run_csharp)",
            "Lets AI agents execute newly written scripts. When off, only enabled saved tools on disk can run — agents keep run_saved_tool and every read-only bridge tool.",
            settings.AllowArbitraryCode, LocalSettingsStore.SetAllowArbitraryCode));

        root.Children.Add(SectionTitle("Saved tools folder"));
        root.Children.Add(Note("Primary, writable location for saved tools. Agents read it via list_saved_tools before creating files."));
        var rootRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var saveRoot = ActionButton("Save");
        saveRoot.Margin = new Thickness(8, 0, 0, 0);
        saveRoot.Click += (_, _) => Guard(() =>
        {
            LocalSettingsStore.SetSavedToolsRoot(_rootBox.Text);
            _rootBox.Text = LocalSettingsStore.Load().SavedToolsRoot;
            Status("Folder saved.");
        });
        DockPanel.SetDock(saveRoot, Dock.Right);
        rootRow.Children.Add(saveRoot);
        StyleTextBox(_rootBox);
        _rootBox.Text = settings.SavedToolsRoot;
        rootRow.Children.Add(_rootBox);
        root.Children.Add(rootRow);

        root.Children.Add(SectionTitle("Extra tool paths"));
        root.Children.Add(Note("Read-only roots searched after the primary folder, in order. On duplicate tool IDs the first root wins; subfolders are groups."));
        _paths.Height = 84;
        _paths.BorderThickness = new Thickness(1);
        _paths.BorderBrush = Brush(_palette.Border);
        _paths.Background = Brush(_palette.Surface);
        _paths.Foreground = Brush(_palette.Text);
        _paths.Margin = new Thickness(0, 2, 0, 0);
        foreach (var path in settings.SavedToolsPaths) _paths.Items.Add(path);
        root.Children.Add(_paths);

        var reorder = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var up = ActionButton("Move up");
        up.Click += (_, _) => Move(-1);
        reorder.Children.Add(up);
        var down = ActionButton("Move down");
        down.Margin = new Thickness(6, 0, 0, 0);
        down.Click += (_, _) => Move(1);
        reorder.Children.Add(down);
        var remove = ActionButton("Remove");
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.Click += (_, _) =>
        {
            if (_paths.SelectedIndex < 0) return;
            _paths.Items.RemoveAt(_paths.SelectedIndex);
            SavePaths();
        };
        reorder.Children.Add(remove);
        root.Children.Add(reorder);

        var addRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var add = ActionButton("Add path");
        add.Margin = new Thickness(8, 0, 0, 0);
        add.Click += (_, _) => Guard(() =>
        {
            var candidate = _newPathBox.Text.Trim();
            if (candidate.Length == 0) return;
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (!Path.IsPathFullyQualified(expanded)) throw new InvalidDataException("Enter an absolute folder path.");
            _paths.Items.Add(Path.GetFullPath(expanded));
            _newPathBox.Text = "";
            SavePaths();
        });
        DockPanel.SetDock(add, Dock.Right);
        addRow.Children.Add(add);
        StyleTextBox(_newPathBox);
        addRow.Children.Add(_newPathBox);
        root.Children.Add(addRow);

        _status.FontSize = 10;
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Foreground = Brush(_palette.MutedText);
        if (settings.Error is not null)
        {
            _status.Text = "settings.json problem: " + settings.Error;
            _status.Foreground = Brush(_palette.Error);
        }
        root.Children.Add(_status);

        var close = ActionButton("Close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.MinWidth = 70;
        close.Margin = new Thickness(0, 12, 0, 0);
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = root;
    }

    private void Move(int delta)
    {
        var index = _paths.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _paths.Items.Count) return;
        var item = _paths.Items[index];
        _paths.Items.RemoveAt(index);
        _paths.Items.Insert(target, item);
        _paths.SelectedIndex = target;
        SavePaths();
    }

    private void SavePaths() => Guard(() =>
    {
        LocalSettingsStore.SetSavedToolsPaths(_paths.Items.Cast<string>().ToList());
        Status("Paths saved.");
    });

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { Status(ex.Message, error: true); }
    }

    private void Status(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = Brush(error ? _palette.Error : _palette.MutedText);
    }

    private StackPanel Toggle(string title, string explanation, bool initial, Action<bool> save)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        var box = new CheckBox
        {
            Content = title,
            IsChecked = initial,
            Foreground = Brush(_palette.Text),
            FontWeight = FontWeights.SemiBold
        };
        box.Checked += (_, _) => Guard(() => { save(true); Status(title + ": on."); });
        box.Unchecked += (_, _) => Guard(() => { save(false); Status(title + ": off."); });
        panel.Children.Add(box);
        var note = Note(explanation);
        note.Margin = new Thickness(18, 2, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private TextBlock SectionTitle(string text, bool first = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(_palette.Text),
            Margin = new Thickness(0, first ? 0 : 14, 0, 2)
        };
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 10,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(_palette.SecondaryText),
        Margin = new Thickness(0, 0, 0, 4)
    };

    private void StyleTextBox(TextBox box)
    {
        box.Padding = new Thickness(5, 3, 5, 3);
        box.BorderThickness = new Thickness(1);
        box.Background = Brush(_palette.Surface);
        box.Foreground = Brush(_palette.Text);
        box.BorderBrush = Brush(_palette.Border);
        box.CaretBrush = Brush(_palette.Text);
    }

    private Button ActionButton(string label) => new()
    {
        Content = label,
        MinWidth = 64,
        Height = 22,
        Padding = new Thickness(8, 0, 8, 0),
        FontSize = 10,
        Background = Brush(_palette.Surface),
        Foreground = Brush(_palette.SecondaryText),
        BorderBrush = Brush(_palette.Border),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
