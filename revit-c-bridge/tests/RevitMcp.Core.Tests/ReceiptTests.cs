using RevitMcp.Contracts;
using RevitMcp.Core;
using RevitMcp.Bridge;
using Autodesk.Revit.DB;
using System.Text.Json;

internal static class ReceiptTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Receipt assertion failed."); }
    public static Task CancellationRollsBackAtCheckpoint()
    {
        var doc = new Document();
        var cancel = false;
        string? outcome = null;
        var coordinator = new TransactionCoordinator { ObserveOutcome = value => outcome = value };
        using var control = ScriptControl.Bind((_, _) => cancel = true, () => cancel);
        try
        {
            coordinator.Execute(doc, "auto", "checkpoint", () =>
            {
                doc.Value = 10;
                ScriptControl.ReportProgress("placed first item", 10);
                ScriptControl.ThrowIfCancellationRequested();
                return new { ok = true };
            });
            throw new Exception("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { Check(doc.Value == 0 && outcome == "rolled_back" && !doc.IsModifiable); }
        return Task.CompletedTask;
    }
    public static Task TimingRevisionAndTerminalReceipt()
    {
        var record = new RequestRecord(new("receipt", null, "run_python", "doc", 1, DateTimeOffset.UtcNow.AddMinutes(1), null, "auto", JsonSerializer.SerializeToElement(new { }), 10));
        var initial = record.Revision;
        var queue = new BoundedRequestQueue(2);
        Check(queue.Position(record) is null && queue.TryEnqueue(record) && queue.Position(record) == 1);
        queue.TryDequeue(out _); Check(queue.Position(record) is null);
        record.Transition(RequestState.Running);
        record.Progress("halfway", 50); record.RequestCancellation();
        record.SetReceipt(JsonSerializer.SerializeToElement(new { model_outcome = "rolled_back" }));
        record.Transition(RequestState.Failed, errorCode: "cooperative_cancelled");
        var receipt = record.Receipt();
        Check(record.Revision > initial && receipt.GetProperty("cancellation_requested").GetBoolean());
        Check(receipt.GetProperty("started_utc").ValueKind == JsonValueKind.String && receipt.GetProperty("completed_utc").ValueKind == JsonValueKind.String);
        Check(receipt.GetProperty("details").GetProperty("model_outcome").GetString() == "rolled_back");
        var terminal = record.Revision;
        record.Progress("late callback", 100); record.RequestCancellation();
        Check(record.Revision == terminal);
        return Task.CompletedTask;
    }
    public static Task CancellationScopeDoesNotLeak()
    {
        using (ScriptControl.Bind((_, _) => { }, () => true))
        {
            try { ScriptControl.ThrowIfCancellationRequested(); throw new Exception("No cancellation."); }
            catch (OperationCanceledException) { }
        }
        ScriptControl.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
