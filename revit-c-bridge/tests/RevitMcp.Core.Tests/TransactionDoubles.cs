// These doubles test bridge orchestration, not Revit's native transaction implementation.
namespace Autodesk.Revit.DB
{
    public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack }
    public sealed class Document
    {
        public bool IsModifiable { get; set; }
        public int Value { get; set; }
        public int Commits { get; set; }
        public bool RejectCommit { get; set; }
    }
    public sealed class Transaction(Document doc, string name) : IDisposable
    {
        private TransactionStatus _status;
        private int _before;
        public TransactionStatus Start() { _ = name; _before = doc.Value; doc.IsModifiable = true; return _status = TransactionStatus.Started; }
        public TransactionStatus Commit() { if (doc.RejectCommit) return RollBack(); doc.Commits++; doc.IsModifiable = false; return _status = TransactionStatus.Committed; }
        public TransactionStatus RollBack() { doc.Value = _before; doc.IsModifiable = false; return _status = TransactionStatus.RolledBack; }
        public TransactionStatus GetStatus() => _status;
        public void Dispose() { if (_status == TransactionStatus.Started) RollBack(); }
    }
    public sealed class TransactionGroup(Document doc, string name) : IDisposable
    {
        private TransactionStatus _status;
        private int _before;
        public TransactionStatus Start() { _ = name; _before = doc.Value; return _status = TransactionStatus.Started; }
        public TransactionStatus Assimilate() => _status = TransactionStatus.Committed;
        public TransactionStatus RollBack() { doc.Value = _before; return _status = TransactionStatus.RolledBack; }
        public TransactionStatus GetStatus() => _status;
        public void Dispose() { if (_status == TransactionStatus.Started) RollBack(); }
    }
}
namespace Autodesk.Revit.Exceptions
{
    public class ModificationOutsideTransactionException : Exception { }
    public class InvalidOperationException(string message) : Exception(message) { }
}
namespace RevitMcp.Bridge
{
    public sealed class RequestDispatchException(string code, string message, string? remediation = null) : Exception(message)
    {
        public string Code { get; } = code;
        public string? Remediation { get; } = remediation;
    }
    public static class VerificationService
    {
        public static readonly string[] DeferredFields = ["deep_geometry", "joins", "materials", "extensible_storage"];
    }
}
