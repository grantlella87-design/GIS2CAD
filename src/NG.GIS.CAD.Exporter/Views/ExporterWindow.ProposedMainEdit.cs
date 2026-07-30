using System;
using System.Globalization;
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
    private async void EditImportedProposedMain_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_mapView == null)
            {
                WorkOrderGeometryTextBox.Text = "MapView is not initialized.";
                return;
            }
            if (_geometryEditor == null)
            {
                WorkOrderGeometryTextBox.Text = "Geometry editor is not initialized.";
                return;
            }
            if (_editableImportedProposedMainGeometry == null || _editableImportedProposedMainGeometry.IsEmpty)
            {
                WorkOrderGeometryTextBox.Text = "No imported proposed main geometry is available to edit. Select a work order that exists in proposed mains first.";
                return;
            }
            if (_geometryEditor.IsStarted)
            {
                WorkOrderGeometryTextBox.Text = "Geometry editor is already active. Apply or cancel the current edit first.";
                return;
            }
            _workOrderOverlay?.Graphics.Clear();
            _mapView.GeometryEditor = _geometryEditor;
            _geometryEditor.Start(_editableImportedProposedMainGeometry);
            _isEditingImportedProposedMain = true;
            if (DataContext is ViewModels.ExporterViewModel vm)
            {
                vm.Status = "Editing imported proposed main. Use the map editor handles, then click Apply Edit.";
            }
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            WorkOrderGeometryTextBox.Text = FlattenProposedMainEditException(ex);
        }
    }

    private async void ApplyImportedProposedMainEdit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_geometryEditor == null || !_geometryEditor.IsStarted)
            {
                WorkOrderGeometryTextBox.Text = "No imported proposed main edit is active.";
                return;
            }
            var editedGeometry = _geometryEditor.Stop();
            _isEditingImportedProposedMain = false;
            if (editedGeometry == null || editedGeometry.IsEmpty)
            {
                WorkOrderGeometryTextBox.Text = "Edited proposed main geometry was empty.";
                return;
            }
            var projected = editedGeometry.SpatialReference?.Wkid == 3857 ? editedGeometry : GeometryEngine.Project(editedGeometry, SpatialReferences.WebMercator);
            if (projected == null || projected.IsEmpty)
            {
                WorkOrderGeometryTextBox.Text = "Edited proposed main geometry could not be projected to Web Mercator.";
                return;
            }
            // Dragging a vertex reopens the joins the import closed, so the snap runs again on what
            // the editor produced rather than only on what the service returned.
            var snapped = SnapAmendedProposedMainGeometries(new[] { projected });
            var snappedGeometry = snapped.Count > 0 && !snapped[0].IsEmpty ? snapped[0] : projected;

            _editableImportedProposedMainGeometry = snappedGeometry;
            DrawEditedImportedProposedMainGeometry(snappedGeometry);
            var paddingFeet = GetSharedPaddingFeetForWorkOrderImport();
            var buffered = paddingFeet > 0 ? GeometryEngine.Buffer(snappedGeometry, paddingFeet * 0.3048) : snappedGeometry;
            var extent = buffered?.Extent ?? snappedGeometry.Extent;
            if (_mapView != null)
            {
                await _mapView.SetViewpointGeometryAsync(ExpandEnvelopeByFeet(extent, 200), 20);
            }
            CommitExtentToViewModelForWorkOrderImport(extent);
            WriteEditedProposedMainProof(snappedGeometry, extent, paddingFeet);
            if (DataContext is ViewModels.ExporterViewModel vm)
            {
                vm.Status = "Applied edited proposed main geometry for work order "
                    + (_editableImportedProposedMainWorkOrderId ?? string.Empty)
                    + ". Endpoint snaps applied: " + _lastProposedMainEndpointSnapCount + ".";
            }
        }
        catch (Exception ex)
        {
            WorkOrderGeometryTextBox.Text = FlattenProposedMainEditException(ex);
        }
    }

    private void CancelImportedProposedMainEdit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_geometryEditor != null && _geometryEditor.IsStarted)
            {
                _geometryEditor.Stop();
            }
            _isEditingImportedProposedMain = false;
            if (_editableImportedProposedMainGeometry != null && !_editableImportedProposedMainGeometry.IsEmpty)
            {
                DrawEditedImportedProposedMainGeometry(_editableImportedProposedMainGeometry);
            }
            if (DataContext is ViewModels.ExporterViewModel vm)
            {
                vm.Status = "Canceled imported proposed main edit.";
            }
        }
        catch (Exception ex)
        {
            WorkOrderGeometryTextBox.Text = FlattenProposedMainEditException(ex);
        }
    }

    private void DrawEditedImportedProposedMainGeometry(Geometry geometry)
    {
        if (_workOrderOverlay == null) { return; }
        _workOrderOverlay.Graphics.Clear();
        var s = _editableImportedProposedMainSymbol ?? new ProposedMainRestSymbol();
        var lineColor = System.Drawing.Color.FromArgb(ClampByte(s.A), ClampByte(s.R), ClampByte(s.G), ClampByte(s.B));
        var lineStyle = ConvertRestLineStyleToRuntimeStyle(s.Style);
        var lineSymbol = new SimpleLineSymbol(lineStyle, lineColor, Math.Max(2.0, s.Width));
        _workOrderOverlay.Graphics.Add(new Graphic(geometry, lineSymbol));
        var paddingFeet = GetSharedPaddingFeetForWorkOrderImport();
        if (paddingFeet > 0)
        {
            var buffer = GeometryEngine.Buffer(geometry, paddingFeet * 0.3048);
            RememberProposedMainBuffer(buffer);
            var bufferSymbol = new SimpleFillSymbol(
                SimpleFillSymbolStyle.Solid,
                System.Drawing.Color.FromArgb(45, ClampByte(s.R), ClampByte(s.G), ClampByte(s.B)),
                new SimpleLineSymbol(lineStyle, lineColor, 2));
            _workOrderOverlay.Graphics.Add(new Graphic(buffer, bufferSymbol));
        }
    }

    private void WriteEditedProposedMainProof(Geometry geometry, Envelope extent, double paddingFeet)
    {
        var s = _editableImportedProposedMainSymbol ?? new ProposedMainRestSymbol();
        var proof = new
        {
            source = "edited imported proposed main",
            originalSource = "MA/Material_View_MA/MapServer/54",
            matchField = "workorderid",
            selectedWorkOrder = _editableImportedProposedMainWorkOrderId,
            editMode = "applied in MapView GeometryEditor",
            geometryType = geometry.GeometryType.ToString(),
            endpointSnapsApplied = _lastProposedMainEndpointSnapCount,
            restRendererColor = new[] { s.R, s.G, s.B, s.A },
            restRendererWidth = s.Width,
            restRendererStyle = s.Style,
            paddingFeet,
            wkid = 3857,
            xmin = Math.Round(extent.XMin, 3),
            ymin = Math.Round(extent.YMin, 3),
            xmax = Math.Round(extent.XMax, 3),
            ymax = Math.Round(extent.YMax, 3),
            capturedLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        WorkOrderGeometryTextBox.Text = JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FlattenProposedMainEditException(Exception ex)
    {
        var messages = new System.Collections.Generic.List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            messages.Add(current.GetType().Name + ": " + current.Message);
        }
        return string.Join(" -> ", messages);
    }
}
