using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System.Reflection;
using System.Windows.Media;

namespace RevitMcp.Bridge;

public sealed class App : IExternalApplication
{
    private ActivityPane? _activityPane;
    private DateTimeOffset _nextActivityRefreshUtc;
    private string? _lastActiveTitle;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var runtime = BridgeRuntime.Create(application.ControlledApplication.VersionNumber);
            var handler = new RevitRequestHandler(runtime);
            runtime.AttachExternalEvent(ExternalEvent.Create(handler));
            _activityPane = new ActivityPane(runtime);
            application.RegisterDockablePane(ActivityPane.PaneId, "Revit MCP Activity", _activityPane);
            application.Idling += OnIdling;
            application.ThemeChanged += OnThemeChanged;
            CreateRibbon(application, runtime);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Revit MCP", $"Bridge startup failed: {ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.Idling -= OnIdling;
        application.ThemeChanged -= OnThemeChanged;
        BridgeRuntime.Current?.Dispose();
        return Result.Succeeded;
    }

    private void OnIdling(object? sender, IdlingEventArgs args)
    {
        if (_activityPane is null || DateTimeOffset.UtcNow < _nextActivityRefreshUtc) return;
        _nextActivityRefreshUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
        var uiapp = sender as UIApplication;
        string? activeTitle = null;
        var open = new List<string>();
        // A closed document can linger behind ActiveUIDocument or in the Documents
        // set; touching members of a dead handle throws InvalidObjectException.
        try
        {
            var activeDocument = uiapp?.ActiveUIDocument?.Document;
            if (activeDocument is not null && activeDocument.IsValidObject) activeTitle = activeDocument.Title;
            if (uiapp is not null)
                foreach (Autodesk.Revit.DB.Document document in uiapp.Application.Documents)
                    if (document.IsValidObject)
                        open.Add(document.Title + (document.IsFamilyDocument ? " (family)" : ""));
        }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException) { }
        if (activeTitle != _lastActiveTitle)
        {
            BridgeRuntime.Current?.Log.Add(new(DateTimeOffset.UtcNow, null, null, "active_document", null, null, null, null, activeTitle ?? "none", null));
            _lastActiveTitle = activeTitle;
        }
        _activityPane.Refresh(activeTitle, open);
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs args) =>
        _activityPane?.ApplyTheme();

    private static void CreateRibbon(UIControlledApplication application, BridgeRuntime runtime)
    {
        const string tab = "Revit MCP";
        try { application.CreateRibbonTab(tab); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        var panel = application.CreateRibbonPanel(tab, "Local Bridge");
        var assembly = Assembly.GetExecutingAssembly().Location;
        var onData = new PushButtonData("RevitMcp.Bridge.On", "Bridge ON", assembly, typeof(BridgeOnCommand).FullName!);
        var offData = new PushButtonData("RevitMcp.Bridge.Off", "Bridge OFF", assembly, typeof(BridgeOffCommand).FullName!);
        var activityData = new PushButtonData("RevitMcp.Bridge.Activity", "Activity", assembly, typeof(ActivityPaneCommand).FullName!);
        var settingsData = new PushButtonData("RevitMcp.Bridge.Settings", "Settings", assembly, typeof(SettingsCommand).FullName!);
        var on = (PushButton)panel.AddItem(onData);
        var off = (PushButton)panel.AddItem(offData);
        panel.AddSeparator();
        var activity = (PushButton)panel.AddItem(activityData);
        var settings = (PushButton)panel.AddItem(settingsData);
        var green = Color.FromRgb(67, 190, 105);
        var red = Color.FromRgb(220, 101, 101);
        var blue = Color.FromRgb(74, 157, 216);
        var gray = Color.FromRgb(150, 158, 170);
        on.LargeImage = LucideIcon.Create(LucideIcon.PlugZap, green, 32, 1.25);
        on.Image = LucideIcon.Create(LucideIcon.PlugZap, green, 16, 1.75);
        off.LargeImage = LucideIcon.Create(LucideIcon.Unplug, red, 32, 1.25);
        off.Image = LucideIcon.Create(LucideIcon.Unplug, red, 16, 1.75);
        activity.LargeImage = LucideIcon.Create(LucideIcon.Activity, blue, 32, 1.25);
        activity.Image = LucideIcon.Create(LucideIcon.Activity, blue, 16, 1.75);
        activity.ToolTip = "Show or hide local Revit MCP bridge activity.";
        settings.LargeImage = LucideIcon.Create(LucideIcon.Settings, gray, 32, 1.25);
        settings.Image = LucideIcon.Create(LucideIcon.Settings, gray, 16, 1.75);
        settings.ToolTip = "Execution policy (dialog bypass, arbitrary code) and saved-tool locations.";
        runtime.AttachButtons(on, off);
        runtime.RefreshRibbon();
    }
}

// Lucide icons (ISC license, lucide.dev) rendered as vector drawings: 24-unit
// path data stroked with an absolute pixel width, so icons stay crisp and the
// stroke weight is constant at any ribbon scale.
internal static class LucideIcon
{
    public static readonly string[] PlugZap =
    [
        "M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z",
        "M2 22 5 19",
        "M7.5 13.5 10 11",
        "M10.5 16.5 13 14",
        "M18 3 14 7h6l-4 4",
    ];

    public static readonly string[] Unplug =
    [
        "M19 5 22 2",
        "M2 22 5 19",
        "M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z",
        "M7.5 13.5 10 11",
        "M10.5 16.5 13 14",
        "M12 6l6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0Z",
    ];

    public static readonly string[] Activity =
    [
        "M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2",
    ];

    public static readonly string[] Settings =
    [
        "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z",
        "M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z",
    ];

    public static ImageSource Create(string[] pathData, Color color, double size, double strokePx)
    {
        var pen = new Pen(new SolidColorBrush(color), strokePx * 24.0 / size)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new System.Windows.Rect(0, 0, 24, 24))));
        foreach (var data in pathData)
            group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(data)));
        group.Transform = new ScaleTransform(size / 24.0, size / 24.0);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
