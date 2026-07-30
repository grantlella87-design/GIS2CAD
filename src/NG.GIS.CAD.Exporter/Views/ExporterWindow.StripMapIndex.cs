using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using NG.GIS.CAD.Exporter.Services;
using NG.GIS.CAD.Exporter.ViewModels;
using RuntimeGeometry = Esri.ArcGISRuntime.Geometry.Geometry;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Lays the strip map index out along the proposed main and draws it on the page 2 map.
///
/// The sheets follow whichever proposed main is current: the one imported for a work order, as
/// edited, or the segments drawn by hand.
/// </summary>
public partial class ExporterWindow
{
    private GraphicsOverlay? _stripMapOverlay;
    private IReadOnlyList<StripMapSheet> _stripMapSheets = Array.Empty<StripMapSheet>();

    private async void BuildStripMapIndex_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        try
        {
            var route = GetCurrentProposedMainGeometry();
            if (route == null || route.IsEmpty)
            {
                vm.StripMapSummary = "No proposed main to lay sheets along yet. Import a work order, or draw the main by hand, first.";
                return;
            }

            if (vm.StripMapSheetWidth <= 0 || vm.StripMapSheetHeight <= 0)
            {
                vm.StripMapSummary = "Choose a viewport from the template, or type a sheet width and height.";
                return;
            }

            if (vm.StripMapScaleDenominator <= 0)
            {
                vm.StripMapSummary = "The scale denominator has to be greater than zero. 240 gives 1:240.";
                return;
            }

            _stripMapSheets = StripMapIndexService.Build(
                route,
                vm.StripMapGroundWidthMetres,
                vm.StripMapGroundHeightMetres,
                vm.StripMapOverlapFraction);

            DrawStripMapSheets(_stripMapSheets);
            WriteStripMapProof(vm);

            // Handed to the view model so the export can put the same sheets in the drawing, on their
            // own CAD layer, rather than recomputing a set that might not match what is on the map.
            vm.StripMapSheets = _stripMapSheets;

            if (_stripMapSheets.Count == 0)
            {
                vm.StripMapSummary = "The proposed main produced no sheets. It may have no length, or be a single point.";
                return;
            }

            var firstSheet = _stripMapSheets[0];
            var lastSheet = _stripMapSheets[^1];
            var overhangFeet = StripMapIndexService.MetresToFeet(vm.StripMapGroundWidthMetres)
                - (firstSheet.EndStationFeet - firstSheet.StartStationFeet);

            vm.StripMapSummary = string.Format(
                CultureInfo.InvariantCulture,
                "{0} sheet{1} along {2:0.#} ft of proposed main, at 1:{3:0.##}. First and last sheet hang {4:0.#} ft past each end.",
                _stripMapSheets.Count,
                _stripMapSheets.Count == 1 ? string.Empty : "s",
                lastSheet.EndStationFeet,
                vm.StripMapScaleDenominator,
                Math.Max(0, overhangFeet));

            await vm.SaveStripMapSettingsAsync();
        }
        catch (Exception ex)
        {
            vm.StripMapSummary = "Laying out the strip map index failed: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void ClearStripMapIndex_Click(object sender, RoutedEventArgs e)
    {
        _stripMapSheets = Array.Empty<StripMapSheet>();
        _stripMapOverlay?.Graphics.Clear();
        if (DataContext is ExporterViewModel vm)
        {
            // Cleared here too, so an export after this does not still write the old sheets.
            vm.StripMapSheets = _stripMapSheets;
            vm.StripMapSummary = "Strip map sheets cleared.";
        }
    }

    /// <summary>
    /// The proposed main as it currently stands. The imported geometry is preferred because editing
    /// it writes back to the same field, so it is already the amended route; hand drawn segments are
    /// used when there is no imported main.
    /// </summary>
    private RuntimeGeometry? GetCurrentProposedMainGeometry()
    {
        if (_editableImportedProposedMainGeometry != null && !_editableImportedProposedMainGeometry.IsEmpty)
        {
            return _editableImportedProposedMainGeometry;
        }
        return BuildManualProposedPipelineMultipartGeometry();
    }

    private void DrawStripMapSheets(IReadOnlyList<StripMapSheet> sheets)
    {
        var mapView = GetExporterMapView();
        if (mapView == null) { return; }

        if (_stripMapOverlay == null)
        {
            _stripMapOverlay = new GraphicsOverlay { Id = "Strip Map Index" };
            mapView.GraphicsOverlays.Add(_stripMapOverlay);
        }

        _stripMapOverlay.Graphics.Clear();

        // Outline only. A filled sheet would hide the main it is meant to be indexing.
        var outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.FromArgb(255, 0, 90, 170), 2.0);
        var fill = new SimpleFillSymbol(SimpleFillSymbolStyle.Null, System.Drawing.Color.Transparent, outline);

        foreach (var sheet in sheets)
        {
            _stripMapOverlay.Graphics.Add(new Graphic(sheet.Outline, fill));

            // The number is the whole point of an index, so it is drawn turned with its sheet.
            var label = new TextSymbol
            {
                Text = sheet.Number.ToString(CultureInfo.InvariantCulture),
                Color = System.Drawing.Color.FromArgb(255, 0, 90, 170),
                Size = 16,
                HorizontalAlignment = Esri.ArcGISRuntime.Symbology.HorizontalAlignment.Center,
                VerticalAlignment = Esri.ArcGISRuntime.Symbology.VerticalAlignment.Middle,
                Angle = -sheet.RotationDegrees
            };
            var centre = new MapPoint(sheet.CenterX, sheet.CenterY, sheet.Outline.SpatialReference);
            _stripMapOverlay.Graphics.Add(new Graphic(centre, label));
        }
    }

    /// <summary>
    /// Writes the sheet layout into the page 2 readout, so the numbers behind the picture can be
    /// checked rather than taken on trust.
    /// </summary>
    private void WriteStripMapProof(ExporterViewModel vm)
    {
        var sheets = _stripMapSheets;
        var proof = new
        {
            source = "strip map index along the proposed main",
            viewport = vm.SelectedStripMapViewport?.Display ?? "typed sheet dimensions",
            sheetPaperWidth = vm.StripMapSheetWidth,
            sheetPaperHeight = vm.StripMapSheetHeight,
            sheetPaperUnits = vm.StripMapSheetIsMillimetres ? "mm" : "in",
            scale = "1:" + vm.StripMapScaleDenominator.ToString("0.##", CultureInfo.InvariantCulture),
            sheetGroundWidthFeet = Math.Round(StripMapIndexService.MetresToFeet(vm.StripMapGroundWidthMetres), 2),
            sheetGroundHeightFeet = Math.Round(StripMapIndexService.MetresToFeet(vm.StripMapGroundHeightMetres), 2),
            overlapPercent = Math.Round(vm.StripMapOverlapFraction * 100.0, 2),
            sheetCount = sheets.Count,

            // The run is centred on the route, so this is how far the first and last sheet reach past
            // its ends. Equal by construction, which is what makes those two sheets show the same
            // length of pipe.
            endOverhangFeet = sheets.Count == 0
                ? 0
                : Math.Round(StripMapIndexService.MetresToFeet(vm.StripMapGroundWidthMetres)
                    - (sheets[0].EndStationFeet - sheets[0].StartStationFeet), 2),

            sheets = sheets.Select(s => new
            {
                s.Number,
                startFeet = Math.Round(s.StartStationFeet, 2),
                endFeet = Math.Round(s.EndStationFeet, 2),

                // Pipe on this sheet rather than sheet size, so the first and last reading back the
                // same is the thing to look at.
                pipeFeet = Math.Round(s.EndStationFeet - s.StartStationFeet, 2),
                bearingDegrees = Math.Round(s.RotationDegrees, 3),
                centerX = Math.Round(s.CenterX, 3),
                centerY = Math.Round(s.CenterY, 3)
            }).ToList(),
            wkid = 3857,
            capturedLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };

        WorkOrderGeometryTextBox.Text = JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true });
    }
}
