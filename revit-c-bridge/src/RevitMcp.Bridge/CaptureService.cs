using Autodesk.Revit.DB;
using RevitMcp.Contracts;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RView = Autodesk.Revit.DB.View;
using WPoint = System.Windows.Point;

namespace RevitMcp.Bridge;

/// <summary>One native Revit implementation for MCP and ribbon helpers. No Python dependency.</summary>
public static class CaptureService
{
    private const double FeetPerMetre = 1 / 0.3048;
    private const string Prefix = "3XN MCP - ";
    private sealed record Item(Element Element, BoundingBoxXYZ Box);
    private sealed record Shot(string Key, string Label, string Group, BoundingBoxXYZ Box, XYZ Forward, XYZ Up,
        bool Section = false, ICollection<ElementId>? Isolate = null, string? Association = null);
    private sealed record Rendered(long ViewId, string Label, string Group, string Path, double Width, double Height, object Frame, string? Association);

    public static object Capture(Document doc, JsonElement args, Action<string>? observeOutcome = null)
    {
        if (doc.IsModifiable || doc.IsReadOnly) throw new RequestDispatchException("capture_document_busy", "Capture needs a writable document with no open transaction.");
        var preset = Text(args, "preset", "building");
        if (!new[] { "building", "elevations", "axo", "floors", "element", "horizontal", "vertical" }.Contains(preset))
            throw new RequestDispatchException("invalid_capture_preset", "Use building, elevations, axo, floors, element, horizontal, or vertical.");
        var requestedIds = Longs(args, "element_ids");
        if (requestedIds.Length > 1000) throw new RequestDispatchException("capture_selection_limit", "Select at most 1000 elements.");
        if (preset == "element" && requestedIds.Length == 0) throw new RequestDispatchException("selection_required", "Element inspection needs element_ids.");
        var margin = Math.Clamp(Number(args, "margin_m", preset == "element" ? 1 : 2), 0.05, 100) * FeetPerMetre;
        var pixels = Math.Clamp((int)Number(args, "pixel_size", 1600), 512, 3200);
        var fraction = Math.Clamp(Number(args, "cut_fraction", 0.5), 0.01, 0.99);
        var selected = requestedIds.Select(id => doc.GetElement(new ElementId(id))).ToArray();
        if (selected.Any(e => e is null)) throw new RequestDispatchException("element_not_found", "At least one selected element no longer exists.");
        var physical = Physical(doc).ToArray();
        var scope = requestedIds.Length > 0 ? selected.Select(e => BoxItem(e!)).Where(x => x is not null).Cast<Item>().ToArray() : BuildingScope(doc, physical);
        if (scope.Length == 0) throw new RequestDispatchException("capture_empty_scope", "No physical geometry with a bounding box was found in this scope.");
        var tight = Union(scope.Select(i => i.Box));
        var frame = Expand(tight, margin);
        var axes = ElementAxes(doc, scope, tight);
        var shots = new List<Shot>();
        var scopeNote = requestedIds.Length > 0 ? "Explicit selection bounds; surrounding geometry remains visible in context views."
            : "Physical geometry near the grid envelope when available. For multiple buildings, pass an explicit selection.";
        if (preset is "building" or "elevations")
        {
            shots.Add(new("north", "North elevation · project north", "elevations", frame, -XYZ.BasisY, XYZ.BasisZ));
            shots.Add(new("south", "South elevation", "elevations", frame, XYZ.BasisY, XYZ.BasisZ));
            shots.Add(new("east", "East elevation", "elevations", frame, -XYZ.BasisX, XYZ.BasisZ));
            shots.Add(new("west", "West elevation", "elevations", frame, XYZ.BasisX, XYZ.BasisZ));
        }
        if (preset is "building" or "axo")
        {
            shots.Add(new("ne", "NE axonometric", "axo", frame, new XYZ(-1, -1, -0.8), XYZ.BasisZ));
            shots.Add(new("nw", "NW axonometric", "axo", frame, new XYZ(1, -1, -0.8), XYZ.BasisZ));
            shots.Add(new("se", "SE axonometric", "axo", frame, new XYZ(-1, 1, -0.8), XYZ.BasisZ));
            shots.Add(new("sw", "SW axonometric", "axo", frame, new XYZ(1, 1, -0.8), XYZ.BasisZ));
        }
        if (preset == "floors")
        {
            var requestedLevels = Longs(args, "level_ids");
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToArray();
            var candidates = levels.Where(l => requestedLevels.Contains(l.Id.Value) || (requestedLevels.Length == 0 && physical.Any(p => p.Element is Floor && LevelOf(p.Element) == l.Id.Value))).ToArray();
            if (requestedLevels.Except(levels.Select(l => l.Id.Value)).Any()) throw new RequestDispatchException("level_not_found", "A requested level no longer exists.");
            if (candidates.Length == 0) throw new RequestDispatchException("capture_no_floor_levels", "No floor-hosting levels found. Pass level_ids explicitly.");
            if (candidates.Length > 24) throw new RequestDispatchException("capture_level_limit", "Capture at most 24 levels per call; pass level_ids.");
            foreach (var level in candidates)
            {
                var next = levels.FirstOrDefault(l => l.Elevation > level.Elevation + 0.01);
                var top = next?.Elevation ?? tight.Max.Z + 0.1;
                var bottom = level.Elevation - 0.5 * FeetPerMetre;
                var included = physical.Where(p => IntersectsXY(p.Box, tight) && (LevelOf(p.Element) == level.Id.Value
                    || HostLevel(doc, p.Element) == level.Id.Value || (p.Element is not Floor && p.Element is not Ceiling && p.Element is not RoofBase
                        && p.Box.Max.Z > level.Elevation + 0.01 && p.Box.Min.Z < top - 0.01))).Select(p => p.Element.Id).ToHashSet();
                IncludeComponents(doc, included);
                var box = new BoundingBoxXYZ { Min = new XYZ(frame.Min.X, frame.Min.Y, bottom), Max = new XYZ(frame.Max.X, frame.Max.Y, Math.Max(bottom + 0.1, top)) };
                shots.Add(new("floor-" + level.Id.Value, level.Name + " · " + (level.Elevation * 0.3048).ToString("0.00", CultureInfo.InvariantCulture) + " m", "floors",
                    box, new XYZ(-1, -1, -0.8), XYZ.BasisZ, Isolate: included.ToArray(),
                    Association: "Level + host + geometric storey intersection; section box clips spanning elements. " + included.Count + " associated elements."));
            }
        }
        if (preset == "element")
        {
            var isolated = scope.Select(p => p.Element.Id).ToHashSet();
            IncludeComponents(doc, isolated);
            // A shallow tilt from the element's face is more legible than a fixed global 45-degree view.
            var direction = -axes.Short - axes.Long * 0.18 - XYZ.BasisZ * 0.25;
            shots.Add(new("context", "Element · section box with context", "element", frame, direction, XYZ.BasisZ));
            if (Bool(args, "include_isolated", true)) shots.Add(new("isolated", "Element + components · isolated", "element", frame, direction, XYZ.BasisZ, Isolate: isolated.ToArray()));
            shots.Add(Section("horizontal", "Horizontal middle cut · with context", "element", tight, margin, -XYZ.BasisZ, axes.Short, fraction));
            shots.Add(Section("longitudinal", "Longitudinal middle cut · with context", "element", tight, margin, axes.Short, XYZ.BasisZ, fraction));
        }
        if (preset == "horizontal") shots.Add(Section("horizontal", "Horizontal cut · with context", "sections", tight, margin, -XYZ.BasisZ, axes.Short, fraction));
        if (preset == "vertical")
        {
            var along = Text(args, "axis", "long") == "short" ? axes.Long : axes.Short;
            shots.Add(Section("vertical", Text(args, "axis", "long") + " side cut · with context", "sections", tight, margin, along, XYZ.BasisZ, fraction));
        }
        var directory = NewDirectory(args, "capture");
        var rendered = new List<Rendered>();
        using var group = new TransactionGroup(doc, "3XN MCP inspection views");
        if (group.Start() != TransactionStatus.Started) throw new RequestDispatchException("capture_transaction_failed", "Could not start inspection view group.");
        try
        {
            var views = new List<RView>();
            using (var transaction = new Transaction(doc, "Create or update inspection views"))
            {
                transaction.Start();
                foreach (var shot in shots)
                {
                    ScriptControl.ThrowIfCancellationRequested();
                    views.Add(CreateView(doc, preset, shot));
                }
                doc.Regenerate();
                if (transaction.Commit() != TransactionStatus.Committed) throw new RequestDispatchException("capture_view_commit_failed", "Revit rejected inspection views.");
            }
            for (var index = 0; index < views.Count; index++)
            {
                ScriptControl.ReportProgress("Exporting " + shots[index].Label, 100.0 * index / views.Count);
                var path = ExportPng(doc, views[index].Id, directory, (index + 1).ToString("00") + "-" + shots[index].Key, pixels);
                var shot = shots[index];
                var projected = ProjectedExtents(shot.Box, shot.Forward, shot.Up);
                rendered.Add(new(views[index].Id.Value, shot.Label, shot.Group, path, projected.X, projected.Y, VerificationService.Box(shot.Box)!, shot.Association));
            }
            var sheets = new List<string>();
            foreach (var set in rendered.GroupBy(r => r.Group))
                foreach (var page in set.Chunk(4).Select((items, i) => (items, i)))
                    sheets.Add(ContactSheet(page.items, Path.Combine(directory, set.Key + "-" + (page.i + 1) + ".png")));
            var result = ResultProjection.Materialize(new
            {
                preset, scope = scopeNote, bounds = VerificationService.Box(frame), scope_element_count = scope.Length,
                views = rendered.Select(r => new { view_id = r.ViewId, label = r.Label, image = r.Path, frame = r.Frame, association = r.Association }).ToArray(),
                contact_sheets = sheets, output_directory = directory,
                note = "Dedicated reusable inspection views; working view and selection are preserved. Elevations are orthographic 3D projections. Cuts are native section views. Isolation retains parent containers required to display hosted components."
            });
            if (group.Assimilate() != TransactionStatus.Committed) throw new RequestDispatchException("capture_group_failed", "Inspection view group did not commit.");
            observeOutcome?.Invoke("committed");
            return result;
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            observeOutcome?.Invoke(group.GetStatus() == TransactionStatus.RolledBack ? "rolled_back" : "unknown");
            throw;
        }
    }

    public static object ExportExisting(Document doc, JsonElement args)
    {
        if (doc.IsModifiable) throw new RequestDispatchException("export_transaction_open", "Export must run outside a transaction.");
        var ids = Longs(args, "view_ids");
        if (ids.Length == 0) ids = [args.GetProperty("view_id").GetInt64()];
        if (ids.Length > 24) throw new RequestDispatchException("export_view_limit", "Export at most 24 views.");
        foreach (var id in ids)
            if (doc.GetElement(new ElementId(id)) is not RView view || view.IsTemplate || !view.CanBePrinted)
                throw new RequestDispatchException("view_not_exportable", "A requested view is missing, a template, or cannot be printed.");
        var format = Text(args, "format", "pdf").ToLowerInvariant();
        if (format is not ("pdf" or "png")) throw new RequestDispatchException("invalid_export_format", "Use pdf or png.");
        var directory = NewDirectory(args, "export");
        var name = SafeName(Text(args, "file_name", "revit-view"));
        var artifacts = new List<string>();
        if (format == "png")
        {
            foreach (var id in ids) artifacts.Add(ExportPng(doc, new ElementId(id), directory, name + "-" + id, Math.Clamp((int)Number(args, "pixel_size", 1600), 512, 3200)));
        }
        else
        {
            using var options = new PDFExportOptions { FileName = name, Combine = true };
            if (!doc.Export(directory, ids.Select(id => new ElementId(id)).ToList(), options)) throw new RequestDispatchException("pdf_export_failed", "Revit did not export the requested PDF.");
            artifacts.AddRange(Directory.GetFiles(directory, "*.pdf"));
            if (artifacts.Count == 0) throw new RequestDispatchException("export_file_missing", "Revit returned success without a PDF file.");
        }
        return new { exported = true, format, view_ids = ids, artifacts, artifact = artifacts.FirstOrDefault(), output_directory = directory };
    }

    private static RView CreateView(Document doc, string preset, Shot shot)
    {
        var name = Prefix + preset + " · " + shot.Key;
        var existing = new FilteredElementCollector(doc).OfClass(typeof(RView)).Cast<RView>().FirstOrDefault(v => !v.IsTemplate && v.Name == name);
        RView view;
        if (shot.Section)
        {
            // Section direction is immutable: replace only our reserved helper view when its frame changes.
            if (existing is not null)
            {
                if (doc.ActiveView.Id == existing.Id) throw new RequestDispatchException("inspection_view_active", "Switch to a working view before rebuilding this inspection section.");
                doc.Delete(existing.Id);
            }
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Section)
                ?? throw new RequestDispatchException("section_type_missing", "The document has no section view type.");
            view = ViewSection.CreateSection(doc, type.Id, shot.Box);
            view.Name = name;
        }
        else
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new RequestDispatchException("3d_view_type_missing", "The document has no 3D view type.");
            var v3 = existing as View3D ?? View3D.CreateIsometric(doc, type.Id);
            if (existing is null) v3.Name = name;
            if (v3.IsLocked) v3.Unlock();
            v3.ViewTemplateId = ElementId.InvalidElementId;
            v3.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            // Reset only this reserved helper's previous permanent isolation.
            var hidden = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => e.IsHidden(v3)).Select(e => e.Id).ToList();
            if (hidden.Count > 0) v3.UnhideElements(hidden);
            var forward = shot.Forward.Normalize();
            var right = forward.CrossProduct(shot.Up).Normalize();
            var up = right.CrossProduct(forward).Normalize();
            var centre = (shot.Box.Min + shot.Box.Max) * 0.5;
            v3.SetOrientation(new ViewOrientation3D(centre - forward * Math.Max(shot.Box.Max.DistanceTo(shot.Box.Min) * 2, 20), up, forward));
            v3.SetSectionBox(shot.Box);
            v3.IsSectionBoxActive = true;
            if (shot.Isolate is { Count: > 0 }) IsolateForExport(doc, v3, shot.Isolate);
            // A section box does not resize Revit's pre-existing 2D crop region.
            doc.Regenerate();
            var crop = v3.CropBox;
            var points = Corners(shot.Box).Select(crop.Transform.Inverse.OfPoint).ToArray();
            crop.Min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), crop.Min.Z);
            crop.Max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), crop.Max.Z);
            v3.CropBox = crop;
            view = v3;
        }
        view.ViewTemplateId = ElementId.InvalidElementId;
        view.DetailLevel = ViewDetailLevel.Fine;
        view.DisplayStyle = DisplayStyle.HLR;
        view.CropBoxActive = true;
        view.CropBoxVisible = false;
        foreach (Category category in doc.Settings.Categories)
            if (category.CategoryType == CategoryType.Annotation && view.CanCategoryBeHidden(category.Id)) view.SetCategoryHidden(category.Id, true);
        return view;
    }

    private static Shot Section(string key, string label, string group, BoundingBoxXYZ tight, double margin, XYZ forward, XYZ approximateUp, double fraction)
    {
        var z = forward.Normalize();
        var x = approximateUp.CrossProduct(z).Normalize();
        var y = z.CrossProduct(x).Normalize();
        var transform = Autodesk.Revit.DB.Transform.Identity;
        transform.Origin = (tight.Min + tight.Max) * 0.5;
        transform.BasisX = x; transform.BasisY = y; transform.BasisZ = z;
        var points = Corners(tight).Select(transform.Inverse.OfPoint).ToArray();
        var local = Bounds(points);
        // Position the cutting plane through the requested fraction, looking into the remaining half.
        var cut = local.Min.Z + (local.Max.Z - local.Min.Z) * fraction;
        transform.Origin += z * cut;
        var box = new BoundingBoxXYZ { Transform = transform,
            Min = new XYZ(local.Min.X - margin, local.Min.Y - margin, 0),
            Max = new XYZ(local.Max.X + margin, local.Max.Y + margin, Math.Max(0.1, local.Max.Z - cut + margin)) };
        return new(key, label, group, box, forward, y, true);
    }

    private static IEnumerable<Item> Physical(Document doc) => new FilteredElementCollector(doc).WhereElementIsNotElementType()
        .Where(e => e.Category?.CategoryType == CategoryType.Model && e is not RevitLinkInstance && !e.ViewSpecific
            && e.Category.Id.Value is not ((long)BuiltInCategory.OST_Topography) and not ((long)BuiltInCategory.OST_Toposolid)
            && e is not Level && e is not Grid && e is not ReferencePlane && e is not BasePoint
            && e is not InternalOrigin && e.Category.Id.Value != (long)BuiltInCategory.OST_Cameras)
        .Select(BoxItem).Where(x => x is not null).Cast<Item>();
    private static Item? BoxItem(Element element)
    {
        try
        {
            var box = element.get_BoundingBox(null);
            return box is null ? null : new(element, Bounds(Corners(box)));
        }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException) { return null; }
    }
    private static Item[] BuildingScope(Document doc, Item[] physical)
    {
        var points = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve.IsBound)
            .SelectMany(g => new[] { g.Curve.GetEndPoint(0), g.Curve.GetEndPoint(1) }).ToArray();
        if (points.Length < 4) return physical;
        var hint = Expand(Bounds(points), 5 * FeetPerMetre);
        var near = physical.Where(i => IntersectsXY(hint, i.Box)).ToArray();
        return near.Length > 0 ? near : physical;
    }
    private static (XYZ Long, XYZ Short) ElementAxes(Document doc, Item[] items, BoundingBoxXYZ box)
    {
        XYZ axis = XYZ.BasisX;
        if (items.Length == 1)
        {
            var e = items[0].Element;
            if (e is Panel or Mullion)
            {
                var host = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault(w => w.CurtainGrid is { } grid
                    && (grid.GetPanelIds().Contains(e.Id) || grid.GetMullionIds().Contains(e.Id)));
                if (host is not null) e = host;
            }
            if (e.Location is LocationCurve location && location.Curve.IsBound)
            {
                var projection = location.Curve.Project((box.Min + box.Max) * 0.5);
                axis = projection is not null ? location.Curve.ComputeDerivatives(projection.Parameter, false).BasisX
                    : location.Curve.GetEndPoint(1) - location.Curve.GetEndPoint(0);
            }
            else if (e is FamilyInstance family) axis = family.GetTransform().BasisX;
        }
        axis = new XYZ(axis.X, axis.Y, 0);
        if (axis.GetLength() < 1e-6) axis = XYZ.BasisX;
        axis = axis.Normalize();
        var other = XYZ.BasisZ.CrossProduct(axis).Normalize();
        var points = items.SelectMany(i => Corners(i.Box)).ToArray();
        var a = points.Max(p => p.DotProduct(axis)) - points.Min(p => p.DotProduct(axis));
        var b = points.Max(p => p.DotProduct(other)) - points.Min(p => p.DotProduct(other));
        return a >= b ? (axis, other) : (other, -axis);
    }
    private static long LevelOf(Element e) => e.LevelId != ElementId.InvalidElementId ? e.LevelId.Value
        : e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId()?.Value ?? -1;
    private static long HostLevel(Document doc, Element e) => e is FamilyInstance f && f.Host is { } host ? LevelOf(host) : -1;
    private static void IncludeComponents(Document doc, HashSet<ElementId> ids)
    {
        var pending = new Queue<ElementId>(ids);
        while (pending.TryDequeue(out var id))
        {
            var element = doc.GetElement(id);
            IEnumerable<ElementId> children = element is FamilyInstance family ? family.GetSubComponentIds() : [];
            if (element is Wall wall && wall.CurtainGrid is { } grid) children = children.Concat(grid.GetPanelIds()).Concat(grid.GetMullionIds());
            foreach (var child in children) if (ids.Add(child)) pending.Enqueue(child);
        }
    }
    private static void IsolateForExport(Document doc, View3D view, ICollection<ElementId> elements)
    {
        var keep = elements.ToHashSet();
        var siblings = new HashSet<ElementId>();
        // System curtain panels require their wall container to remain visible.
        foreach (var wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
        {
            if (wall.CurtainGrid is not { } grid || keep.Contains(wall.Id)) continue;
            var children = grid.GetPanelIds().Concat(grid.GetMullionIds()).ToArray();
            if (!children.Any(keep.Contains)) continue;
            keep.Add(wall.Id);
            foreach (var id in children) if (!keep.Contains(id)) siblings.Add(id);
        }
        foreach (var id in elements)
        {
            var family = doc.GetElement(id) as FamilyInstance;
            while (family?.SuperComponent is { } parent) { keep.Add(parent.Id); family = parent as FamilyInstance; }
        }
        view.IsolateElementsTemporary(keep.ToArray());
        // ExportImage ignores temporary isolation; persist it in our helper only.
        view.ConvertTemporaryHideIsolateToPermanent();
        var hide = siblings.Where(id => !doc.GetElement(id).IsHidden(view) && doc.GetElement(id).CanBeHidden(view)).ToArray();
        if (hide.Length > 0) view.HideElements(hide);
    }
    private static bool IntersectsXY(BoundingBoxXYZ a, BoundingBoxXYZ b) => a.Min.X <= b.Max.X && a.Max.X >= b.Min.X && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;
    private static BoundingBoxXYZ Union(IEnumerable<BoundingBoxXYZ> boxes) => Bounds(boxes.SelectMany(Corners));
    private static BoundingBoxXYZ Expand(BoundingBoxXYZ b, double margin) => new() { Min = b.Min - new XYZ(margin, margin, margin), Max = b.Max + new XYZ(margin, margin, margin) };
    private static BoundingBoxXYZ Bounds(IEnumerable<XYZ> source)
    {
        var p = source.ToArray();
        return new() { Min = new XYZ(p.Min(v => v.X), p.Min(v => v.Y), p.Min(v => v.Z)), Max = new XYZ(p.Max(v => v.X), p.Max(v => v.Y), p.Max(v => v.Z)) };
    }
    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ b)
    {
        for (var i = 0; i < 8; i++) yield return b.Transform.OfPoint(new XYZ((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
    }
    private static XYZ ProjectedExtents(BoundingBoxXYZ b, XYZ direction, XYZ approximateUp)
    {
        var forward = direction.Normalize(); var right = forward.CrossProduct(approximateUp).Normalize(); var up = right.CrossProduct(forward);
        var points = Corners(b).ToArray();
        return new XYZ(Math.Max(0.1, points.Max(p => p.DotProduct(right)) - points.Min(p => p.DotProduct(right))),
            Math.Max(0.1, points.Max(p => p.DotProduct(up)) - points.Min(p => p.DotProduct(up))), 0);
    }
    private static string ExportPng(Document doc, ElementId id, string directory, string name, int pixels)
    {
        var before = Directory.GetFiles(directory, "*.png").ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var options = new ImageExportOptions { ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(directory, SafeName(name)),
            HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG, ZoomType = ZoomFitType.FitToPage,
            PixelSize = pixels, ImageResolution = ImageResolution.DPI_150 };
        options.SetViewsAndSheets(new List<ElementId> { id });
        doc.ExportImage(options);
        var files = Directory.GetFiles(directory, "*.png").Where(p => !before.Contains(p)).ToArray();
        if (files.Length != 1 || new FileInfo(files[0]).Length == 0) throw new RequestDispatchException("png_export_failed", "Revit did not produce exactly one nonempty PNG for view " + id.Value + ".");
        return files[0];
    }
    private static string ContactSheet(Rendered[] items, string path)
    {
        const int width = 1000, height = 800;
        var columns = items.Length == 1 ? 1 : 2; var rows = (items.Length + columns - 1) / columns;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, columns * width, rows * height));
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i]; var left = i % columns * width; var top = i / columns * height;
                using var stream = File.OpenRead(item.Path);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var scale = Math.Min((width - 40.0) / image.PixelWidth, (height - 110.0) / image.PixelHeight);
                dc.DrawImage(image, new Rect(left + (width - image.PixelWidth * scale) / 2, top + 60 + (height - 110 - image.PixelHeight * scale) / 2, image.PixelWidth * scale, image.PixelHeight * scale));
                DrawText(dc, item.Label, left + 20, top + 16, 24);
                DrawText(dc, "View " + item.ViewId + " · frame " + (item.Width * 0.3048).ToString("0.00", CultureInfo.InvariantCulture) + " × " + (item.Height * 0.3048).ToString("0.00", CultureInfo.InvariantCulture) + " m · fit to tile", left + 20, top + height - 38, 16);
                dc.DrawRectangle(null, new Pen(Brushes.LightGray, 1), new Rect(left, top, width, height));
            }
        }
        var bitmap = new RenderTargetBitmap(columns * width, rows * height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
        return path;
    }
    private static void DrawText(DrawingContext dc, string text, double x, double y, double size) => dc.DrawText(
        new System.Windows.Media.FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, 1) { MaxTextWidth = 960, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, new WPoint(x, y));
    private static string NewDirectory(JsonElement args, string prefix)
    {
        var root = Text(args, "output_directory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcp", "captures"));
        var directory = Path.Combine(Path.GetFullPath(root), prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory); return directory;
    }
    private static string SafeName(string value) => string.Concat(Path.GetFileNameWithoutExtension(value).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string Text(JsonElement args, string key, string fallback) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
    private static double Number(JsonElement args, string key, double fallback) => args.TryGetProperty(key, out var v) && v.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
    private static bool Bool(JsonElement args, string key, bool fallback) => args.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
    private static long[] Longs(JsonElement args, string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetInt64()).ToArray() : [];
}
