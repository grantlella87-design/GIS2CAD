using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Esri.ArcGISRuntime.Geometry;
using RuntimeGeometry = Esri.ArcGISRuntime.Geometry.Geometry;
using Esri.ArcGISRuntime.UI;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Elbows at the bends of a steel main, and picking a placed feature out on the map.
///
/// Steel does not bend: a change of direction in a steel main is a fitting, and the fitting is a
/// feature GIS expects to be there. Drawing the main and then remembering to drop an elbow on every
/// corner is the kind of task that is done well for the first three corners.
/// </summary>
public partial class ExporterWindow
{
    /// <summary>
    /// How much a main has to turn at a vertex before the turn is a fitting rather than a drawn line
    /// that is not quite straight. Five degrees, which the user set: below it, a hand drawn corridor
    /// that wanders would sprout elbows all along its length.
    /// </summary>
    private const double SteelElbowMinimumDeflectionDegrees = 5.0;

    /// <summary>The word in an asset type that means the main is steel, and the word in the junction
    /// layer's symbol list that means elbow. Matched loosely, because neither is written here.</summary>
    private const string SteelMaterialWord = "steel";
    private const string ElbowSymbolWord = "elbow";

    /// <summary>
    /// Rebuilds the elbows implied by the drawing: one at every bend of more than the minimum, on every
    /// segment whose attributes say steel.
    ///
    /// Rebuilt rather than added to. A main can be redrawn, a bend can be straightened, and the material
    /// can be changed from steel to something else after the elbows have appeared; working out the whole
    /// set again from what is currently drawn is the only version of this that cannot leave an elbow
    /// behind on a corner that is no longer there.
    /// </summary>
    private void SyncSteelElbowsToProposedMain()
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        RemoveAutomaticPlacedFeatures(vm);

        var elbow = FindElbowSymbol(vm);
        if (elbow == null) { return; }

        EnsurePlacedFeatureOverlay();

        var placed = 0;
        var rows = vm.ProposedMainSegmentRows;

        for (var i = 0; i < _manualProposedPipelineSegmentGeometries.Count; i++)
        {
            if (i >= rows.Count) { break; }
            if (!rows[i].AssetTypeMentions(SteelMaterialWord)) { continue; }

            foreach (var bend in FindBends(_manualProposedPipelineSegmentGeometries[i]))
            {
                AddAutomaticPlacedFeature(vm, elbow, bend.At, bend.AngleDegrees);
                placed++;
            }
        }

        if (placed > 0)
        {
            vm.Status = placed + (placed == 1 ? " elbow was" : " elbows were")
                + " added at the bends of the steel main. They go to GIS with everything else placed here.";
        }

        vm.RaisePlacedFeaturesChanged();
    }

    /// <summary>
    /// Every vertex of one segment that turns by more than the minimum, with the direction the fitting
    /// should face: the average of the two runs meeting there, which is the line that bisects the bend.
    /// </summary>
    private static IEnumerable<(MapPoint At, double AngleDegrees)> FindBends(RuntimeGeometry? geometry)
    {
        if (geometry is not Multipart multipart) { yield break; }

        foreach (var part in multipart.Parts)
        {
            var points = part.Points.ToList();

            // The ends of a run are not bends: nothing meets them on one side, so there is nothing to
            // take an average of and nothing for a fitting to join.
            for (var i = 1; i < points.Count - 1; i++)
            {
                var incoming = BearingDegrees(points[i - 1], points[i]);
                var outgoing = BearingDegrees(points[i], points[i + 1]);

                var deflection = NormalizeDeflection(outgoing - incoming);
                if (Math.Abs(deflection) < SteelElbowMinimumDeflectionDegrees) { continue; }

                // Halfway between the two, which is the same as the average of the bearings and is the
                // way a fitting on that corner actually lies.
                var angle = incoming + (deflection / 2);
                yield return (points[i], angle < 0 ? angle + 360 : angle % 360);
            }
        }
    }

    /// <summary>Clockwise from north, which is how a marker's angle is measured.</summary>
    private static double BearingDegrees(MapPoint from, MapPoint to)
    {
        var angle = Math.Atan2(to.X - from.X, to.Y - from.Y) * 180.0 / Math.PI;
        return angle < 0 ? angle + 360 : angle;
    }

    /// <summary>
    /// A turn as the smaller of the two ways round, so a main that swings from due north to due west
    /// reads as a ninety degree turn rather than a two hundred and seventy degree one.
    /// </summary>
    private static double NormalizeDeflection(double degrees)
    {
        var wrapped = degrees % 360;
        if (wrapped > 180) { wrapped -= 360; }
        if (wrapped < -180) { wrapped += 360; }
        return wrapped;
    }

    /// <summary>
    /// The elbow in the junction layer's palette, found by its name rather than by a code written here,
    /// because what the subtypes are called is GIS's to decide.
    /// </summary>
    private static SymbolPaletteItemViewModel? FindElbowSymbol(ExporterViewModel vm)
    {
        foreach (var layer in vm.SymbolPalettes)
        {
            foreach (var symbol in layer.Symbols)
            {
                if (symbol.Label.Contains(ElbowSymbolWord, StringComparison.OrdinalIgnoreCase)) { return symbol; }
            }
        }

        return null;
    }

    private void AddAutomaticPlacedFeature(
        ExporterViewModel vm, SymbolPaletteItemViewModel symbol, MapPoint at, double angleDegrees)
    {
        var graphic = new Graphic(at, BuildPlacedSymbol(symbol, angleDegrees));
        _placedFeatureOverlay?.Graphics.Add(graphic);

        var row = new PlacedFeatureViewModel(symbol)
        {
            IsAutomatic = true,
            Position = DescribePlacement(at)
        };

        _placedPaletteFeatures[row] = new PlacedPaletteFeature(at, symbol, graphic, angleDegrees);
        vm.PlacedFeatures.Add(row);
    }

    private void RemoveAutomaticPlacedFeatures(ExporterViewModel vm)
    {
        foreach (var row in vm.PlacedFeatures.Where(p => p.IsAutomatic).ToList())
        {
            if (_placedPaletteFeatures.TryGetValue(row, out var placed))
            {
                _placedFeatureOverlay?.Graphics.Remove(placed.Graphic);
                _placedPaletteFeatures.Remove(row);
            }

            vm.PlacedFeatures.Remove(row);
        }
    }

    /// <summary>
    /// Picks one placed feature out on the map, and brings it into view when it is not already there.
    ///
    /// Only when it is off screen. A list of a dozen valves in one street would otherwise jump the map
    /// on every click, and moving the map out from under somebody who can already see the thing they
    /// clicked is the opposite of helping them find it.
    /// </summary>
    private async void FocusPlacedFeature(PlacedFeatureViewModel row)
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        foreach (var other in vm.PlacedFeatures) { other.IsFocused = ReferenceEquals(other, row); }

        foreach (var pair in _placedPaletteFeatures)
        {
            pair.Value.Graphic.IsSelected = ReferenceEquals(pair.Key, row);
        }

        if (!_placedPaletteFeatures.TryGetValue(row, out var placed)) { return; }
        if (_mapView == null) { return; }

        try
        {
            if (_mapView.VisibleArea != null && GeometryEngine.Intersects(_mapView.VisibleArea, placed.Location))
            {
                return;
            }

            // Centred at the scale the map is already at, so this brings the feature into view without
            // also deciding how closely the user wanted to be looking at things.
            await _mapView.SetViewpointCenterAsync(placed.Location);
        }
        catch (Exception)
        {
            // Being unable to move the map is not worth interrupting anything for. The row is still
            // picked out in the list and the graphic is still selected on the map.
        }
    }

    private void PlacedFeatureRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not PlacedFeatureViewModel row) { return; }

        FocusPlacedFeature(row);
    }
}
