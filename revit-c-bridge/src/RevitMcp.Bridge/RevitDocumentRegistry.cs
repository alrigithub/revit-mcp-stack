using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Bridge;

public sealed class RevitDocumentRegistry
{
    private sealed record Identity(string Session, long Generation, string Fingerprint);
    // Revit can return a different managed Document wrapper for the same native
    // document on each Application.Documents enumeration. Document overrides
    // Equals/GetHashCode for native identity, so use that equality instead of
    // ConditionalWeakTable/reference identity.
    private readonly Dictionary<Document, Identity> _identities = new();
    private readonly DocumentGenerationRegistry _generations = new();
    private readonly object _gate = new();

    public object[] List(UIApplication uiapp)
    {
        var active = uiapp.ActiveUIDocument?.Document;
        return uiapp.Application.Documents.Cast<Document>().Select(doc => Describe(doc, SameDocument(active, doc))).ToArray();
    }

    public (Document Document, UIDocument? UiDocument) Resolve(UIApplication uiapp, string? session, long? generation, bool requireActive)
    {
        if (string.IsNullOrWhiteSpace(session) || generation is null)
            throw new RequestDispatchException("document_binding_required", "An explicit document_session and document_generation are required.");
        foreach (Document doc in uiapp.Application.Documents)
        {
            var identity = Get(doc);
            if (identity.Session != session) continue;
            if (identity.Generation != generation) throw new RequestDispatchException("document_generation_mismatch", "The bound document was replaced or regenerated; list documents again.");
            var uidoc = SameDocument(uiapp.ActiveUIDocument?.Document, doc) ? uiapp.ActiveUIDocument : null;
            if (requireActive && uidoc is null) throw new RequestDispatchException("document_not_active", "Activate the bound document for this UI-only operation.");
            return (doc, uidoc);
        }
        throw new RequestDispatchException("document_closed", "The bound document is no longer open; list documents again.");
    }

    public object Describe(Document doc, bool active)
    {
        var identity = Get(doc);
        return new
        {
            document_session = identity.Session,
            document_generation = identity.Generation,
            title = doc.Title,
            path = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
            is_family_document = doc.IsFamilyDocument,
            is_workshared = doc.IsWorkshared,
            is_active = active,
            is_modifiable = doc.IsModifiable,
            bridge_transaction_state_only = true
        };
    }

    private Identity Get(Document document)
    {
        lock (_gate)
        {
            foreach (var stale in _identities.Keys.Where(item => !item.IsValidObject).ToArray())
            {
                _generations.Close(_identities[stale].Session);
                _identities.Remove(stale);
            }
            var fingerprint = Fingerprint(document);
            if (_identities.TryGetValue(document, out var identity))
            {
                var generation = _generations.Observe(identity.Session, fingerprint);
                if (generation == identity.Generation) return identity;
                identity = identity with { Generation = generation, Fingerprint = fingerprint };
                _identities[document] = identity;
                return identity;
            }
            var session = Guid.NewGuid().ToString("N");
            identity = new(session, _generations.Observe(session, fingerprint), fingerprint);
            _identities.Add(document, identity);
            return identity;
        }
    }

    private static string Fingerprint(Document document) => string.Join("|",
        document.PathName ?? string.Empty,
        document.Title,
        document.IsFamilyDocument,
        document.IsWorkshared);

    private static bool SameDocument(Document? left, Document? right) =>
        left is not null && right is not null && left.Equals(right);
}

public sealed class RequestDispatchException(string code, string message, string? remediation = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Remediation { get; } = remediation;
}
