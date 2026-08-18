# tag_walls: tag every wall visible in the active view at its midpoint.
# Skips walls that are already tagged in this view. Optional leader; optional
# explicit tag type (wall tag or multi-category tag).
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire), rewritten for IronPython 2.7.
# Bridge runs this in one auto transaction - do not open transactions here.

from Autodesk.Revit.DB import (BuiltInCategory, ElementId, FamilySymbol,
                               FilteredElementCollector, IndependentTag,
                               LocationCurve, Reference, TagOrientation)

MM_PER_FOOT = 304.8
TAG_CATEGORY_IDS = (int(BuiltInCategory.OST_WallTags),
                    int(BuiltInCategory.OST_MultiCategoryTags))


def find_wall_tag_type(requested_id):
    if requested_id > 0:
        candidate = doc.GetElement(ElementId(requested_id))
        if isinstance(candidate, FamilySymbol) and candidate.Category is not None and \
                int(candidate.Category.Id.Value) in TAG_CATEGORY_IDS:
            return candidate
    for category in (BuiltInCategory.OST_WallTags, BuiltInCategory.OST_MultiCategoryTags):
        types = FilteredElementCollector(doc) \
            .OfCategory(category).WhereElementIsElementType()
        for symbol in types:
            return symbol
    return None


def elements_already_tagged(view):
    tagged = set()
    tags = FilteredElementCollector(doc, view.Id).OfClass(IndependentTag)
    for tag in tags:
        try:
            for element_id in tag.GetTaggedLocalElementIds():
                tagged.add(int(element_id.Value))
        except Exception:
            pass
    return tagged


def run():
    view = doc.ActiveView
    use_leader = bool(request.get("use_leader", False))
    requested_type_id = int(request.get("tag_type_id", 0))

    tag_symbol = find_wall_tag_type(requested_type_id)
    if tag_symbol is None:
        return {"success": False,
                "message": "No wall tag or multi-category tag type is loaded in this project."}
    if not tag_symbol.IsActive:
        tag_symbol.Activate()
        doc.Regenerate()

    walls = FilteredElementCollector(doc, view.Id) \
        .OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().ToElements()
    if walls.Count == 0:
        return {"success": False, "view": view.Name,
                "message": "No walls are visible in the active view."}

    already_tagged = elements_already_tagged(view)
    created = []
    skipped = 0
    errors = []
    for wall in walls:
        wall_id = int(wall.Id.Value)
        if wall_id in already_tagged:
            skipped += 1
            continue
        location = wall.Location
        if not isinstance(location, LocationCurve):
            continue  # skip walls without a location curve (e.g. some in-place walls)
        try:
            midpoint = location.Curve.Evaluate(0.5, True)
            tag = IndependentTag.Create(doc, tag_symbol.Id, view.Id, Reference(wall),
                                        use_leader, TagOrientation.Horizontal, midpoint)
            if tag is None:
                continue
            created.append({
                "tag_id": int(tag.Id.Value),
                "wall_id": wall_id,
                "wall_name": wall.Name,
                "location_mm": {"x": round(midpoint.X * MM_PER_FOOT, 1),
                                "y": round(midpoint.Y * MM_PER_FOOT, 1)},
            })
        except Exception as ex:
            errors.append("Wall %d: %s" % (wall_id, ex))

    result = {
        "success": True,
        "view": view.Name,
        "walls_in_view": walls.Count,
        "tagged": len(created),
        "skipped_already_tagged": skipped,
        "tags": created,
    }
    if errors:
        result["errors"] = errors
    return result


_result = run()
