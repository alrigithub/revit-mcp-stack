# Create a rectangular grid system from bay spacings in two directions.
# Grids along X (vertical lines, parallel to Y) are placed by x_spacings;
# grids along Y (horizontal lines, parallel to X) are placed by y_spacings.
# Naming per direction is inferred from the start label: a letter gives
# A, B, ... Z, AA, AB; digits give 1, 2, 3. Duplicates get a numeric suffix.
# Ported from mcp-servers-for-revit (MIT, (c) 2026 sparx-fire), rewritten for IronPython 2.7.

from Autodesk.Revit.DB import FilteredElementCollector, Grid, Line, XYZ

MM = 304.8
MAX_GRIDS = 500


def _positions(origin, spacings, label):
    if not isinstance(spacings, list):
        raise Exception("%s must be a list of bay widths in mm" % label)
    positions = [float(origin)]
    for i in range(len(spacings)):
        value = spacings[i]
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value <= 0:
            raise Exception("%s[%d] must be a positive number in mm, got %r" % (label, i, value))
        positions.append(positions[-1] + float(value))
    return positions


def _alpha_label(index):
    # 0 -> A ... 25 -> Z, 26 -> AA, 27 -> AB ... (Excel-style)
    name = ""
    n = index
    while True:
        name = chr(ord("A") + n % 26) + name
        n = n // 26 - 1
        if n < 0:
            return name


def _labels(count, start_label):
    text = (start_label or "").strip()
    if text.isdigit():
        start = int(text)
        return [str(start + i) for i in range(count)]
    first = text[:1].upper()
    if first < "A" or first > "Z":
        first = "A"
    offset = ord(first) - ord("A")
    return [_alpha_label(offset + i) for i in range(count)]


def _unique(base, taken):
    name = base
    counter = 1
    while name.lower() in taken:
        name = "%s%d" % (base, counter)
        counter += 1
    taken.add(name.lower())
    return name


extension = float(request.get("extension_mm"))
if extension <= 0:
    raise Exception("extension_mm must be greater than 0")

x_positions = _positions(request.get("origin_x_mm"), request.get("x_spacings"), "x_spacings")
y_positions = _positions(request.get("origin_y_mm"), request.get("y_spacings"), "y_spacings")
total = len(x_positions) + len(y_positions)
if total > MAX_GRIDS:
    raise Exception("refusing to create %d grids (limit %d)" % (total, MAX_GRIDS))

x_labels = _labels(len(x_positions), request.get("x_start_label"))
y_labels = _labels(len(y_positions), request.get("y_start_label"))

existing_names = set()
for g in FilteredElementCollector(doc).OfClass(Grid):
    existing_names.add(g.Name.lower())

# Grid lines span the other direction's full run plus the extension on both ends.
y_lo = (min(y_positions) - extension) / MM
y_hi = (max(y_positions) + extension) / MM
x_lo = (min(x_positions) - extension) / MM
x_hi = (max(x_positions) + extension) / MM

created = []
for i in range(len(x_positions)):
    x = x_positions[i]
    name = _unique(x_labels[i], existing_names)
    line = Line.CreateBound(XYZ(x / MM, y_lo, 0.0), XYZ(x / MM, y_hi, 0.0))
    grid = Grid.Create(doc, line)
    grid.Name = name
    created.append({"element_id": int(grid.Id.Value), "name": name, "axis": "X",
                    "position_mm": round(x, 1), "renamed": name != x_labels[i]})

for i in range(len(y_positions)):
    y = y_positions[i]
    name = _unique(y_labels[i], existing_names)
    line = Line.CreateBound(XYZ(x_lo, y / MM, 0.0), XYZ(x_hi, y / MM, 0.0))
    grid = Grid.Create(doc, line)
    grid.Name = name
    created.append({"element_id": int(grid.Id.Value), "name": name, "axis": "Y",
                    "position_mm": round(y, 1), "renamed": name != y_labels[i]})

renamed_count = 0
for entry in created:
    if entry["renamed"]:
        renamed_count += 1

_result = {
    "created_count": len(created),
    "x_count": len(x_positions),
    "y_count": len(y_positions),
    "renamed_count": renamed_count,
    "grids": created,
}
