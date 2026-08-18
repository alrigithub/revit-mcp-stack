# create_dimensions: create one linear dimension in the active view across two
# or more elements. Picks each element's best planar-face reference (normal most
# aligned with the dimension direction), falling back to the element itself.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire), rewritten for IronPython 2.7.
# Bridge runs this in one auto transaction - do not open transactions here.

from Autodesk.Revit.DB import (DimensionType, ElementId, GeometryInstance, Line,
                               LocationCurve, LocationPoint, Options, PlanarFace,
                               Reference, ReferenceArray, Solid, XYZ)

MM_PER_FOOT = 304.8


def anchor_point(element, view):
    location = element.Location
    if isinstance(location, LocationPoint):
        return location.Point
    if isinstance(location, LocationCurve):
        return location.Curve.Evaluate(0.5, True)
    box = element.get_BoundingBox(view)
    if box is not None:
        return (box.Min + box.Max) * 0.5
    return None


def best_face_reference(element, view, direction):
    """Planar face whose normal aligns best with the dimension direction."""
    options = Options()
    options.View = view
    options.ComputeReferences = True
    geometry = element.get_Geometry(options)
    if geometry is None:
        return None
    solids = []
    for item in geometry:
        if isinstance(item, Solid) and item.Faces.Size > 0:
            solids.append(item)
        elif isinstance(item, GeometryInstance):
            for nested in item.GetInstanceGeometry():
                if isinstance(nested, Solid) and nested.Faces.Size > 0:
                    solids.append(nested)
    best = None
    best_alignment = -1.0
    for solid in solids:
        for face in solid.Faces:
            if not isinstance(face, PlanarFace) or face.Reference is None:
                continue
            normal = face.FaceNormal
            if abs(normal.Z) > 0.9:
                continue  # skip top/bottom faces, useless in plan
            alignment = abs(normal.DotProduct(direction))
            if alignment > best_alignment:
                best_alignment = alignment
                best = face.Reference
    return best


def run():
    view = doc.ActiveView
    raw_ids = request.get("element_ids") or []
    if len(raw_ids) < 2:
        return {"success": False,
                "message": "element_ids needs at least two element ids."}

    elements = []
    points = []
    for raw in raw_ids:
        element = doc.GetElement(ElementId(int(raw)))
        if element is None:
            return {"success": False, "message": "Element id %s not found." % raw}
        point = anchor_point(element, view)
        if point is None:
            return {"success": False,
                    "message": "Element id %s has no usable location." % raw}
        elements.append(element)
        points.append(point)

    span = points[-1] - points[0]
    if span.GetLength() < 0.003:  # under ~1 mm
        return {"success": False,
                "message": "First and last element are at the same location; "
                           "cannot orient the dimension line."}
    direction = span.Normalize()

    references = ReferenceArray()
    for element in elements:
        reference = best_face_reference(element, view, direction)
        if reference is None:
            reference = Reference(element)
        references.Append(reference)

    offset_feet = float(request.get("offset_mm", 1000.0)) / MM_PER_FOOT
    perpendicular = direction.CrossProduct(XYZ.BasisZ)
    if perpendicular.GetLength() < 1e-9:  # vertical dimension (section/elevation)
        perpendicular = direction.CrossProduct(XYZ.BasisX)
    perpendicular = perpendicular.Normalize()
    start = points[0] + perpendicular * offset_feet
    end = points[-1] + perpendicular * offset_feet

    try:
        dimension = doc.Create.NewDimension(view, Line.CreateBound(start, end), references)
    except Exception as ex:
        return {"success": False,
                "message": "NewDimension failed (references may not be valid "
                           "in this view): %s" % ex}
    if dimension is None:
        return {"success": False, "message": "Revit did not create the dimension."}

    type_id = int(request.get("dimension_type_id", 0))
    if type_id > 0:
        dimension_type = doc.GetElement(ElementId(type_id))
        if isinstance(dimension_type, DimensionType):
            dimension.DimensionType = dimension_type

    return {
        "success": True,
        "dimension_id": int(dimension.Id.Value),
        "reference_count": references.Size,
        "view": view.Name,
    }


_result = run()
