namespace RevitMcp.Core;

// Reflection guards for Revit APIs newer than the compile-time reference (2025).
// A null result means "this Revit build does not have the API" — callers keep
// their 2025 behavior and light up on 2025.3+/2026+ without a rebuild.
public static class ReflectionProbes
{
    // Document.GetActiveEditMode() (2025.3+) returns EditModeType; falls back to
    // Document.IsInEditMode() (also 2025.3+, kept separate in case one is trimmed).
    public static string? ActiveEditMode(object? document)
    {
        if (document is null) return null;
        try
        {
            var type = document.GetType();
            var get = type.GetMethod("GetActiveEditMode", Type.EmptyTypes);
            if (get is not null) return get.Invoke(document, null)?.ToString();
            if (type.GetMethod("IsInEditMode", Type.EmptyTypes)?.Invoke(document, null) is bool inEdit)
                return inEdit ? "unknown" : "None";
        }
        catch
        {
            // A probe must never take down a request; absence and failure look the same.
        }
        return null;
    }

    // LabelUtils.GetFailureSeverityName(FailureSeverity) (2026+).
    public static string? FailureSeverityName(Type? labelUtilsType, object? severity)
    {
        if (labelUtilsType is null || severity is null) return null;
        try
        {
            var method = labelUtilsType.GetMethod("GetFailureSeverityName", [severity.GetType()]);
            return method?.Invoke(null, [severity])?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
