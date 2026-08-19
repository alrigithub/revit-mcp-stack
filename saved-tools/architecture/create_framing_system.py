# Create a structural beam system (BeamSystem) filling a rectangular boundary
# on a level: beams at a fixed center-to-center spacing running along X or Y,
# beam type resolved by name substring or auto-selected. Simplified from the
# source version: rectangular boundary only, fixed-distance layout, beams sit
# at the level (no Z offset), no auto-created levels.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire), rewritten for IronPython 2.7.

import Autodesk.Revit.DB as DB
import Autodesk.Revit.DB.Structure as DBS
from Autodesk.Revit.DB import (FilteredElementCollector, FamilySymbol, BuiltInCategory,
                               Level, Line, XYZ, ViewPlan)
from System.Collections.Generic import List as NetList

MM = 304.8


def type_name(element_type):
    # FamilySymbol.Name raises MissingMemberException in IronPython 2.7;
    # read the name through the base Element property instead.
    return DB.Element.Name.GetValue(element_type)


x_min = float(request.get("x_min_mm"))
x_max = float(request.get("x_max_mm"))
y_min = float(request.get("y_min_mm"))
y_max = float(request.get("y_max_mm"))
spacing = float(request.get("spacing_mm"))
direction = (request.get("direction") or "x").lower()
justify = (request.get("justify") or "center").lower()
level_name = request.get("level_name")
beam_type_name = request.get("beam_type_name")

if x_min >= x_max or y_min >= y_max:
    raise Exception("boundary must satisfy x_min_mm < x_max_mm and y_min_mm < y_max_mm")
if spacing <= 0:
    raise Exception("spacing_mm must be greater than 0")
if direction not in ("x", "y"):
    raise Exception("direction must be 'x' or 'y'")

justify_enum = getattr(DB, "BeamSystemJustifyType", None) or getattr(DBS, "BeamSystemJustifyType", None)
if justify_enum is None:
    raise Exception("BeamSystemJustifyType not found in the Revit API")
justify_map = {"beginning": justify_enum.Beginning, "center": justify_enum.Center, "end": justify_enum.End}
if justify not in justify_map:
    raise Exception("justify must be one of: %s" % ", ".join(sorted(justify_map.keys())))

# Resolve the level: explicit name, else active plan view's level, else lowest level.
levels = list(FilteredElementCollector(doc).OfClass(Level))
if not levels:
    raise Exception("no levels in the project; create a level first")
level = None
if level_name:
    for lv in levels:
        if lv.Name.lower() == level_name.lower():
            level = lv
            break
    if level is None:
        names = ", ".join([lv.Name for lv in levels])
        raise Exception("level not found: %s. Available: %s" % (level_name, names))
else:
    active_view = uidoc.ActiveView
    if isinstance(active_view, ViewPlan) and active_view.GenLevel is not None:
        level = active_view.GenLevel
    else:
        level = levels[0]
        for lv in levels:
            if lv.Elevation < level.Elevation:
                level = lv

# Resolve the beam type: substring match on "Family: Type", else first loaded type.
symbols = list(FilteredElementCollector(doc).OfClass(FamilySymbol)
               .OfCategory(BuiltInCategory.OST_StructuralFraming))
if not symbols:
    raise Exception("no structural framing families loaded; load a beam family first")
beam_type = None
if beam_type_name:
    wanted = beam_type_name.lower()
    for fs in symbols:
        full = "%s: %s" % (fs.Family.Name, type_name(fs))
        if wanted in full.lower():
            beam_type = fs
            break
    if beam_type is None:
        names = ", ".join(["%s: %s" % (fs.Family.Name, type_name(fs)) for fs in symbols[:30]])
        raise Exception("beam type matching %r not found. Loaded types: %s" % (beam_type_name, names))
else:
    for fs in symbols:
        if fs.IsActive:
            beam_type = fs
            break
    if beam_type is None:
        beam_type = symbols[0]
if not beam_type.IsActive:
    beam_type.Activate()

# Rectangular profile at the level elevation: bottom, right, top, left.
z = level.Elevation
p0 = XYZ(x_min / MM, y_min / MM, z)
p1 = XYZ(x_max / MM, y_min / MM, z)
p2 = XYZ(x_max / MM, y_max / MM, z)
p3 = XYZ(x_min / MM, y_max / MM, z)
curves = [Line.CreateBound(p0, p1), Line.CreateBound(p1, p2),
          Line.CreateBound(p2, p3), Line.CreateBound(p3, p0)]

# Beams run parallel to the direction curve: bottom edge for X, right edge for Y.
direction_index = 0 if direction == "x" else 1

profile = NetList[DB.Curve]()
for c in curves:
    profile.Add(c)

beam_system = DB.BeamSystem.Create(doc, profile, level, direction_index, False)
beam_system.BeamType = beam_type
beam_system.LayoutRule = DB.LayoutRuleFixedDistance(spacing / MM, justify_map[justify])
doc.Regenerate()  # materialize the member beams so their ids are available

beam_ids = [int(bid.Value) for bid in beam_system.GetBeamIds()]

_result = {
    "beam_system_id": int(beam_system.Id.Value),
    "beam_count": len(beam_ids),
    "beam_ids": beam_ids,
    "level": level.Name,
    "beam_type": "%s: %s" % (beam_type.Family.Name, type_name(beam_type)),
    "spacing_mm": spacing,
    "direction": direction,
    "justify": justify,
}
