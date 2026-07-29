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
    private void StartManualProposedMainSegment_Click(object sender, RoutedEventArgs e)
    {
        StartManualProposedPipelineSegmentDrawing();
        SetManualProposedPipelineStatus("Manual proposed pipeline segment drawing started. Draw one segment, then use Apply Segment to add another.");
    }
    private void ApplyManualProposedMainSegment_Click(object sender, RoutedEventArgs e)
    {
        var added = StopAndStoreCurrentManualProposedPipelineSegment();
        RefreshManualProposedPipelineSegmentOverlay();
        UpdateManualProposedPipelineSegmentSummary();
        StartManualProposedPipelineSegmentDrawing();
        SetManualProposedPipelineStatus(added
            ? "Manual proposed pipeline segment added. Continue drawing the next segment or use Finish Segments."
            : "No segment geometry was captured. Continue drawing or use Finish Segments.");
    }
    private void FinishManualProposedMainSegments_Click(object sender, RoutedEventArgs e)
    {
        StopAndStoreCurrentManualProposedPipelineSegment();
        RefreshManualProposedPipelineSegmentOverlay();
        UpdateManualProposedPipelineSegmentSummary();
        SetLegacySingleManualPipelineGeometryToCombinedSegments();
        SetManualProposedPipelineStatus(_manualProposedPipelineSegmentGeometries.Count == 1
            ? "Manual proposed pipeline finished with 1 segment."
            : $"Manual proposed pipeline finished with {_manualProposedPipelineSegmentGeometries.Count} segments.");
    }
    private void ClearManualProposedMainSegments_Click(object sender, RoutedEventArgs e)
    {
        TryStopGeometryEditor();
        _manualProposedPipelineSegmentGeometries.Clear();
        if (_manualProposedPipelineSegmentOverlay != null)
        {
            _manualProposedPipelineSegmentOverlay.Graphics.Clear();
        }
        SetLegacySingleManualPipelineGeometry(null);
        UpdateManualProposedPipelineSegmentSummary();
        SetManualProposedPipelineStatus("Manual proposed pipeline segments cleared.");
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
        _manualProposedPipelineSegmentGeometries.Add(geometry);
        SetLegacySingleManualPipelineGeometryToCombinedSegments();
        return true;
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
        _manualProposedPipelineSegmentSymbol ??= new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.Red, 4.0);
        _manualProposedPipelineSegmentOverlay.Graphics.Clear();
        foreach (var segment in _manualProposedPipelineSegmentGeometries)
        {
            _manualProposedPipelineSegmentOverlay.Graphics.Add(new Graphic(segment, _manualProposedPipelineSegmentSymbol));
        }
    }
    private void UpdateManualProposedPipelineSegmentSummary()
    {
        var json = BuildManualProposedPipelineSegmentSummaryJson();
        var targetTextBox = FindManualProposedPipelineTextBox();
        if (targetTextBox != null)
        {
            targetTextBox.Text = json;
        }
    }
    private string BuildManualProposedPipelineSegmentSummaryJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"source\": \"Manual proposed pipeline multi-segment GeometryEditor\",");
        sb.AppendLine("  \"geometryType\": \"Polyline\",");
        sb.AppendLine($"  \"segmentCount\": {_manualProposedPipelineSegmentGeometries.Count.ToString(CultureInfo.InvariantCulture)},");
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
    private TextBox? FindManualProposedPipelineTextBox()
    {
        var allTextBoxes = GetVisualChildren<TextBox>(this).ToList();
        var preferred = allTextBoxes.FirstOrDefault(tb =>
            (tb.Name.IndexOf("manual", StringComparison.OrdinalIgnoreCase) >= 0 && tb.Name.IndexOf("pipeline", StringComparison.OrdinalIgnoreCase) >= 0) ||
            tb.Text.IndexOf("GeometryEditor", StringComparison.OrdinalIgnoreCase) >= 0 ||
            tb.Text.IndexOf("geometryType", StringComparison.OrdinalIgnoreCase) >= 0 ||
            tb.Text.IndexOf("capturedLocalTime", StringComparison.OrdinalIgnoreCase) >= 0);
        return preferred ?? allTextBoxes.OrderByDescending(tb => tb.ActualHeight * tb.ActualWidth).FirstOrDefault();
    }
    private void SetManualProposedPipelineStatus(string message)
    {
        foreach (var statusCandidate in GetVisualChildren<TextBlock>(this))
        {
            if (statusCandidate.Name.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusCandidate.Text = message;
                return;
            }
        }
        foreach (var statusCandidate in GetVisualChildren<TextBox>(this))
        {
            if (statusCandidate.Name.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusCandidate.Text = message;
                return;
            }
        }
    }
    private void SetLegacySingleManualPipelineGeometryToLastSegment() { SetLegacySingleManualPipelineGeometryToCombinedSegments(); }
    private void SetLegacySingleManualPipelineGeometry(RuntimeGeometry? geometry)
    {
        var field = GetType().GetField("_proposedPipelineGeometry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && (geometry == null || field.FieldType.IsAssignableFrom(geometry.GetType())))
        {
            field.SetValue(this, geometry);
        }
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
    private static IEnumerable<T> GetVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            yield break;
        }
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }
            foreach (var nested in GetVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
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
    private void SetLegacySingleManualPipelineGeometryToCombinedSegments()
    {
        SetLegacySingleManualPipelineGeometry(BuildManualProposedPipelineMultipartGeometry());
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
