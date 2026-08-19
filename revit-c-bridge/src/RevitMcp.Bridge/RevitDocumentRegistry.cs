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
    // Those same overrides throw InvalidObjectException once the native document
    // is gone (closed doc, EditFamily leftover), so dead keys are purged by
    // rebuilding the map — never by hashing them — and every entry point drops
    // invalid documents before touching any other member.
    private Dictionary<Document, Identity> _identities = new();
    private readonly DocumentGenerationRegistry _generations = new();
    private readonly object _gate = new();

    public object[] List(UIApplication uiapp)
    {
        var active = ActiveDocument(uiapp);
        return uiapp.Application.Documents.Cast<Document>().Where(IsAlive).Select(doc => Describe(doc, SameDocument(active, doc))).ToArray();
    }

    public (Document Document, UIDocument? UiDocument) Resolve(UIApplication uiapp, string? session, long? generation, bool requireActive)
    {
        if (string.IsNullOrWhiteSpace(session) || generation is null)
            throw new RequestDispatchException("document_binding_required", "An explicit document_session and document_generation are required.");
        foreach (Document doc in uiapp.Application.Documents)
        {
            if (!IsAlive(doc)) continue;
            var identity = Get(doc);
            if (identity.Session != session) continue;
            if (identity.Generation != generation) throw new RequestDispatchException("document_generation_mismatch", "The bound document was replaced or regenerated; list documents again.");
            var uidoc = SameDocument(ActiveDocument(uiapp), doc) ? uiapp.ActiveUIDocument : null;
            if (requireActive && uidoc is null) throw new RequestDispatchException("document_not_active", "Activate the bound document for this UI-only operation.");
            return (doc, uidoc);
        }
        throw new RequestDispatchException("document_closed", "The bound document is no longer open; list documents again.");
    }

    public object Describe(Document doc, bool active)
    {
        if (!IsAlive(doc)) throw new RequestDispatchException("document_closed", "The document handle is no longer valid; list documents again.");
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
            Purge();
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

    private void Purge()
    {
        if (_identities.Keys.All(IsAlive)) return;
        var alive = new Dictionary<Document, Identity>();
        foreach (var pair in _identities)
        {
            if (IsAlive(pair.Key)) alive.Add(pair.Key, pair.Value);
            else _generations.Close(pair.Value.Session);
        }
        _identities = alive;
    }

    private static bool IsAlive(Document? document)
    {
        if (document is null) return false;
        try { return document.IsValidObject; }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException) { return false; }
    }

    private static Document? ActiveDocument(UIApplication uiapp)
    {
        try
        {
            var doc = uiapp.ActiveUIDocument?.Document;
            return IsAlive(doc) ? doc : null;
        }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException) { return null; }
    }

    private static string Fingerprint(Document document) => string.Join("|",
        document.PathName ?? string.Empty,
        document.Title,
        document.IsFamilyDocument,
        document.IsWorkshared);

    private static bool SameDocument(Document? left, Document? right)
    {
        if (left is null || right is null) return false;
        try { return left.Equals(right); }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException) { return false; }
    }
}

public sealed class RequestDispatchException(string code, string message, string? remediation = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Remediation { get; } = remediation;
}
