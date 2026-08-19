# -*- coding: utf-8 -*-
# Model statistics: element counts by category and level, view/sheet/family
# totals, and basic health numbers (warnings, links, CAD imports). Optional
# per family/type instance breakdown. Pure read.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire),
# rewritten for IronPython 2.7.

from Autodesk.Revit.DB import (
    Element, FilteredElementCollector, FamilyInstance, Family, Level, View,
    ViewSheet, RevitLinkInstance, ImportInstance, UnitUtils, UnitTypeId,
)

MAX_TYPES = 50


def _mm(value):
    return round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters), 1)


def _count_of_class(element_class):
    return FilteredElementCollector(doc).OfClass(element_class).GetElementCount()


def _main():
    include_types = bool(request.get("include_types", False))

    elements = FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
    category_counts = {}
    level_counts = {}
    type_counts = {}

    for element in elements:
        category = element.Category
        if category is not None:
            name = category.Name
            category_counts[name] = category_counts.get(name, 0) + 1
        level_id = element.LevelId
        if level_id is not None:
            level_key = int(level_id.Value)
            if level_key != -1:  # -1 = InvalidElementId (no level)
                level_counts[level_key] = level_counts.get(level_key, 0) + 1
        if include_types and isinstance(element, FamilyInstance):
            symbol = element.Symbol
            if symbol is None:
                continue
            family = symbol.Family
            family_name = family.Name if family is not None else ""
            category_name = category.Name if category is not None else ""
            # symbol.Name raises MissingMemberException in IronPython 2.7
            key = (family_name, Element.Name.GetValue(symbol), category_name)
            type_counts[key] = type_counts.get(key, 0) + 1

    levels = []
    for level in FilteredElementCollector(doc).OfClass(Level):
        levels.append({
            "name": level.Name,
            "elevation_mm": _mm(level.Elevation),
            "element_count": level_counts.get(int(level.Id.Value), 0),
        })
    levels.sort(key=lambda entry: entry["elevation_mm"])

    view_count = 0
    for view in FilteredElementCollector(doc).OfClass(View):
        if not view.IsTemplate and not isinstance(view, ViewSheet):
            view_count += 1

    categories = []
    for name, count in category_counts.items():
        categories.append({"category": name, "count": count})
    categories.sort(key=lambda entry: entry["count"], reverse=True)

    result = {
        "ok": True,
        "project": doc.Title,
        "total_elements": elements.Count,
        "total_element_types": FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount(),
        "loadable_families": _count_of_class(Family),
        "views": view_count,
        "sheets": _count_of_class(ViewSheet),
        "health": {
            "warnings": doc.GetWarnings().Count,
            "revit_links": _count_of_class(RevitLinkInstance),
            "cad_imports": _count_of_class(ImportInstance),
        },
        "categories": categories,
        "levels": levels,
    }

    if include_types:
        types = []
        for key, count in type_counts.items():
            types.append({
                "family": key[0],
                "type": key[1],
                "category": key[2],
                "count": count,
            })
        types.sort(key=lambda entry: entry["count"], reverse=True)
        result["types_truncated"] = len(types) > MAX_TYPES
        result["types"] = types[:MAX_TYPES]

    return result


_result = _main()
