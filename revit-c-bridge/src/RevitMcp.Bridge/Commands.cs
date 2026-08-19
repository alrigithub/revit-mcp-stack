using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Bridge;

[Transaction(TransactionMode.Manual)]
public sealed class BridgeOnCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try { BridgeRuntime.Require().Enable(); return Result.Succeeded; }
        catch (Exception ex) { message = ex.Message; return Result.Failed; }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class BridgeOffCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try { BridgeRuntime.Require().Disable(); return Result.Succeeded; }
        catch (Exception ex) { message = ex.Message; return Result.Failed; }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            new SettingsDialog(commandData.Application.MainWindowHandle).ShowDialog();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ActivityPaneCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var pane = commandData.Application.GetDockablePane(ActivityPane.PaneId);
            if (pane.IsShown()) pane.Hide(); else pane.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
