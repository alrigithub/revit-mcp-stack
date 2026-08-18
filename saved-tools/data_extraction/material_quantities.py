# -*- coding: utf-8 -*-
# Material takeoff: per-material area (m2) and volume (m3) aggregated across
# the whole model, a single category, or the current selection. Pure read.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire),
# rewritten for IronPython 2.7.

from Autodesk.Revit.DB import (
    FilteredElementCollector, BuiltInCategory, Material,
    UnitUtils, UnitTypeId,
)


def _m2(value):
    return round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.SquareMeters), 3)


def _m3(value):
    return round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.CubicMeters), 3)


def _resolve_category(name):
    """Accept 'OST_Walls' or 'Walls'; return the BuiltInCategory or None."""
    candidates = [name]
    if not name.startswith("OST_"):
        candidates.append("OST_" + name.replace(" ", ""))
    for candidate in candidates:
        bic = getattr(BuiltInCategory, candidate, None)
        if bic is not None:
            return bic
    return None


def _collect_elements(category_name, selected_only):
    bic = None
    if category_name:
        bic = _resolve_category(category_name)
        if bic is None:
            message = ("unknown category %r; use a BuiltInCategory name like "
                       "'OST_Walls' or a plain name like 'Walls'") % category_name
            return None, message
    if selected_only:
        elements = []
        for element_id in uidoc.Selection.GetElementIds():
            element = doc.GetElement(element_id)
            if element is None:
                continue
            if bic is not None:
                category = element.Category
                if category is None or int(category.Id.Value) != int(bic):
                    continue
            elements.append(element)
        return elements, None
    collector = FilteredElementCollector(doc).WhereElementIsNotElementType()
    if bic is not None:
        collector = collector.OfCategory(bic)
    return list(collector.ToElements()), None


def _main():
    category_name = (request.get("category") or "").strip()
    selected_only = bool(request.get("selected_only", False))

    elements, error = _collect_elements(category_name, selected_only)
    if error:
        return {"ok": False, "error": error}

    totals = {}        # material id (int) -> accumulator dict
    element_sets = {}  # material id (int) -> set of element ids using it
    skipped = 0
    for element in elements:
        try:
            material_ids = element.GetMaterialIds(False)  # geometry materials, no paint
        except Exception:
            skipped += 1
            continue
        for material_id in material_ids:
            material = doc.GetElement(material_id)
            if not isinstance(material, Material):
                continue
            key = int(material_id.Value)
            if key not in totals:
                totals[key] = {
                    "material_id": key,
                    "name": material.Name,
                    "material_class": material.MaterialClass,
                    "area_internal": 0.0,
                    "volume_internal": 0.0,
                }
                element_sets[key] = set()
            totals[key]["area_internal"] += element.GetMaterialArea(material_id, False)
            totals[key]["volume_internal"] += element.GetMaterialVolume(material_id)
            element_sets[key].add(int(element.Id.Value))

    materials = []
    total_area = 0.0
    total_volume = 0.0
    for key, entry in totals.items():
        total_area += entry["area_internal"]
        total_volume += entry["volume_internal"]
        materials.append({
            "material_id": entry["material_id"],
            "name": entry["name"],
            "material_class": entry["material_class"],
            "area_m2": _m2(entry["area_internal"]),
            "volume_m3": _m3(entry["volume_internal"]),
            "element_count": len(element_sets[key]),
        })
    materials.sort(key=lambda m: m["area_m2"], reverse=True)

    return {
        "ok": True,
        "scope": "selection" if selected_only else (category_name or "model"),
        "elements_scanned": len(elements),
        "elements_skipped": skipped,
        "material_count": len(materials),
        "total_area_m2": _m2(total_area),
        "total_volume_m3": _m3(total_volume),
        "materials": materials,
    }


_result = _main()
