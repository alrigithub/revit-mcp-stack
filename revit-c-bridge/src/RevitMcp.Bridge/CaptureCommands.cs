using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;

namespace RevitMcp.Bridge;

[Transaction(TransactionMode.Manual)]
public sealed class BuildingCaptureCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => CaptureDialog.Run(data.Application, "building", ref message);
}
[Transaction(TransactionMode.Manual)]
public sealed class FloorCaptureCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => CaptureDialog.Run(data.Application, "floors", ref message);
}
[Transaction(TransactionMode.Manual)]
public sealed class ElementCaptureCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => CaptureDialog.Run(data.Application, "element", ref message);
}
[Transaction(TransactionMode.Manual)]
public sealed class SectionCaptureCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => CaptureDialog.Run(data.Application, "horizontal", ref message);
}
public sealed class CaptureAvailable : IExternalCommandAvailability
{
    // Availability runs in a read-only API context; IsReadOnly here would disable every button.
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) => app.ActiveUIDocument is not null;
}

internal sealed class CaptureDialog : Window
{
    private readonly ComboBox _preset = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly CheckBox _selection = new() { Content = "Frame the current selection", Margin = new Thickness(0, 0, 0, 12) };
    private readonly CheckBox _isolated = new() { Content = "Include an isolated element view", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Slider _cut = new() { Minimum = 1, Maximum = 99, Value = 50, TickFrequency = 25, IsSnapToTickEnabled = false, Margin = new Thickness(0, 6, 0, 12) };
    private readonly ComboBox _axis = new() { ItemsSource = new[] { "long", "short" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 12) };
    private readonly ListBox _levels = new() { SelectionMode = SelectionMode.Multiple, MaxHeight = 150, Margin = new Thickness(0, 4, 0, 12) };
    private static readonly (string Key, string Label)[] Presets =
    [
        ("building", "Building overview — four elevations and four axonometrics"),
        ("elevations", "Four elevations"), ("axo", "Four axonometrics"), ("floors", "Floor axonometrics"),
        ("element", "Element with context, isolated, and middle cuts"),
        ("horizontal", "Horizontal section"), ("vertical", "Vertical section")
    ];
    private CaptureDialog(UIApplication app, string preset)
    {
        Title = "3XN inspection views"; Width = 510; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        new WindowInteropHelper(this).Owner = app.MainWindowHandle;
        var stack = new StackPanel { Margin = new Thickness(24) }; Content = stack;
        stack.Children.Add(new TextBlock { Text = "Capture inspection views", FontSize = 22, Margin = new Thickness(0, 0, 0, 14) });
        _preset.ItemsSource = Presets.Select(p => p.Label).ToArray(); _preset.SelectedIndex = Array.FindIndex(Presets, p => p.Key == preset);
        stack.Children.Add(_preset);
        var hasSelection = app.ActiveUIDocument.Selection.GetElementIds().Count > 0;
        _selection.IsEnabled = hasSelection; _selection.IsChecked = preset == "element" && hasSelection;
        stack.Children.Add(_selection); stack.Children.Add(_isolated);
        var cuts = new StackPanel(); stack.Children.Add(cuts);
        cuts.Children.Add(new TextBlock { Text = "Section position (1–99%, default middle)", FontSize = 13 }); cuts.Children.Add(_cut);
        var sides = new StackPanel(); stack.Children.Add(sides);
        sides.Children.Add(new TextBlock { Text = "Vertical section side" }); sides.Children.Add(_axis);
        var floors = new StackPanel(); stack.Children.Add(floors);
        floors.Children.Add(new TextBlock { Text = "Select floors, or leave empty for all levels hosting floors.", TextWrapping = TextWrapping.Wrap });
        _levels.ItemsSource = new FilteredElementCollector(app.ActiveUIDocument.Document).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => new LevelChoice(l.Id.Value, l.Name)).ToArray();
        _levels.DisplayMemberPath = nameof(LevelChoice.Name); floors.Children.Add(_levels);
        void UpdateOptions()
        {
            var key = Presets[_preset.SelectedIndex].Key;
            _isolated.Visibility = key == "element" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            cuts.Visibility = key is "element" or "horizontal" or "vertical" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            sides.Visibility = key == "vertical" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            floors.Visibility = key == "floors" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (key == "element") _selection.IsChecked = hasSelection;
        }
        _preset.SelectionChanged += (_, _) => UpdateOptions(); UpdateOptions();
        stack.Children.Add(new TextBlock { Text = "Creates reusable helper views and labelled PNG sheets. Your working view stays open.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16) });
        var button = new Button { Content = "Create PNGs", Padding = new Thickness(20, 8, 20, 8), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => DialogResult = true; stack.Children.Add(button);
    }
    private sealed record LevelChoice(long Id, string Name);
    public static Result Run(UIApplication app, string preset, ref string message)
    {
        try
        {
            if (app.ActiveUIDocument is null) { TaskDialog.Show("3XN inspection views", "Open a model first."); return Result.Cancelled; }
            var dialog = new CaptureDialog(app, preset);
            if (dialog.ShowDialog() != true) return Result.Cancelled;
            var key = Presets[dialog._preset.SelectedIndex].Key;
            var args = JsonSerializer.SerializeToElement(new
            {
                preset = key,
                element_ids = dialog._selection.IsChecked == true || key == "element" ? app.ActiveUIDocument.Selection.GetElementIds().Select(id => id.Value).ToArray() : [],
                level_ids = dialog._levels.SelectedItems.Cast<LevelChoice>().Select(l => l.Id).ToArray(),
                cut_fraction = dialog._cut.Value / 100, axis = (string)dialog._axis.SelectedItem,
                include_isolated = dialog._isolated.IsChecked == true
            });
            var result = JsonSerializer.SerializeToElement(CaptureService.Capture(app.ActiveUIDocument.Document, args));
            var directory = result.GetProperty("output_directory").GetString()!;
            var done = new TaskDialog("3XN inspection views") { MainInstruction = "Inspection PNGs are ready", MainContent = directory, CommonButtons = TaskDialogCommonButtons.Close };
            done.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the PNG folder");
            if (done.Show() == TaskDialogResult.CommandLink1) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return Result.Succeeded;
        }
        catch (Exception ex) { message = ex.Message; return Result.Failed; }
    }
}
