using Autodesk.Revit.DB;
using System.Text.Json;

namespace RevitMcp.Bridge;

public sealed class TransactionCoordinator
{
    public object Execute(Document document, string mode, string name, Func<object> action)
    {
        return mode switch
        {
            "read" => ExecuteRead(document, action),
            "auto" => ExecuteAuto(document, name, action),
            "manual" => ExecuteManual(document, action),
            "group" => ExecuteGroup(document, name, action),
            _ => throw new RequestDispatchException("invalid_transaction_mode", "Use read, auto, manual, or group.")
        };
    }

    public object ExecuteAtomicBatch(Document document, string name, IReadOnlyList<Func<object>> steps)
    {
        using var group = new TransactionGroup(document, name);
        if (group.Start() != TransactionStatus.Started) throw new RequestDispatchException("transaction_group_start_failed", "Revit rejected the transaction group.");
        var results = new List<object>();
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                using var transaction = new Transaction(document, $"{name} {i + 1}");
                if (transaction.Start() != TransactionStatus.Started) throw new RequestDispatchException("transaction_start_failed", "Revit rejected a batch step transaction.");
                var result = steps[i]();
                _ = JsonSerializer.SerializeToUtf8Bytes(result); // materialize before commit
                if (transaction.Commit() != TransactionStatus.Committed) throw new RequestDispatchException("transaction_commit_failed", "A batch step did not commit.");
                results.Add(result);
            }
            if (group.Assimilate() != TransactionStatus.Committed) throw new RequestDispatchException("transaction_group_assimilate_failed", "The group did not create one undo item.");
            return new { atomic = true, undo_items_expected = 1, steps = results };
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    private static object ExecuteRead(Document document, Func<object> action)
    {
        if (document.IsModifiable) throw new RequestDispatchException("document_already_modifiable", "A read request will not run while the document is modifiable.", "Retry after the owning Revit context closes its transaction.");
        return action();
    }
    private static object ExecuteManual(Document document, Func<object> action)
    {
        var initiallyModifiable = document.IsModifiable;
        var result = action();
        if (document.IsModifiable != initiallyModifiable)
            throw new RequestDispatchException("manual_transaction_left_open", "Manual code changed document modifiability across the call.", "Commit or roll back every code-owned transaction before returning.");
        return result;
    }
    private static object ExecuteAuto(Document document, string name, Func<object> action)
    {
        using var transaction = new Transaction(document, name);
        if (transaction.Start() != TransactionStatus.Started) throw new RequestDispatchException("transaction_start_failed", "Revit rejected the bridge-owned transaction.");
        try
        {
            var result = action();
            _ = JsonSerializer.SerializeToUtf8Bytes(result);
            if (transaction.Commit() != TransactionStatus.Committed) throw new RequestDispatchException("transaction_commit_failed", "The bridge-owned transaction did not commit.");
            return result;
        }
        catch { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); throw; }
    }
    private static object ExecuteGroup(Document document, string name, Func<object> action)
    {
        using var group = new TransactionGroup(document, name);
        if (group.Start() != TransactionStatus.Started) throw new RequestDispatchException("transaction_group_start_failed", "Revit rejected the bridge-owned group.");
        try
        {
            var result = ExecuteAuto(document, name + " step", action);
            if (group.Assimilate() != TransactionStatus.Committed) throw new RequestDispatchException("transaction_group_assimilate_failed", "The group did not assimilate.");
            return result;
        }
        catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
    }
}
