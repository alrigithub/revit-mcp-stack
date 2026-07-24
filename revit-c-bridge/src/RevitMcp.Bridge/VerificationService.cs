using Autodesk.Revit.DB;

namespace RevitMcp.Bridge;

public static class VerificationService
{
    public static readonly string[] DeferredFields = ["deep_geometry", "joins", "materials", "extensible_storage"];

    public static object Element(Document document, Element element, int relationshipLimit = 50)
    {
        var box = element.get_BoundingBox(null);
        var viewBox = document.ActiveView is null ? null : element.get_BoundingBox(document.ActiveView);
        var type = document.GetElement(element.GetTypeId());
        return new
        {
            identity = new { element_id = element.Id.Value, unique_id = element.UniqueId, runtime_type = element.GetType().FullName, category = element.Category is null ? null : new { id = element.Category.Id.Value, name = element.Category.Name } },
            instance_parameters = Parameters(element),
            type_parameters = type is null ? Array.Empty<object>() : Parameters(type),
            bounding_boxes = new { model = Box(box), active_view = Box(viewBox) },
            geometry = GeometrySummary(element),
            relationships = Relationships(document, element, relationshipLimit),
            worksharing = Worksharing(document, element),
            phase = ParameterText(element, BuiltInParameter.PHASE_CREATED),
            design_option = element.DesignOption is null ? null : new { id = element.DesignOption.Id.Value, name = element.DesignOption.Name },
            omitted_fields = Array.Empty<string>(),
            deferred_fields = DeferredFields
        };
    }

    public static object[] Parameters(Element element) => element.Parameters.Cast<Parameter>().Take(500).Select(p => (object)new
    {
        id = p.Id.Value,
        name = p.Definition?.Name,
        storage_type = p.StorageType.ToString(),
        units = p.Definition?.GetDataType().TypeId,
        raw = Raw(p),
        display = Safe(() => p.AsValueString()),
        is_read_only = p.IsReadOnly
    }).ToArray();

    public static object[] Warnings(Document document) => document.GetWarnings().Take(1000).Select(w => (object)new
    {
        failure_definition_id = w.GetFailureDefinitionId().Guid.ToString("D"),
        severity = w.GetSeverity().ToString(),
        description = w.GetDescriptionText(),
        involved_element_ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().Take(200).Select(id => id.Value).ToArray()
    }).ToArray();

    private static object Relationships(Document document, Element element, int limit)
    {
        var family = element as FamilyInstance;
        return new
        {
            type_id = Valid(element.GetTypeId()),
            host_id = family?.Host is null ? (long?)null : family.Host.Id.Value,
            level_id = Valid(element.LevelId),
            owner_view_id = Valid(element.OwnerViewId),
            dependent_ids = element.GetDependentElements(null).Take(limit).Select(id => id.Value).ToArray(),
            page = new { offset = 0, limit, has_more = element.GetDependentElements(null).Count > limit }
        };
    }
    private static object Worksharing(Document document, Element element)
    {
        if (!document.IsWorkshared) return new { enabled = false, owner = (string?)null, editability = "not_workshared" };
        var info = WorksharingUtils.GetWorksharingTooltipInfo(document, element.Id);
        var status = WorksharingUtils.GetCheckoutStatus(document, element.Id);
        return new { enabled = true, owner = info.Owner, editability = status.ToString() };
    }
    private static object GeometrySummary(Element element)
    {
        try
        {
            var objects = element.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse, IncludeNonVisibleObjects = false });
            var top = objects?.Cast<GeometryObject>().Take(200).ToArray() ?? [];
            return new { top_level_count = top.Length, runtime_types = top.GroupBy(x => x.GetType().Name).ToDictionary(g => g.Key, g => g.Count()), bounded = true };
        }
        catch (Exception ex) { return new { top_level_count = 0, runtime_types = new Dictionary<string, int>(), bounded = true, error = ex.GetType().Name }; }
    }
    private static object? Box(BoundingBoxXYZ? box) => box is null ? null : new { min = Point(box.Min), max = Point(box.Max), transform_origin = Point(box.Transform.Origin) };
    private static object Point(XYZ point) => new { x = point.X, y = point.Y, z = point.Z };
    private static long? Valid(ElementId id) => id == ElementId.InvalidElementId ? null : id.Value;
    private static object? Raw(Parameter p) => p.StorageType switch { StorageType.Double => p.AsDouble(), StorageType.Integer => p.AsInteger(), StorageType.String => p.AsString(), StorageType.ElementId => p.AsElementId()?.Value, _ => null };
    private static string? ParameterText(Element e, BuiltInParameter id) => Safe(() => e.get_Parameter(id)?.AsValueString());
    private static T? Safe<T>(Func<T?> action) { try { return action(); } catch { return default; } }
}
