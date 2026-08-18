# -*- coding: utf-8 -*-
# Exports all rooms with name, number, level, area (m2), volume (m3),
# perimeter (mm), unbounded height (mm), and key parameters (department,
# occupancy, phase, comments). Pure read.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire),
# rewritten for IronPython 2.7.

from Autodesk.Revit.DB import (
    FilteredElementCollector, BuiltInCategory, BuiltInParameter,
    UnitUtils, UnitTypeId,
)
from Autodesk.Revit.DB.Architecture import Room


def _m2(value):
    return round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.SquareMeters), 3)


def _m3(value):
    return round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.CubicMeters), 3)


def _mm(value):
    return round(UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters), 1)


def _param_string(element, built_in):
    parameter = element.get_Parameter(built_in)
    if parameter is None:
        return ""
    value = parameter.AsString()
    return value if value else ""


def _phase_name(room):
    parameter = room.get_Parameter(BuiltInParameter.ROOM_PHASE)
    if parameter is None:
        return ""
    phase = doc.GetElement(parameter.AsElementId())
    return phase.Name if phase is not None else ""


def _main():
    include_unplaced = bool(request.get("include_unplaced", False))
    level_filter = (request.get("level") or "").strip()

    collector = (FilteredElementCollector(doc)
                 .OfCategory(BuiltInCategory.OST_Rooms)
                 .WhereElementIsNotElementType())

    rooms = []
    total_area = 0.0
    skipped_zero_area = 0
    for room in collector:
        if not isinstance(room, Room):
            continue
        if room.Area == 0 and not include_unplaced:
            skipped_zero_area += 1
            continue
        level = room.Level
        level_name = level.Name if level is not None else ""
        if level_filter and level_name != level_filter:
            continue
        rooms.append({
            "id": int(room.Id.Value),
            "unique_id": room.UniqueId,
            "name": _param_string(room, BuiltInParameter.ROOM_NAME),
            "number": room.Number if room.Number else "",
            "level": level_name,
            "is_placed": room.Location is not None,
            "area_m2": _m2(room.Area),
            "volume_m3": _m3(room.Volume),
            "perimeter_mm": _mm(room.Perimeter),
            "unbounded_height_mm": _mm(room.UnboundedHeight),
            "department": _param_string(room, BuiltInParameter.ROOM_DEPARTMENT),
            "occupancy": _param_string(room, BuiltInParameter.ROOM_OCCUPANCY),
            "comments": _param_string(room, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS),
            "phase": _phase_name(room),
        })
        total_area += room.Area

    rooms.sort(key=lambda entry: (entry["level"], entry["number"]))

    return {
        "ok": True,
        "room_count": len(rooms),
        "skipped_zero_area_rooms": skipped_zero_area,
        "total_area_m2": _m2(total_area),
        "volume_note": "volume_m3 is 0 unless Areas and Volumes computation is enabled in Revit",
        "rooms": rooms,
    }


_result = _main()
