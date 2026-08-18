# color_splash: color elements of one category in the active view by the value
# of a parameter - each distinct value gets its own solid-fill override color.
# reset=true clears the overrides instead.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire), rewritten for IronPython 2.7.
# Bridge runs this in one auto transaction - do not open transactions here.

from Autodesk.Revit.DB import (Color, ElementId, FillPatternElement,
                               FilteredElementCollector, OverrideGraphicSettings,
                               StorageType)

# 20 visually distinct base colors; values beyond that get golden-angle hues.
PALETTE = [
    (230, 25, 75), (60, 180, 75), (255, 225, 25), (0, 130, 200),
    (245, 130, 48), (145, 30, 180), (70, 240, 240), (240, 50, 230),
    (210, 245, 60), (250, 190, 212), (0, 128, 128), (220, 190, 255),
    (170, 110, 40), (255, 250, 200), (128, 0, 0), (170, 255, 195),
    (128, 128, 0), (255, 215, 180), (0, 0, 128), (128, 128, 128),
]


def extra_color(index):
    hue = (index * 0.61803398875) % 1.0
    sector = int(hue * 6.0) % 6
    fraction = hue * 6.0 - int(hue * 6.0)
    value, saturation = 0.85, 0.65
    p = value * (1.0 - saturation)
    q = value * (1.0 - fraction * saturation)
    t = value * (1.0 - (1.0 - fraction) * saturation)
    rgb = [(value, t, p), (q, value, p), (p, value, t),
           (p, q, value), (t, value, q), (value, p, q)][sector]
    return (int(rgb[0] * 255), int(rgb[1] * 255), int(rgb[2] * 255))


def find_category(name):
    wanted = name.strip().lower()
    for category in doc.Settings.Categories:
        if category.Name.lower() == wanted:
            return category
    return None


def find_solid_fill_id():
    for pattern_element in FilteredElementCollector(doc).OfClass(FillPatternElement):
        if pattern_element.GetFillPattern().IsSolidFill:
            return pattern_element.Id
    return None


def parameter_value_string(parameter):
    if parameter is None or not parameter.HasValue:
        return "None"
    storage = parameter.StorageType
    if storage == StorageType.String:
        return parameter.AsString() or "None"
    if storage == StorageType.Double:
        return parameter.AsValueString() or str(parameter.AsDouble())
    if storage == StorageType.Integer:
        return parameter.AsValueString() or str(parameter.AsInteger())
    if storage == StorageType.ElementId:
        target = parameter.AsElementId()
        if target == ElementId.InvalidElementId:
            return "None"
        element = doc.GetElement(target)
        return element.Name if element is not None else str(int(target.Value))
    return "None"


def lookup_parameter(element, name):
    parameter = element.LookupParameter(name)
    if parameter is not None and parameter.HasValue:
        return parameter
    type_id = element.GetTypeId()
    if type_id != ElementId.InvalidElementId:
        element_type = doc.GetElement(type_id)
        if element_type is not None:
            type_parameter = element_type.LookupParameter(name)
            if type_parameter is not None:
                return type_parameter
    return parameter


def run():
    view = doc.ActiveView
    category_name = request.get("category", "")
    parameter_name = (request.get("parameter_name") or "").strip()
    reset = bool(request.get("reset", False))

    if not view.AreGraphicsOverridesAllowed():
        return {"success": False,
                "message": "The active view (%s) does not allow graphic overrides." % view.ViewType}

    category = find_category(category_name)
    if category is None:
        return {"success": False, "message": "Category '%s' not found." % category_name}

    elements = FilteredElementCollector(doc, view.Id) \
        .OfCategoryId(category.Id).WhereElementIsNotElementType() \
        .WhereElementIsViewIndependent().ToElements()
    if elements.Count == 0:
        return {"success": False,
                "message": "No '%s' elements are visible in the active view." % category.Name}

    if reset:
        cleared = OverrideGraphicSettings()  # empty settings remove the override
        for element in elements:
            view.SetElementOverrides(element.Id, cleared)
        return {"success": True, "reset": True, "view": view.Name,
                "category": category.Name, "cleared_elements": elements.Count}

    if not parameter_name:
        return {"success": False,
                "message": "parameter_name is required unless reset is true."}

    groups = {}
    for element in elements:
        value = parameter_value_string(lookup_parameter(element, parameter_name))
        groups.setdefault(value, []).append(element.Id)

    solid_fill_id = find_solid_fill_id()
    results = []
    for index, value in enumerate(sorted(groups.keys())):
        if index < len(PALETTE):
            rgb = PALETTE[index]
        else:
            rgb = extra_color(index - len(PALETTE))
        color = Color(rgb[0], rgb[1], rgb[2])
        overrides = OverrideGraphicSettings()
        overrides.SetProjectionLineColor(color)
        overrides.SetSurfaceForegroundPatternColor(color)
        overrides.SetCutForegroundPatternColor(color)
        if solid_fill_id is not None:
            overrides.SetSurfaceForegroundPatternId(solid_fill_id)
            overrides.SetCutForegroundPatternId(solid_fill_id)
        for element_id in groups[value]:
            view.SetElementOverrides(element_id, overrides)
        results.append({"value": value, "count": len(groups[value]),
                        "color": {"r": rgb[0], "g": rgb[1], "b": rgb[2]}})

    return {
        "success": True,
        "view": view.Name,
        "category": category.Name,
        "parameter": parameter_name,
        "total_elements": elements.Count,
        "colored_groups": len(results),
        "groups": results,
    }


_result = run()
