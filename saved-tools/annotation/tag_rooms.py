# tag_rooms: tag every placed room visible in the active view with a room tag.
# Skips rooms that already carry a tag in this view and unplaced/unenclosed rooms.
# Optional leader; optional explicit room tag type.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire), rewritten for IronPython 2.7.
# Bridge runs this in one auto transaction - do not open transactions here.

from Autodesk.Revit.DB import (BuiltInCategory, BuiltInParameter, ElementId,
                               FilteredElementCollector, LinkElementId,
                               LocationPoint, UV)

MM_PER_FOOT = 304.8


def find_room_tag_type(requested_id):
    if requested_id > 0:
        candidate = doc.GetElement(ElementId(requested_id))
        if candidate is not None and candidate.Category is not None and \
                int(candidate.Category.Id.Value) == int(BuiltInCategory.OST_RoomTags):
            return candidate
    types = FilteredElementCollector(doc) \
        .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsElementType()
    for symbol in types:
        return symbol
    return None


def rooms_already_tagged(view):
    tagged = set()
    tags = FilteredElementCollector(doc, view.Id) \
        .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType()
    for tag in tags:
        room = tag.Room
        if room is not None:
            tagged.add(int(room.Id.Value))
    return tagged


def run():
    view = doc.ActiveView
    use_leader = bool(request.get("use_leader", False))
    requested_type_id = int(request.get("tag_type_id", 0))

    tag_symbol = find_room_tag_type(requested_type_id)
    if tag_symbol is None:
        return {"success": False,
                "message": "No room tag type is loaded in this project."}
    if not tag_symbol.IsActive:
        tag_symbol.Activate()
        doc.Regenerate()
    apply_type = requested_type_id > 0 and int(tag_symbol.Id.Value) == requested_type_id

    rooms = FilteredElementCollector(doc, view.Id) \
        .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElements()
    if rooms.Count == 0:
        return {"success": False, "view": view.Name,
                "message": "No rooms are visible in the active view. "
                           "Open a floor plan on the level that contains the rooms."}

    already_tagged = rooms_already_tagged(view)
    created = []
    skipped = 0
    errors = []
    for room in rooms:
        if room.Area <= 0:
            continue  # unplaced or not enclosed
        room_id = int(room.Id.Value)
        if room_id in already_tagged:
            skipped += 1
            continue
        try:
            location = room.Location
            if isinstance(location, LocationPoint):
                center = location.Point
            else:
                box = room.get_BoundingBox(view)
                if box is None:
                    continue
                center = (box.Min + box.Max) * 0.5
            tag = doc.Create.NewRoomTag(LinkElementId(room.Id),
                                        UV(center.X, center.Y), view.Id)
            if tag is None:
                continue
            if apply_type:
                tag.ChangeTypeId(tag_symbol.Id)
            if use_leader:
                tag.HasLeader = True
            name_param = room.get_Parameter(BuiltInParameter.ROOM_NAME)
            created.append({
                "tag_id": int(tag.Id.Value),
                "room_id": room_id,
                "room_name": (name_param.AsString() if name_param else None) or "Room",
                "room_number": room.Number,
                "location_mm": {"x": round(center.X * MM_PER_FOOT, 1),
                                "y": round(center.Y * MM_PER_FOOT, 1)},
            })
        except Exception as ex:
            errors.append("Room %d: %s" % (room_id, ex))

    result = {
        "success": True,
        "view": view.Name,
        "rooms_in_view": rooms.Count,
        "tagged": len(created),
        "skipped_already_tagged": skipped,
        "tags": created,
    }
    if errors:
        result["errors"] = errors
    return result


_result = run()
