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
        return GuardModification(action, "read");
    }
    private static object ExecuteManual(Document document, Func<object> action)
    {
        var initiallyModifiable = document.IsModifiable;
        var result = GuardModification(action, "manual");
        if (document.IsModifiable != initiallyModifiable)
            throw new RequestDispatchException("manual_transaction_left_open", "Manual code changed document modifiability across the call.", "Commit or roll back every code-owned transaction before returning.");
        return result;
    }
    private static object GuardModification(Func<object> action, string mode)
    {
        // Live Revit 2025 throws ModificationOutsideTransactionException here; the
        // message-matched InvalidOperationException covers older phrasings. Mapping
        // matters doubly because Revit's own message contains "model", which the
        // redaction filter would otherwise reduce to "[redacted]".
        try { return action(); }
        catch (Autodesk.Revit.Exceptions.ModificationOutsideTransactionException)
        {
            throw Forbidden(mode);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) when (ex.Message.Contains("Modification of the document is forbidden", StringComparison.OrdinalIgnoreCase))
        {
            throw Forbidden(mode);
        }
    }
    private static RequestDispatchException Forbidden(string mode) => new("modification_without_transaction",
        $"The code modified the document while transaction_mode '{mode}' had no open transaction.",
        mode == "read"
            ? "Re-run with transaction_mode 'auto' (one bridge-owned transaction) or 'group'."
            : "Start and commit a Transaction inside the code, or re-run with transaction_mode 'auto'.");
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
