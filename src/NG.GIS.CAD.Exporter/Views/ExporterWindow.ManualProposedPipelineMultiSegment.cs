using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Esri.ArcGISRuntime.Geometry;
using RuntimeGeometry = Esri.ArcGISRuntime.Geometry.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Esri.ArcGISRuntime.UI.Editing;
namespace NG.GIS.CAD.Exporter.Views;
public partial class ExporterWindow
{
    private readonly List<RuntimeGeometry> _manualProposedPipelineSegmentGeometries = new();
    private GraphicsOverlay? _manualProposedPipelineSegmentOverlay;
    private SimpleLineSymbol? _manualProposedPipelineSegmentSymbol;
    private SimpleLineSymbol? _manualProposedPipelineSelectedSegmentSymbol;
    private int _manualProposedPipelineEndpointSnapCount;

    /// <summary>Waiting for the user to click the segment they mean. Set by Edit Segment.</summary>
    private bool _pickingManualProposedPipelineSegment;

    /// <summary>
    /// The segment being edited, or -1 when a new one is being drawn.
    ///
    /// This is what tells Apply Segment whether it is replacing something or adding something, which is
    /// the whole difference between editing a segment and drawing another one on top of it.
    /// </summary>
    private int _editingManualProposedPipelineSegmentIndex = -1;

    /// <summary>
    /// The segment picked in the attribute table, or -1 for none.
    ///
    /// Kept apart from the editing index because they mean different things. Editing opens a segment in
    /// the geometry editor and makes Delete remove it; picking its row in the table is only saying
    /// "this one", and should not arm either.
    /// </summary>
    private int _highlightedManualProposedPipelineSegmentIndex = -1;

    /// <summary>Which segment to draw as picked out: the one being edited, or the row that is selected.</summary>
    private int HighlightedManualProposedPipelineSegmentIndex =>
        _editingManualProposedPipelineSegmentIndex >= 0
            ? _editingManualProposedPipelineSegmentIndex
            : _highlightedManualProposedPipelineSegmentIndex;

    /// <summary>
    /// Draws one segment as picked out because its row was selected in the attribute table, so a row
    /// and a line on the map can be told to be the same thing.
    /// </summary>
    private void HighlightManualProposedPipelineSegment(int index)
    {
        var clamped = index >= 0 && index < _manualProposedPipelineSegmentGeometries.Count ? index : -1;
        if (clamped == _highlightedManualProposedPipelineSegmentIndex) { return; }

        _highlightedManualProposedPipelineSegmentIndex = clamped;
        RefreshManualProposedPipelineSegmentOverlay();
    }

    private void StartManualProposedMainSegment_Click(object sender, RoutedEventArgs e)
    {
        // A new segment, so anything that was being edited is no longer what Apply Segment means.
        _pickingManualProposedPipelineSegment = false;
        _editingManualProposedPipelineSegmentIndex = -1;

        StartManualProposedPipelineSegmentDrawing();
        RefreshManualProposedPipelineSegmentOverlay();
        SetManualProposedPipelineStatus("Manual proposed pipeline segment drawing started. Draw one segment, then use Apply Segment to add another.");
    }

    /// <summary>
    /// Picks a drawn segment to change, rather than clearing them all.
    ///
    /// Selection is by clicking the segment on the map, because the segments have no names and a list of
    /// "segment 3" would leave the user counting along the line to find out which that is.
    /// </summary>
    private void EditManualProposedMainSegment_Click(object sender, RoutedEventArgs e)
    {
        if (_manualProposedPipelineSegmentGeometries.Count == 0)
        {
            SetManualProposedPipelineStatus("There are no drawn segments to edit yet. Use Start Segment to draw one.");
            return;
        }

        // Any half drawn segment is dropped rather than stored. The user asked to edit an existing one,
        // so keeping what they had started would add a segment they did not ask for.
        TryStopGeometryEditor();

        _pickingManualProposedPipelineSegment = true;
        _editingManualProposedPipelineSegmentIndex = -1;
        RefreshManualProposedPipelineSegmentOverlay();
        SetManualProposedPipelineStatus("Click the segment on the map you want to change. The nearest one to "
            + "where you click is picked, and once it is picked you can drag its points and use Apply "
            + "Segment, or press Delete to remove it.");
    }

    private void ApplyManualProposedMainSegment_Click(object sender, RoutedEventArgs e)
    {
        var editedIndex = _editingManualProposedPipelineSegmentIndex;
        var added = StopAndStoreCurrentManualProposedPipelineSegment();
        RefreshManualProposedPipelineSegmentOverlay();
        UpdateManualProposedPipelineSegmentSummary();

        if (editedIndex >= 0)
        {
            // Editing one segment is finished by applying it. Starting the editor again here would put
            // the user back into drawing a new segment, which is not what they asked for.
            SetManualProposedPipelineStatus(added
                ? $"Segment {editedIndex + 1} updated. Endpoint snaps applied: {_manualProposedPipelineEndpointSnapCount}. Use Edit Segment to change another."
                : $"Segment {editedIndex + 1} was left as it was, because no edited geometry came back.");
            return;
        }

        StartManualProposedPipelineSegmentDrawing();
        SetManualProposedPipelineStatus(added
            ? $"Manual proposed pipeline segment added. Endpoint snaps applied: {_manualProposedPipelineEndpointSnapCount}. Continue drawing the next segment or use Finish Segments."
            : "No segment geometry was captured. Continue drawing or use Finish Segments.");
    }

    /// <summary>
    /// Selects the drawn segment nearest to where the user clicked, and opens it for editing.
    ///
    /// Nearest wins outright rather than being held to a tolerance. The user only gets here by asking to
    /// edit and then clicking, so they meant to pick something, and refusing a click that was a few
    /// pixels wide of the line would read as the map ignoring them.
    /// </summary>
    private void SelectManualProposedPipelineSegmentAt(MapPoint tapped)
    {
        var nearestIndex = -1;
        var nearestDistance = double.MaxValue;

        for (var i = 0; i < _manualProposedPipelineSegmentGeometries.Count; i++)
        {
            try
            {
                var distance = GeometryEngine.Distance(_manualProposedPipelineSegmentGeometries[i], tapped);
                if (distance < nearestDistance) { nearestDistance = distance; nearestIndex = i; }
            }
            catch
            {
                // A segment that cannot be measured against cannot be the one meant. The rest still can.
            }
        }

        if (nearestIndex < 0)
        {
            SetManualProposedPipelineStatus("No drawn segment could be measured against that click. Try clicking closer to a line.");
            return;
        }

        _pickingManualProposedPipelineSegment = false;
        _editingManualProposedPipelineSegmentIndex = nearestIndex;
        RefreshManualProposedPipelineSegmentOverlay();

        var mapView = GetExporterMapView();
        if (mapView?.GeometryEditor != null)
        {
            TryStopGeometryEditor();
            mapView.GeometryEditor.Start(_manualProposedPipelineSegmentGeometries[nearestIndex]);
        }

        SetManualProposedPipelineStatus($"Segment {nearestIndex + 1} of "
            + $"{_manualProposedPipelineSegmentGeometries.Count} selected. Drag its points to change it and "
            + "use Apply Segment, or press Delete to remove it.");
    }

    /// <summary>
    /// Removes the segment currently being edited.
    ///
    /// The buffer and the extent are rebuilt from what is left, so deleting the segment that reached
    /// furthest actually shrinks the exported area rather than leaving it scoped to a line that is gone.
    /// </summary>
    private void DeleteSelectedManualProposedPipelineSegment()
    {
        var index = _editingManualProposedPipelineSegmentIndex;
        if (index < 0 || index >= _manualProposedPipelineSegmentGeometries.Count) { return; }

        TryStopGeometryEditor();
        _manualProposedPipelineSegmentGeometries.RemoveAt(index);
        _editingManualProposedPipelineSegmentIndex = -1;
        _pickingManualProposedPipelineSegment = false;

        // The snap count described a set that no longer exists, so it is worked out again over what is
        // left rather than left standing as a number about something else.
        ApplyEndpointSnapsToManualProposedPipelineSegments();
        RefreshManualProposedPipelineSegmentOverlay();
        UpdateManualProposedPipelineSegmentSummary();

        SetManualProposedPipelineStatus(_manualProposedPipelineSegmentGeometries.Count == 0
            ? "Segment deleted. No manual proposed pipeline segments are left."
            : $"Segment {index + 1} deleted. {_manualProposedPipelineSegmentGeometries.Count} left. "
              + "Use Edit Segment to change another.");
    }
    private void FinishManualProposedMainSegments_Click(object sender, RoutedEventArgs e)
    {
        StopAndStoreCurrentManualProposedPipelineSegment();
        RefreshManualProposedPipelineSegmentOverlay();
        UpdateManualProposedPipelineSegmentSummary();
        var segmentSummary = _manualProposedPipelineSegmentGeometries.Count == 1
            ? "Manual proposed pipeline finished with 1 segment."
            : $"Manual proposed pipeline finished with {_manualProposedPipelineSegmentGeometries.Count} segments.";
        SetManualProposedPipelineStatus($"{segmentSummary} Endpoint snaps applied: {_manualProposedPipelineEndpointSnapCount}.");
    }
    private void ClearManualProposedMainSegments_Click(object sender, RoutedEventArgs e)
    {
        TryStopGeometryEditor();
        _manualProposedPipelineSegmentGeometries.Clear();
        _manualProposedPipelineEndpointSnapCount = 0;
        _editingManualProposedPipelineSegmentIndex = -1;
        _pickingManualProposedPipelineSegment = false;
        // Dropped outright rather than left to the refresh, which gives up early when there is no map
        // view to draw on. Clearing the segments has to clear the buffer whatever the map is doing, or
        // the export stays scoped to a boundary around a main that is no longer drawn.
        RememberProposedMainBuffer(null);
        RefreshManualProposedPipelineSegmentOverlay();
        UpdateManualProposedPipelineSegmentSummary();
        SetManualProposedPipelineStatus("Manual proposed pipeline segments cleared.");
    }
    /// <summary>
    /// Turns a click on the map into a segment selection, but only while one is being asked for.
    ///
    /// Handled rather than swallowed: the tap is marked handled only when it was used, so panning and
    /// every other use of the map is untouched the rest of the time.
    /// </summary>
    private void ExporterMapView_GeoViewTapped(object? sender, GeoViewInputEventArgs e)
    {
        // The palette gets the click first, because arming a symbol is a deliberate act and the click
        // that follows it is meant for the thing that was armed.
        if (TryPlacePaletteSymbolAt(e.Location))
        {
            e.Handled = true;
            return;
        }

        if (!_pickingManualProposedPipelineSegment) { return; }
        if (e.Location == null) { return; }

        e.Handled = true;
        SelectManualProposedPipelineSegmentAt(e.Location);
    }

    /// <summary>
    /// Deletes the selected segment on the Delete key.
    ///
    /// Ignored while a text box has focus, because Delete there means deleting a character and taking a
    /// segment away instead would be startling and hard to undo.
    /// </summary>
    private void ExporterWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) { return; }
        if (Keyboard.FocusedElement is TextBox or ComboBox) { return; }

        // A vertex is a smaller thing than the line it sits on, so Delete takes whichever is selected
        // and prefers the smaller. Picking a point to fix and losing the whole segment for it is the
        // kind of mistake that costs the drawing rather than the point.
        if (TryDeleteSelectedProposedMainVertex())
        {
            e.Handled = true;
            return;
        }

        if (_editingManualProposedPipelineSegmentIndex < 0) { return; }

        e.Handled = true;
        DeleteSelectedManualProposedPipelineSegment();
    }

    /// <summary>
    /// Removes the vertex the geometry editor has selected, and says whether it did.
    ///
    /// False for everything that is not a selected vertex: no editor, nothing being edited, or a
    /// selection that is the whole geometry rather than a point on it. Delete then falls through to
    /// the segment, which is what it did before.
    /// </summary>
    private bool TryDeleteSelectedProposedMainVertex()
    {
        var editor = GetExporterMapView()?.GeometryEditor;
        if (editor == null || !editor.IsStarted) { return false; }

        // Mid vertices count. One is the point that appears halfway along a segment to be dragged out
        // into a real vertex, and pressing Delete on one is a way of saying "not that one".
        if (editor.SelectedElement is not (GeometryEditorVertex or GeometryEditorMidVertex)) { return false; }

        try
        {
            editor.DeleteSelectedElement();

            // The edit is not committed here. The segment is still open in the editor, so Apply
            // Segment is what puts it back into the list, the same as any other change made to it.
            SetManualProposedPipelineStatus("Vertex deleted. Use Apply Segment to keep the change, or "
                + "Delete again to remove another.");
            return true;
        }
        catch (Exception ex)
        {
            SetManualProposedPipelineStatus("That vertex could not be deleted: " + ex.Message);

            // Handled all the same. Falling through to erasing the whole segment because a vertex
            // refused to go is the worst of both.
            return true;
        }
    }

    private void StartManualProposedPipelineSegmentDrawing()
    {
        var mapView = GetExporterMapView();
        if (mapView == null)
        {
            MessageBox.Show("The map view could not be found, so manual proposed pipeline drawing could not start.", "NG GIS CAD Exporter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (mapView.GeometryEditor == null)
        {
            mapView.GeometryEditor = new GeometryEditor();
        }
        TryStopGeometryEditor();
        mapView.GeometryEditor.Start(GeometryType.Polyline);
    }
    private bool StopAndStoreCurrentManualProposedPipelineSegment()
    {
        var geometry = TryStopGeometryEditor();
        if (geometry == null || geometry.IsEmpty)
        {
            return false;
        }
        if (geometry.GeometryType != GeometryType.Polyline)
        {
            return false;
        }
        // Replacing when one was picked for editing, adding otherwise. Adding an edited segment would
        // leave the original in place underneath it, so the main would gain a duplicate every time
        // someone corrected a line.
        var editingIndex = _editingManualProposedPipelineSegmentIndex;
        if (editingIndex >= 0 && editingIndex < _manualProposedPipelineSegmentGeometries.Count)
        {
            _manualProposedPipelineSegmentGeometries[editingIndex] = geometry;
            _editingManualProposedPipelineSegmentIndex = -1;
        }
        else
        {
            _manualProposedPipelineSegmentGeometries.Add(geometry);
        }

        ApplyEndpointSnapsToManualProposedPipelineSegments();
        return true;
    }

    /// <summary>
    /// Pulls each segment endpoint onto a neighbouring segment when it lands within a foot of one.
    ///
    /// A hand drawn segment almost never ends exactly on the previous one, so the joins would carry a
    /// sub foot gap into CAD. The same snap the imported main gets runs over the whole set after each
    /// segment, which also closes gaps an earlier segment left once a later one arrives beside it.
    /// </summary>
    private void ApplyEndpointSnapsToManualProposedPipelineSegments()
    {
        if (_manualProposedPipelineSegmentGeometries.Count == 0)
        {
            _manualProposedPipelineEndpointSnapCount = 0;
            return;
        }

        var snapped = SnapAmendedProposedMainGeometries(_manualProposedPipelineSegmentGeometries).ToList();
        _manualProposedPipelineEndpointSnapCount = _lastProposedMainEndpointSnapCount;
        if (_manualProposedPipelineEndpointSnapCount == 0)
        {
            return;
        }

        _manualProposedPipelineSegmentGeometries.Clear();
        _manualProposedPipelineSegmentGeometries.AddRange(snapped);
    }
    private RuntimeGeometry? TryStopGeometryEditor()
    {
        var mapView = GetExporterMapView();
        var editor = mapView?.GeometryEditor;
        if (editor == null)
        {
            return null;
        }
        try
        {
            return editor.Stop();
        }
        catch
        {
            return null;
        }
    }
    private MapView? GetExporterMapView()
    {
        var type = GetType();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (typeof(MapView).IsAssignableFrom(field.FieldType) && field.GetValue(this) is MapView mapViewFromField)
            {
                return mapViewFromField;
            }
        }
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!typeof(MapView).IsAssignableFrom(property.PropertyType) || property.GetIndexParameters().Length != 0)
            {
                continue;
            }
            try
            {
                if (property.GetValue(this) is MapView mapViewFromProperty)
                {
                    return mapViewFromProperty;
                }
            }
            catch
            {
            }
        }
        return FindVisualChild<MapView>(this);
    }
    private void RefreshManualProposedPipelineSegmentOverlay()
    {
        var mapView = GetExporterMapView();
        if (mapView == null)
        {
            return;
        }
        if (_manualProposedPipelineSegmentOverlay == null)
        {
            _manualProposedPipelineSegmentOverlay = new GraphicsOverlay
            {
                Id = "ManualProposedPipelineSegments"
            };
            mapView.GraphicsOverlays.Add(_manualProposedPipelineSegmentOverlay);
        }
        // Rebuilt each time rather than cached, so the layer's own symbol replaces the fallback as
        // soon as the service answers instead of on the next restart.
        _manualProposedPipelineSegmentSymbol = BuildManualProposedPipelineSymbol();
        _manualProposedPipelineSegmentOverlay.Graphics.Clear();

        // The buffer first, so the segments draw over it rather than under.
        SyncManualProposedPipelineBufferAndExtent();

        // The selected segment is drawn wider and in a different colour, so "segment 3 of 7" in the
        // status line has something on the map to point at.
        _manualProposedPipelineSelectedSegmentSymbol ??=
            new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.Cyan, 7.0);

        for (var i = 0; i < _manualProposedPipelineSegmentGeometries.Count; i++)
        {
            var symbol = i == HighlightedManualProposedPipelineSegmentIndex
                ? _manualProposedPipelineSelectedSegmentSymbol
                : _manualProposedPipelineSegmentSymbol;
            _manualProposedPipelineSegmentOverlay.Graphics.Add(
                new Graphic(_manualProposedPipelineSegmentGeometries[i], symbol));
        }
    }

    /// <summary>
    /// Draws the padding buffer around the segments drawn so far, hands it to the view model, and commits
    /// the extent it covers.
    ///
    /// Without this a hand drawn pipeline showed no buffer and never set an extent at all, so the export
    /// refused to run and told the user to commit an extent on a page where nothing does. The work order
    /// import has always done these three things; drawing the segments by hand produces the same kind of
    /// proposed main and has to end in the same state.
    ///
    /// The extent is committed from the buffer where there is one, so it covers the corridor rather than
    /// the bare line. It is only ever the fallback now that the export is scoped by the buffer itself,
    /// but it is what the rest of the wizard reads to know an extent exists.
    /// </summary>
    private void SyncManualProposedPipelineBufferAndExtent()
    {
        var combined = BuildManualProposedPipelineMultipartGeometry();
        if (combined == null || combined.IsEmpty)
        {
            // Cleared, so the old buffer has to go with it. Leaving it would export a boundary around a
            // main that is no longer there.
            RememberProposedMainBuffer(null);
            return;
        }

        var paddingFeet = GetSharedPaddingFeetForWorkOrderImport();
        var buffer = paddingFeet > 0 ? GeometryEngine.Buffer(combined, paddingFeet * 0.3048) : null;
        RememberProposedMainBuffer(buffer);

        if (buffer != null && !buffer.IsEmpty && _manualProposedPipelineSegmentOverlay != null)
        {
            // The buffer takes the main's own colour rather than a fixed red. It is the padding around
            // that main and belongs to it, and a red halo around a blue line reads as two things.
            var line = BuildManualProposedPipelineSymbol();
            var colour = line.Color;
            var fill = new SimpleFillSymbol(
                SimpleFillSymbolStyle.Solid,
                System.Drawing.Color.FromArgb(45, colour.R, colour.G, colour.B),
                new SimpleLineSymbol(line.Style, colour, 2));
            _manualProposedPipelineSegmentOverlay.Graphics.Add(new Graphic(buffer, fill));
        }

        var extent = (buffer != null && !buffer.IsEmpty ? buffer.Extent : null) ?? combined.Extent;
        if (extent != null)
        {
            CommitExtentToViewModelForWorkOrderImport(extent);
        }
    }
    /// <summary>
    /// Every change to the drawn segments ends here, which is why the attribute table is brought level
    /// from this one place: a row belongs to a segment, so it should appear and go with it rather than
    /// when some other part of the page happens to refresh.
    /// </summary>
    private void UpdateManualProposedPipelineSegmentSummary()
    {
        var json = BuildManualProposedPipelineSegmentSummaryJson();
        var targetTextBox = FindManualProposedPipelineTextBox();
        if (targetTextBox != null)
        {
            targetTextBox.Text = json;
        }

        SyncProposedMainAttributeRows();
    }
    private string BuildManualProposedPipelineSegmentSummaryJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"source\": \"Manual proposed pipeline multi-segment GeometryEditor\",");
        sb.AppendLine("  \"geometryType\": \"Polyline\",");
        sb.AppendLine($"  \"segmentCount\": {_manualProposedPipelineSegmentGeometries.Count.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"  \"endpointSnapsApplied\": {_manualProposedPipelineEndpointSnapCount.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine("  \"segments\": [");
        for (var i = 0; i < _manualProposedPipelineSegmentGeometries.Count; i++)
        {
            var extent = _manualProposedPipelineSegmentGeometries[i].Extent;
            sb.AppendLine("    {");
            sb.AppendLine($"      \"index\": {(i + 1).ToString(CultureInfo.InvariantCulture)},");
            sb.AppendLine($"      \"xmin\": {extent.XMin.ToString(CultureInfo.InvariantCulture)},");
            sb.AppendLine($"      \"ymin\": {extent.YMin.ToString(CultureInfo.InvariantCulture)},");
            sb.AppendLine($"      \"xmax\": {extent.XMax.ToString(CultureInfo.InvariantCulture)},");
            sb.AppendLine($"      \"ymax\": {extent.YMax.ToString(CultureInfo.InvariantCulture)},");
            sb.AppendLine($"      \"width\": {extent.Width.ToString(CultureInfo.InvariantCulture)},");
            sb.AppendLine($"      \"height\": {extent.Height.ToString(CultureInfo.InvariantCulture)}");
            sb.Append(i == _manualProposedPipelineSegmentGeometries.Count - 1 ? "    }" : "    },");
            sb.AppendLine();
        }
        sb.AppendLine("  ],");
        sb.AppendLine($"  \"capturedLocalTime\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"");
        sb.AppendLine("}");
        return sb.ToString();
    }
    /// <summary>
    /// The readout this mode writes its summary into.
    ///
    /// Named outright. This used to hunt the visual tree: a name holding both "manual" and "pipeline",
    /// then any box whose text already looked like one of these summaries, and failing both, the largest
    /// text box on the window. The real box is called ProposedPipelineTextBox, which has no "manual" in
    /// it, so the name test never matched and a fresh one has no summary text to match either. Every
    /// call fell through to the size test and wrote this JSON into whatever box happened to be biggest
    /// and visible, which was the readout belonging to whichever mode was on screen at the time.
    /// </summary>
    private TextBox? FindManualProposedPipelineTextBox() => ProposedPipelineTextBox;

    /// <summary>
    /// Reports what this mode just did, through the view model's status like everything else.
    ///
    /// This also used to walk the tree, for anything named "status". The only two matches in this window
    /// are the template and SharePoint status lines on page 1, so drawing a segment on page 2 overwrote
    /// the message telling the user which template they had picked.
    /// </summary>
    private void SetManualProposedPipelineStatus(string message)
    {
        if (DataContext is ViewModels.ExporterViewModel vm) { vm.Status = message; }
    }
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }
            var nested = FindVisualChild<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }
    // GetVisualChildren was removed with the two callers above. Sweeping the window for a control that
    // looks about right is what put this mode's text into other modes' boxes, and leaving the sweep in
    // place would leave the next such call one line away.
    private void StartProposedPipeline_Click(object sender, RoutedEventArgs e)
    {
        StartManualProposedMainSegment_Click(sender, e);
    }
    private void FinishProposedPipeline_Click(object sender, RoutedEventArgs e)
    {
        FinishManualProposedMainSegments_Click(sender, e);
    }
    private void CancelProposedPipeline_Click(object sender, RoutedEventArgs e)
    {
        ApplyManualProposedMainSegment_Click(sender, e);
    }
    private void ClearProposedPipeline_Click(object sender, RoutedEventArgs e)
    {
        ClearManualProposedMainSegments_Click(sender, e);
    }
    private RuntimeGeometry? BuildManualProposedPipelineMultipartGeometry()
    {
        if (_manualProposedPipelineSegmentGeometries.Count == 0)
        {
            return null;
        }
        if (_manualProposedPipelineSegmentGeometries.Count == 1)
        {
            return _manualProposedPipelineSegmentGeometries[0];
        }
        var paths = new List<System.Text.Json.JsonElement>();
        System.Text.Json.JsonElement? spatialReference = null;
        foreach (var segment in _manualProposedPipelineSegmentGeometries)
        {
            using var document = System.Text.Json.JsonDocument.Parse(segment.ToJson());
            var root = document.RootElement;
            if (spatialReference == null && root.TryGetProperty("spatialReference", out var sr))
            {
                spatialReference = sr.Clone();
            }
            if (root.TryGetProperty("paths", out var segmentPaths))
            {
                foreach (var path in segmentPaths.EnumerateArray())
                {
                    paths.Add(path.Clone());
                }
            }
        }
        if (paths.Count == 0)
        {
            return _manualProposedPipelineSegmentGeometries.LastOrDefault();
        }
        var json = new StringBuilder();
        json.Append("{\"paths\":[");
        for (var i = 0; i < paths.Count; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }
            json.Append(paths[i].GetRawText());
        }
        json.Append(']');
        if (spatialReference != null)
        {
            json.Append(",\"spatialReference\":");
            json.Append(spatialReference.Value.GetRawText());
        }
        json.Append('}');
        return RuntimeGeometry.FromJson(json.ToString());
    }
}
