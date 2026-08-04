using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private string? _lastAutoImportedProposedMainWorkOrderId;
    private bool _isAutoImportingProposedMain;
    private Geometry? _editableImportedProposedMainGeometry;
    private ProposedMainRestSymbol? _editableImportedProposedMainSymbol;
    private string? _editableImportedProposedMainWorkOrderId;
    private bool _isEditingImportedProposedMain;
    private int _lastProposedMainEndpointSnapCount;

    private async void ExtentPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!Equals(e.NewValue, true)) { return; }
        await Task.Yield();

        // The layer's symbol and its field list, which the hand drawn main needs and the imported one
        // does not. Fetched on arrival rather than at startup: it is two calls nobody needs until they
        // are on this page, and page 1 has the network to itself until then.
        await EnsureProposedMainLayerDetailsAsync();
        await AutoImportWorkOrderProposedMainIfReadyAsync(false);
    }

    private async Task AutoImportWorkOrderProposedMainIfReadyAsync(bool force)
    {
        if (_isAutoImportingProposedMain) { return; }
        if (WorkOrderModePanel == null || WorkOrderModePanel.Visibility != Visibility.Visible) { return; }
        var workOrderId = GetSelectedNgOdsWorkOrderNumberForProposedMain();
        if (string.IsNullOrWhiteSpace(workOrderId)) { return; }
        if (!force && string.Equals(_lastAutoImportedProposedMainWorkOrderId, workOrderId, StringComparison.OrdinalIgnoreCase)) { return; }
        try
        {
            _isAutoImportingProposedMain = true;
            await ImportProposedMainByWorkOrderAsync(workOrderId, true);
            _lastAutoImportedProposedMainWorkOrderId = workOrderId;
        }
        finally { _isAutoImportingProposedMain = false; }
    }

    private async void RefreshWorkOrderGeometry_Click(object sender, RoutedEventArgs e)
    {
        var workOrderId = GetSelectedNgOdsWorkOrderNumberForProposedMain();
        if (string.IsNullOrWhiteSpace(workOrderId))
        {
            WorkOrderGeometryTextBox.Text = "Select a Work Order Number first.";
            return;
        }
        await ImportProposedMainByWorkOrderAsync(workOrderId, false);
        _lastAutoImportedProposedMainWorkOrderId = workOrderId;
    }

    private async Task ImportProposedMainByWorkOrderAsync(string workOrderId, bool automatic)
    {
        try
        {
            var token = GetCurrentArcGisAccessTokenForProposedMain();
            if (string.IsNullOrWhiteSpace(token))
            {
                WorkOrderGeometryTextBox.Text = "No current ArcGIS access token was available. Run NGGIS sign-in once, then retry work order geometry import.";
                return;
            }
            SetNgOdsStatus((automatic ? "Auto-importing" : "Querying") + " proposed mains GIS for workorderid " + workOrderId + "...");
            var rawResult = await ProposedMainFeatureService.QueryByWorkOrderAsync(workOrderId, token);
            var snappedGeometries = SnapAmendedProposedMainGeometries(rawResult.Geometries).ToList();
            var snapCount = _lastProposedMainEndpointSnapCount;
            var result = new ProposedMainQueryResult
            {
                WorkOrderId = rawResult.WorkOrderId,
                Geometries = snappedGeometries,
                Extent = BuildEnvelopeFromGeometries(snappedGeometries),
                Symbol = rawResult.Symbol
            };
            if (result.FeatureCount == 0 || result.Extent == null)
            {
                WorkOrderGeometryTextBox.Text = "No proposed main geometry found in GIS layer 54 for workorderid " + workOrderId + ".";
                SetNgOdsStatus("No proposed main geometry found for workorderid " + workOrderId + ".");
                return;
            }
            DrawWorkOrderProposedMainResult(result);
            var paddingFeet = GetSharedPaddingFeetForWorkOrderImport();
            var unionGeometry = result.Geometries.Count == 1 ? result.Geometries[0] : GeometryEngine.Union(result.Geometries);
            _editableImportedProposedMainGeometry = unionGeometry;
            _editableImportedProposedMainSymbol = result.Symbol;
            _editableImportedProposedMainWorkOrderId = result.WorkOrderId;
            var buffered = paddingFeet > 0 && unionGeometry != null && !unionGeometry.IsEmpty ? GeometryEngine.Buffer(unionGeometry, paddingFeet * 0.3048) : unionGeometry;
            var effectiveExtent = buffered?.Extent ?? result.Extent;
            var paddedZoomEnvelope = ExpandEnvelopeByFeet(effectiveExtent, 200);
            if (_mapView != null) { await _mapView.SetViewpointGeometryAsync(paddedZoomEnvelope, 20); }
            CommitExtentToViewModelForWorkOrderImport(effectiveExtent);
            WriteWorkOrderGeometryProof(result, effectiveExtent, paddingFeet, automatic);
            SetNgOdsStatus((automatic ? "Auto-imported" : "Imported") + " proposed main geometry for workorderid " + workOrderId + " from GIS layer 54. Endpoint snaps applied: " + snapCount + ".");
        }
        catch (Exception ex)
        {
            var message = FlattenProposedMainException(ex);
            WorkOrderGeometryTextBox.Text = message;
            SetNgOdsStatus("Work order proposed main import failed: " + message);
        }
    }

    private string GetSelectedNgOdsWorkOrderNumberForProposedMain()
    {
        var selected = WorkOrderSelectionComboBox?.SelectedItem;
        if (selected != null)
        {
            var prop = selected.GetType().GetProperty("WorkOrderNumber", BindingFlags.Public | BindingFlags.Instance);
            var value = prop?.GetValue(selected)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) { return value.Trim(); }
        }
        return WorkOrderSelectionComboBox?.Text?.Trim() ?? string.Empty;
    }

    private string GetCurrentArcGisAccessTokenForProposedMain()
    {
        var assembly = typeof(ExporterWindow).Assembly;
        var authType = assembly.GetType("NG.GIS.CAD.Exporter.Auth.ArcGisPortalOAuth");
        if (authType == null) { return string.Empty; }
        var prop = authType.GetProperty("CurrentAccessToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var propValue = prop?.GetValue(null)?.ToString();
        if (!string.IsNullOrWhiteSpace(propValue)) { return propValue; }
        var field = authType.GetField("CurrentAccessToken", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var fieldValue = field?.GetValue(null)?.ToString();
        return fieldValue ?? string.Empty;
    }

    private void DrawWorkOrderProposedMainResult(ProposedMainQueryResult result)
    {
        DrawProposedMainGeometries(result.Geometries, result.Symbol);
    }

    private void DrawProposedMainGeometries(IReadOnlyList<Geometry> geometries, ProposedMainRestSymbol? symbol)
    {
        if (_workOrderOverlay == null) { return; }
        _workOrderOverlay.Graphics.Clear();
        var s = symbol ?? new ProposedMainRestSymbol();
        var lineColor = System.Drawing.Color.FromArgb(ClampByte(s.A), ClampByte(s.R), ClampByte(s.G), ClampByte(s.B));
        var lineStyle = ConvertRestLineStyleToRuntimeStyle(s.Style);
        var lineSymbol = new SimpleLineSymbol(lineStyle, lineColor, Math.Max(2.0, s.Width));
        foreach (var geometry in geometries) { _workOrderOverlay.Graphics.Add(new Graphic(geometry, lineSymbol)); }
        var paddingFeet = GetSharedPaddingFeetForWorkOrderImport();
        if (paddingFeet > 0 && geometries.Count > 0)
        {
            var unionGeometry = geometries.Count == 1 ? geometries[0] : GeometryEngine.Union(geometries);
            if (unionGeometry != null && !unionGeometry.IsEmpty)
            {
                var buffer = GeometryEngine.Buffer(unionGeometry, paddingFeet * 0.3048);
                RememberProposedMainBuffer(buffer);
                var bufferSymbol = new SimpleFillSymbol(SimpleFillSymbolStyle.Solid, System.Drawing.Color.FromArgb(45, ClampByte(s.R), ClampByte(s.G), ClampByte(s.B)), new SimpleLineSymbol(lineStyle, lineColor, 2));
                _workOrderOverlay.Graphics.Add(new Graphic(buffer, bufferSymbol));
            }
        }
    }

    /// <summary>
    /// Hands the padding buffer to the view model, so the export can draw the same shape rather than a
    /// recomputed one. Cleared when there is no buffer, so an export after the padding is set to zero
    /// does not still write the old boundary.
    /// </summary>
    private void RememberProposedMainBuffer(Geometry? buffer)
    {
        if (DataContext is ViewModels.ExporterViewModel vm)
        {
            vm.ProposedMainBufferOutline = buffer != null && !buffer.IsEmpty ? buffer : null;
        }
    }

    private static int ClampByte(int value)
    {
        if (value < 0) { return 0; }
        if (value > 255) { return 255; }
        return value;
    }

    private static SimpleLineSymbolStyle ConvertRestLineStyleToRuntimeStyle(string? restStyle)
    {
        if (string.IsNullOrWhiteSpace(restStyle)) { return SimpleLineSymbolStyle.Solid; }
        var normalized = restStyle.Trim();
        if (normalized.StartsWith("esriSLS", StringComparison.OrdinalIgnoreCase)) { normalized = normalized.Substring("esriSLS".Length); }
        normalized = normalized.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        return Enum.TryParse<SimpleLineSymbolStyle>(normalized, true, out var parsed) ? parsed : SimpleLineSymbolStyle.Solid;
    }

    private double GetSharedPaddingFeetForWorkOrderImport()
    {
        if (WorkOrderPaddingTextBox != null && double.TryParse(WorkOrderPaddingTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) { return Math.Max(0, parsed); }
        if (WorkOrderPaddingTextBox != null && double.TryParse(WorkOrderPaddingTextBox.Text, out parsed)) { return Math.Max(0, parsed); }
        return 0;
    }

    private void CommitExtentToViewModelForWorkOrderImport(Envelope extent)
    {
        var vm = DataContext;
        if (vm == null || extent == null) { return; }
        var method = vm.GetType().GetMethod("SetExtentFromMap", BindingFlags.Public | BindingFlags.Instance);
        method?.Invoke(vm, new object[] { extent.XMin, extent.YMin, extent.XMax, extent.YMax, 3857 });
    }

    private void WriteWorkOrderGeometryProof(ProposedMainQueryResult result, Envelope extent, double paddingFeet, bool automatic)
    {
        var proof = new
        {
            source = "MA/Material_View_MA/MapServer/54",
            layer = "Pipeline Line (P)",
            matchField = "workorderid",
            selectedWorkOrder = result.WorkOrderId,
            importMode = automatic ? "automatic on Page 2 open" : "manual refresh button",
            featureCount = result.FeatureCount,
            endpointSnapsApplied = _lastProposedMainEndpointSnapCount,
            restRendererColor = new[] { result.Symbol.R, result.Symbol.G, result.Symbol.B, result.Symbol.A },
            restRendererWidth = result.Symbol.Width,
            restRendererStyle = result.Symbol.Style,
            paddingFeet,
            wkid = 3857,
            xmin = Math.Round(extent.XMin, 3),
            ymin = Math.Round(extent.YMin, 3),
            xmax = Math.Round(extent.XMax, 3),
            ymax = Math.Round(extent.YMax, 3),
            viewportExtraOffsetFeet = 200,
            capturedLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        WorkOrderGeometryTextBox.Text = JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Applies the one foot endpoint snap and records how many endpoints moved, so the proof text and
    /// the status line can report it.
    ///
    /// Every path that amends the proposed main goes through here, not only the initial import: an
    /// edit in the geometry editor, or a manually drawn segment, leaves exactly the same near miss
    /// gaps at the joins that the import is snapped to close.
    /// </summary>
    private IReadOnlyList<Geometry> SnapAmendedProposedMainGeometries(IReadOnlyList<Geometry> geometries)
    {
        // The distance and whether to snap at all are now the user's, set on page 2. Both were fixed
        // here at one foot with nothing on screen to say so: a foot suits a street but not a plant
        // yard where mains run inches apart, nor a sketch drawn zoomed out where ends land further
        // apart than that and were quietly left unjoined.
        var snapping = true;
        var toleranceFeet = 1.0;
        if (DataContext is ViewModels.ExporterViewModel vm)
        {
            snapping = vm.SnapProposedMainSegmentEnds;
            toleranceFeet = vm.ProposedMainSnapToleranceFeet;
        }

        if (!snapping)
        {
            _lastProposedMainEndpointSnapCount = 0;
            return geometries;
        }

        var snapped = SnapGeometryEndpoints(geometries, toleranceFeet, out var snapCount);
        _lastProposedMainEndpointSnapCount = snapCount;
        return snapped;
    }

    private static IReadOnlyList<Geometry> SnapGeometryEndpoints(
        IReadOnlyList<Geometry> geometries, double toleranceFeet, out int snapCount)
    {
        snapCount = 0;
        if (geometries == null || geometries.Count == 0) { return Array.Empty<Geometry>(); }
        var parsed = new List<EditablePolylineGeometry>();
        for (var i = 0; i < geometries.Count; i++)
        {
            var geometry = geometries[i];
            if (geometry == null || geometry.IsEmpty) { continue; }
            var editable = EditablePolylineGeometry.FromGeometry(i, geometry);
            if (editable.Paths.Count > 0) { parsed.Add(editable); }
        }
        if (parsed.Count == 0) { return geometries; }
        // Web Mercator metres, which is what page 2 works in. The units stretch away from the equator,
        // so a foot of tolerance here is a little under a foot on the ground; over the distance a snap
        // covers that is far below anything a hand drawn end is accurate to.
        var toleranceMetres = Math.Max(0.0, toleranceFeet) * 0.3048;
        var toleranceSquared = toleranceMetres * toleranceMetres;

        // Every move is worked out against the geometry as it arrived and applied afterwards. Writing
        // each one as it was found made the result depend on which path happened to be walked first,
        // and let one snap feed the next.
        var moves = new List<PendingSnap>();

        // Endpoints something else is already moving to. They stay put: if two ends are each other's
        // nearest and both move, they swap places and the lines cross.
        var pinned = new HashSet<(EditablePath Path, int Index)>();

        foreach (var source in parsed)
        {
            foreach (var path in source.Paths)
            {
                if (path.Points.Count < 2) { continue; }
                TryPlanEndpointSnap(path, 0, parsed, toleranceSquared, moves, pinned);
                TryPlanEndpointSnap(path, path.Points.Count - 1, parsed, toleranceSquared, moves, pinned);
            }
        }

        foreach (var move in moves) { move.Path.Points[move.Index] = move.Target; }
        snapCount = moves.Count;
        if (snapCount == 0) { return geometries; }

        // Rebuilt by source index, and falling back to the original for anything that did not parse
        // as a polyline, so a geometry this cannot snap is carried through rather than dropped.
        var rebuiltByIndex = parsed.ToDictionary(x => x.SourceIndex, x => x.ToGeometry());
        var snapped = new List<Geometry>();
        for (var i = 0; i < geometries.Count; i++)
        {
            if (rebuiltByIndex.TryGetValue(i, out var rebuilt) && rebuilt != null && !rebuilt.IsEmpty)
            {
                snapped.Add(rebuilt);
                continue;
            }
            if (geometries[i] != null && !geometries[i].IsEmpty) { snapped.Add(geometries[i]); }
        }
        return snapped;
    }

    private readonly record struct PendingSnap(EditablePath Path, int Index, EditablePoint Target);

    /// <summary>
    /// Works out where one endpoint should move to, if anywhere, without moving it.
    ///
    /// A vertex is looked for before an edge. Two mains that should join do so end to end, and the other
    /// main's vertex is where it actually stops; the nearest point on its edge can be a long way along
    /// it, so snapping there closes the gap by overlapping the two lines rather than meeting them.
    /// </summary>
    private static void TryPlanEndpointSnap(
        EditablePath sourcePath, int endpointIndex, List<EditablePolylineGeometry> allGeometries,
        double toleranceSquared, List<PendingSnap> moves, HashSet<(EditablePath, int)> pinned)
    {
        if (pinned.Contains((sourcePath, endpointIndex))) { return; }

        var endpoint = sourcePath.Points[endpointIndex];
        EditablePoint target;

        if (TryFindNearestVertex(sourcePath, endpointIndex, allGeometries, toleranceSquared, out target, out var targetOwner))
        {
            // Whatever this end is going to meet stays where it is, so the two cannot trade places.
            pinned.Add(targetOwner);
        }
        else if (!TryFindNearestEdgePoint(sourcePath, endpointIndex, allGeometries, toleranceSquared, out target))
        {
            return;
        }

        if (DistanceSquared(endpoint, target) <= 0.000000000001) { return; }
        if (WouldDoubleBackOnItself(sourcePath, endpointIndex, target)) { return; }

        pinned.Add((sourcePath, endpointIndex));
        moves.Add(new PendingSnap(sourcePath, endpointIndex, target));
    }

    /// <summary>
    /// The nearest vertex of another path. Never anything on this path.
    ///
    /// Its own far end is excluded too. A main whose two ends pass within a foot of each other is a
    /// U-turn far more often than it is a loop, and joining them turns an open main into a closed one,
    /// which changes what it says. A main that really is a loop should be drawn closed.
    /// </summary>
    private static bool TryFindNearestVertex(
        EditablePath sourcePath, int endpointIndex, List<EditablePolylineGeometry> allGeometries,
        double toleranceSquared, out EditablePoint target, out (EditablePath, int) owner)
    {
        var endpoint = sourcePath.Points[endpointIndex];
        var bestDistanceSquared = toleranceSquared;
        var found = false;
        target = endpoint;
        owner = (sourcePath, endpointIndex);

        foreach (var candidateGeometry in allGeometries)
        {
            foreach (var candidatePath in candidateGeometry.Paths)
            {
                if (candidatePath.Points.Count < 2) { continue; }
                if (ReferenceEquals(candidatePath, sourcePath)) { continue; }

                for (var i = 0; i < candidatePath.Points.Count; i++)
                {
                    var distanceSquared = DistanceSquared(endpoint, candidatePath.Points[i]);
                    if (distanceSquared > bestDistanceSquared) { continue; }

                    bestDistanceSquared = distanceSquared;
                    target = candidatePath.Points[i];
                    owner = (candidatePath, i);
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The nearest point on another path's edge, for an end that stops partway along another main
    /// rather than at its end. Never this path's own edges.
    ///
    /// That exclusion is the fix for the zigzag: the old code excluded only the one segment touching the
    /// endpoint, so on a main that turned back near itself the end could snap onto a later segment of
    /// the same line and be pulled past its own neighbour, doubling the line back over itself.
    /// </summary>
    private static bool TryFindNearestEdgePoint(
        EditablePath sourcePath, int endpointIndex, List<EditablePolylineGeometry> allGeometries,
        double toleranceSquared, out EditablePoint target)
    {
        var endpoint = sourcePath.Points[endpointIndex];
        var bestDistanceSquared = toleranceSquared;
        var found = false;
        target = endpoint;

        foreach (var candidateGeometry in allGeometries)
        {
            foreach (var candidatePath in candidateGeometry.Paths)
            {
                if (candidatePath.Points.Count < 2) { continue; }
                if (ReferenceEquals(candidatePath, sourcePath)) { continue; }

                for (var i = 0; i < candidatePath.Points.Count - 1; i++)
                {
                    var projected = ClosestPointOnSegment(endpoint, candidatePath.Points[i], candidatePath.Points[i + 1]);
                    var distanceSquared = DistanceSquared(endpoint, projected);
                    if (distanceSquared > bestDistanceSquared) { continue; }

                    bestDistanceSquared = distanceSquared;
                    target = projected;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether moving the end to <paramref name="target"/> would turn its own first segment around.
    ///
    /// That means the end has been pulled past the vertex next to it, which is exactly what draws as a
    /// zigzag. A backstop as much as a rule: with the source path's own edges excluded above this
    /// should not arise, but a snap that reverses a segment is never what was wanted.
    /// </summary>
    private static bool WouldDoubleBackOnItself(EditablePath path, int endpointIndex, EditablePoint target)
    {
        var neighbourIndex = endpointIndex == 0 ? 1 : endpointIndex - 1;
        var endpoint = path.Points[endpointIndex];
        var neighbour = path.Points[neighbourIndex];

        var beforeX = neighbour.X - endpoint.X;
        var beforeY = neighbour.Y - endpoint.Y;
        var afterX = neighbour.X - target.X;
        var afterY = neighbour.Y - target.Y;

        return beforeX * afterX + beforeY * afterY <= 0;
    }

    private static EditablePoint ClosestPointOnSegment(EditablePoint point, EditablePoint start, EditablePoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.0) { return start; }
        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        if (t < 0.0) { t = 0.0; }
        if (t > 1.0) { t = 1.0; }
        return new EditablePoint(start.X + t * dx, start.Y + t * dy);
    }

    private static double DistanceSquared(EditablePoint a, EditablePoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static Envelope? BuildEnvelopeFromGeometries(IReadOnlyList<Geometry> geometries)
    {
        Envelope? extent = null;
        foreach (var geometry in geometries)
        {
            if (geometry == null || geometry.IsEmpty) { continue; }
            if (extent == null) { extent = geometry.Extent; }
            else
            {
                extent = new Envelope(Math.Min(extent.XMin, geometry.Extent.XMin), Math.Min(extent.YMin, geometry.Extent.YMin), Math.Max(extent.XMax, geometry.Extent.XMax), Math.Max(extent.YMax, geometry.Extent.YMax), SpatialReferences.WebMercator);
            }
        }
        return extent;
    }

    private sealed class EditablePolylineGeometry
    {
        public int SourceIndex { get; }
        public List<EditablePath> Paths { get; } = new List<EditablePath>();

        /// <summary>
        /// The spatial reference exactly as the source geometry wrote it. Imported mains arrive in Web
        /// Mercator, but a manually drawn segment carries the map's spatial reference, and writing a
        /// fixed wkid back would relabel those coordinates rather than convert them.
        /// </summary>
        private string SpatialReferenceJson { get; set; } = "{\"wkid\":3857}";

        private EditablePolylineGeometry(int sourceIndex) { SourceIndex = sourceIndex; }
        public static EditablePolylineGeometry FromGeometry(int sourceIndex, Geometry geometry)
        {
            var editable = new EditablePolylineGeometry(sourceIndex);
            using var doc = JsonDocument.Parse(geometry.ToJson());
            if (doc.RootElement.TryGetProperty("spatialReference", out var spatialReference))
            {
                editable.SpatialReferenceJson = spatialReference.GetRawText();
            }
            if (!doc.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array) { return editable; }
            foreach (var pathElement in paths.EnumerateArray())
            {
                if (pathElement.ValueKind != JsonValueKind.Array) { continue; }
                var path = new EditablePath();
                foreach (var pointElement in pathElement.EnumerateArray())
                {
                    if (pointElement.ValueKind != JsonValueKind.Array) { continue; }
                    var values = pointElement.EnumerateArray().ToArray();
                    if (values.Length < 2) { continue; }
                    if (values[0].TryGetDouble(out var x) && values[1].TryGetDouble(out var y)) { path.Points.Add(new EditablePoint(x, y)); }
                }
                if (path.Points.Count >= 2) { editable.Paths.Add(path); }
            }
            return editable;
        }
        public Geometry? ToGeometry()
        {
            var sb = new StringBuilder();
            sb.Append("{\"paths\":[");
            for (var p = 0; p < Paths.Count; p++)
            {
                if (p > 0) { sb.Append(','); }
                sb.Append('[');
                for (var i = 0; i < Paths[p].Points.Count; i++)
                {
                    if (i > 0) { sb.Append(','); }
                    sb.Append('[');
                    sb.Append(Paths[p].Points[i].X.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(',');
                    sb.Append(Paths[p].Points[i].Y.ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(']');
                }
                sb.Append(']');
            }
            sb.Append("],\"spatialReference\":");
            sb.Append(SpatialReferenceJson);
            sb.Append('}');
            return Geometry.FromJson(sb.ToString());
        }
    }

    private sealed class EditablePath
    {
        public List<EditablePoint> Points { get; } = new List<EditablePoint>();
    }

    private readonly struct EditablePoint
    {
        public double X { get; }
        public double Y { get; }
        public EditablePoint(double x, double y) { X = x; Y = y; }
    }

    private static string FlattenProposedMainException(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException) { messages.Add(current.GetType().Name + ": " + current.Message); }
        return string.Join(" -> ", messages);
    }
}
