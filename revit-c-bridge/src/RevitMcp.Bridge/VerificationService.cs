using Autodesk.Revit.DB;
using RevitMcp.Core;
using System.Text.Json;

namespace RevitMcp.Bridge;

public static class VerificationService
{
    public static readonly string[] DeferredFields = ["deep_geometry", "joins", "extensible_storage"];
    private static readonly string[] AllFields = ["identity", "instance_parameters", "type_parameters", "bounding_boxes", "geometry", "relationships", "worksharing", "phase", "design_option", "materials"];
    public static string[]? Fields(JsonElement args) => args.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
        ? fields.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;

    public static object Element(Document document, Element element, int relationshipLimit = 50, string[]? fields = null)
    {
        fields ??= AllFields.Where(f => f != "materials").ToArray();
        if (fields.Any(f => !AllFields.Contains(f))) throw new RequestDispatchException("invalid_fields", "Use: " + string.Join(", ", AllFields));
        var result = new Dictionary<string, object?>();
        foreach (var field in fields.Distinct()) result[field] = Probe(() => field switch
        {
            "identity" => new { element_id = element.Id.Value, unique_id = element.UniqueId, name = element.Name, is_type = element is ElementType,
                runtime_type = element.GetType().FullName, category = element.Category is null ? null : new { id = element.Category.Id.Value, name = element.Category.Name } },
            "instance_parameters" => Parameters(element),
            "type_parameters" => document.GetElement(element.GetTypeId()) is { } type ? Parameters(type) : Array.Empty<object>(),
            "bounding_boxes" => new { model = Box(element.get_BoundingBox(null)), active_view = document.ActiveView is null ? null : Box(element.get_BoundingBox(document.ActiveView)) },
            "geometry" => GeometrySummary(element),
            "relationships" => Relationships(element, relationshipLimit),
            "worksharing" => Worksharing(document, element),
            "phase" => element.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId()?.Value,
            "design_option" => element.DesignOption is null ? null : new { id = element.DesignOption.Id.Value, name = element.DesignOption.Name },
            "materials" => element.GetMaterialIds(false).Take(100).Select(id => new { id = id.Value, name = document.GetElement(id)?.Name,
                area_internal_square_feet = Probe(() => element.GetMaterialArea(id, false)), volume_internal_cubic_feet = Probe(() => element.GetMaterialVolume(id)) }).ToArray(),
            _ => null
        });
        result["omitted_fields"] = AllFields.Except(fields).ToArray();
        var truncated = new List<string>();
        if (fields.Contains("instance_parameters") && element.Parameters.Size > 500) truncated.Add("instance_parameters");
        if (fields.Contains("type_parameters") && document.GetElement(element.GetTypeId()) is { } et && et.Parameters.Size > 500) truncated.Add("type_parameters");
        if (fields.Contains("materials") && Probe(() => element.GetMaterialIds(false).Count) is int materialCount && materialCount > 100) truncated.Add("materials");
        if (truncated.Count > 0) result["truncated_fields"] = truncated;
        result["deferred_fields"] = DeferredFields;
        return result;
    }

    public static object[] Parameters(Element element, long[]? parameterIds = null) => element.Parameters.Cast<Parameter>()
        .Where(p => parameterIds is null || parameterIds.Contains(p.Id.Value)).Take(500).Select(p => (object)new
        {
            id = p.Id.Value, shared_guid = p.IsShared ? p.GUID.ToString("D") : null, name = p.Definition?.Name,
            storage_type = p.StorageType.ToString(), spec = Probe(() => p.Definition?.GetDataType().TypeId),
            unit_type = Probe(() => p.StorageType == StorageType.Double ? p.GetUnitTypeId().TypeId : null),
            raw = Probe(() => Raw(p)), display = Probe(() => p.AsValueString()), is_read_only = p.IsReadOnly,
            raw_value_convention = "Revit internal units; lengths are feet, angles radians."
        }).ToArray();

    public static object Warning(FailureMessage w) => new
    {
        failure_definition_id = w.GetFailureDefinitionId().Guid.ToString("D"), severity = w.GetSeverity().ToString(),
        description = w.GetDescriptionText(), involved_element_ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().Take(200).Select(id => id.Value).ToArray()
    };
    public static string WarningKey(FailureMessage w) => w.GetFailureDefinitionId().Guid + ":" + string.Join(",", w.GetFailingElements().Concat(w.GetAdditionalElements()).Select(id => id.Value).Distinct().Order());
    public static object[] Warnings(Document document) => document.GetWarnings().Take(1000).Select(Warning).ToArray();

    private static object Relationships(Element element, int limit)
    {
        var dependents = element.GetDependentElements(null);
        var family = element as FamilyInstance;
        return new { type_id = Valid(element.GetTypeId()), host_id = family?.Host?.Id.Value, level_id = Valid(element.LevelId), owner_view_id = Valid(element.OwnerViewId),
            dependent_ids = dependents.Take(limit).Select(id => id.Value).ToArray(), total_dependents = dependents.Count, has_more = dependents.Count > limit };
    }
    private static object Worksharing(Document document, Element element)
    {
        if (!document.IsWorkshared) return new { enabled = false };
        return new { enabled = true, owner = WorksharingUtils.GetWorksharingTooltipInfo(document, element.Id).Owner, editability = WorksharingUtils.GetCheckoutStatus(document, element.Id).ToString() };
    }
    private static object GeometrySummary(Element element)
    {
        var geometry = element.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse, IncludeNonVisibleObjects = false });
        var top = geometry?.Cast<GeometryObject>().Take(201).ToArray() ?? [];
        return new { top_level_count = Math.Min(200, top.Length), runtime_types = top.Take(200).GroupBy(x => x.GetType().Name).ToDictionary(g => g.Key, g => g.Count()), has_more = top.Length > 200 };
    }
    public static object? Box(BoundingBoxXYZ? box) => box is null ? null : new { min = Point(box.Min), max = Point(box.Max), units = "internal_feet",
        transform = new { origin = Point(box.Transform.Origin), basis_x = Point(box.Transform.BasisX), basis_y = Point(box.Transform.BasisY), basis_z = Point(box.Transform.BasisZ) } };
    public static object Point(XYZ point) => new { x = point.X, y = point.Y, z = point.Z };
    private static long? Valid(ElementId id) => id == ElementId.InvalidElementId ? null : id.Value;
    public static object? Raw(Parameter p) => p.StorageType switch { StorageType.Double => p.AsDouble(), StorageType.Integer => p.AsInteger(), StorageType.String => p.AsString(), StorageType.ElementId => p.AsElementId()?.Value, _ => null };
    private static object? Probe(Func<object?> action) { try { return action(); } catch (Exception ex) { return new { error = ex.GetType().Name }; } }
}
