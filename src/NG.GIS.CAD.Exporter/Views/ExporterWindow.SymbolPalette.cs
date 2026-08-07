using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using NG.GIS.CAD.Exporter.Auth;
using NG.GIS.CAD.Exporter.Services;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// The tool panel beside the map: pick a symbol from a GIS layer, click the map, and a feature of that
/// class is placed there and sent to that layer.
///
/// The symbols are the layer's own, read from its renderer. A palette drawn here to look like GIS would
/// be wrong the first time somebody added a subtype, and wrong quietly: the user would pick the closest
/// thing on offer and the feature would go up as something else.
/// </summary>
public partial class ExporterWindow
{
    /// <summary>
    /// The layers offered in the panel. More will follow, which is why this is a list and why each one
    /// gets a section of its own that collapses.
    /// </summary>
    private static readonly (string Name, string Url)[] SymbolPaletteLayers =
    {
        ("Network Junction (P)", "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer/52")
    };

    private GraphicsOverlay? _placedFeatureOverlay;

    /// <summary>
    /// What has been placed and not yet sent, so the map can draw it and the upload knows what to send.
    /// </summary>
    private readonly List<PlacedPaletteFeature> _placedPaletteFeatures = new();

    private bool _symbolPalettesLoaded;

    private sealed record PlacedPaletteFeature(MapPoint Location, SymbolPaletteItemViewModel Symbol);

    /// <summary>
    /// Reads each layer's symbols into the panel. Once per session: a renderer does not change while
    /// the window is open, and re-reading it on every visit to page 2 would be a network round trip for
    /// an answer already on screen.
    /// </summary>
    private async Task LoadSymbolPalettesAsync()
    {
        if (_symbolPalettesLoaded) { return; }
        if (DataContext is not ExporterViewModel vm) { return; }

        var token = ArcGisPortalOAuth.CurrentAccessToken;
        if (string.IsNullOrWhiteSpace(token)) { return; }

        _symbolPalettesLoaded = true;
        vm.SymbolPalettes.Clear();

        foreach (var (name, url) in SymbolPaletteLayers)
        {
            var section = new SymbolPaletteLayerViewModel(name);
            vm.SymbolPalettes.Add(section);

            try
            {
                var layer = await GisSymbolPaletteService.LoadAsync(url, token);

                foreach (var symbol in layer.Symbols)
                {
                    section.Symbols.Add(new SymbolPaletteItemViewModel(
                        symbol, layer.LayerUrl, layer.DrawnByFieldName, CreateSwatch(symbol)));
                }

                if (section.Symbols.Count == 0)
                {
                    section.Status = "This layer reported no symbols.";
                }
            }
            catch (Exception ex)
            {
                // Said on the section rather than thrown. One layer that cannot be read should not take
                // the panel down with it, and an empty section with no explanation reads as a layer
                // that genuinely draws nothing.
                section.Status = "Could not be read: " + ex.Message;
            }
        }
    }

    /// <summary>
    /// Everything that has to happen before page 2 is left: the proposed main goes up, and so does
    /// anything placed from the palette.
    ///
    /// The main goes first and its answer decides whether the page moves at all, because that is the
    /// one that can refuse. What was placed from the palette is sent afterwards and reported, and its
    /// failures are said rather than used to hold the page: the features stay on the map and unsent, so
    /// nothing is lost by moving on and coming back.
    /// </summary>
    private async Task<bool> LeaveExtentPageAsync()
    {
        if (!await TryUploadManualProposedMainAsync()) { return false; }

        var placed = await TryUploadPlacedPaletteFeaturesAsync();
        if (placed != null && DataContext is ExporterViewModel vm)
        {
            vm.Status = string.IsNullOrWhiteSpace(vm.Status) ? placed : vm.Status + " " + placed;
        }

        return true;
    }

    /// <summary>
    /// Turns the service's swatch into something WPF can draw. Null when the service described the
    /// symbol geometrically rather than as a picture, in which case the panel falls back to its colour.
    /// </summary>
    private static BitmapImage? CreateSwatch(GisPaletteSymbol symbol)
    {
        if (symbol.ImageData == null || symbol.ImageData.Length == 0) { return null; }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(symbol.ImageData);
            image.EndInit();

            // Frozen so it can be handed to the UI from wherever this ran.
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Arms or disarms a symbol. Clicking the armed one again puts the map back to normal, so there is
    /// a way out that does not involve placing something to get rid of the cursor.
    /// </summary>
    private void SymbolPaletteItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExporterViewModel vm) { return; }
        if (sender is not FrameworkElement element || element.DataContext is not SymbolPaletteItemViewModel symbol) { return; }

        vm.SelectedPaletteSymbol = ReferenceEquals(vm.SelectedPaletteSymbol, symbol) ? null : symbol;
    }

    /// <summary>
    /// Places one feature where the map was clicked, drawn with the symbol that was picked.
    ///
    /// Returns whether the click was used, so the caller can leave the tap alone when nothing is armed.
    /// Panning and every other use of the map is untouched the rest of the time.
    /// </summary>
    private bool TryPlacePaletteSymbolAt(MapPoint? location)
    {
        if (location == null) { return false; }
        if (_mapView == null) { return false; }
        if (DataContext is not ExporterViewModel vm) { return false; }

        var symbol = vm.SelectedPaletteSymbol;
        if (symbol == null) { return false; }

        EnsurePlacedFeatureOverlay();

        _placedPaletteFeatures.Add(new PlacedPaletteFeature(location, symbol));
        _placedFeatureOverlay?.Graphics.Add(new Graphic(location, BuildPlacedSymbol(symbol)));

        vm.Status = symbol.Label + " placed. It is added to " + symbol.LayerUrl.Split('/')[^1]
            + " when you move on to page 3.";
        return true;
    }

    private void EnsurePlacedFeatureOverlay()
    {
        if (_mapView == null || _placedFeatureOverlay != null) { return; }

        _placedFeatureOverlay = new GraphicsOverlay();
        _mapView.GraphicsOverlays?.Add(_placedFeatureOverlay);
    }

    /// <summary>
    /// What a placed feature is drawn with. The layer's own picture where it supplied one, so what is
    /// on the map matches what GIS will draw once it is up there, and a plain marker in its colour
    /// where it did not.
    /// </summary>
    private static Symbol BuildPlacedSymbol(SymbolPaletteItemViewModel item)
    {
        if (item.Symbol.ImageData is { Length: > 0 } data)
        {
            try
            {
                var picture = new PictureMarkerSymbol(new RuntimeImage(data));
                if (item.Symbol.Size > 0) { picture.Width = item.Symbol.Size; picture.Height = item.Symbol.Size; }
                return picture;
            }
            catch (Exception)
            {
                // Falls through to the plain marker below.
            }
        }

        return new SimpleMarkerSymbol(
            SimpleMarkerSymbolStyle.Circle,
            System.Drawing.Color.FromArgb(
                ClampByte(item.Symbol.A), ClampByte(item.Symbol.R), ClampByte(item.Symbol.G), ClampByte(item.Symbol.B)),
            Math.Max(6, item.Symbol.Size));
    }

    /// <summary>
    /// Sends everything placed to the layer it was picked from, on the way off page 2.
    ///
    /// Reported rather than thrown, and the placed features are kept when a send fails, so a network
    /// problem does not silently lose work somebody has just done.
    /// </summary>
    private async Task<string?> TryUploadPlacedPaletteFeaturesAsync()
    {
        if (_placedPaletteFeatures.Count == 0) { return null; }

        var token = ArcGisPortalOAuth.CurrentAccessToken;
        if (string.IsNullOrWhiteSpace(token)) { return "No ArcGIS token, so nothing placed on the map was added to GIS."; }

        var added = 0;
        var failures = new List<string>();

        foreach (var placed in _placedPaletteFeatures)
        {
            var wgs84 = GeometryEngine.Project(placed.Location, SpatialReferences.Wgs84) as MapPoint;
            if (wgs84 == null) { failures.Add(placed.Symbol.Label + ": its position could not be projected."); continue; }

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(placed.Symbol.DrawnByFieldName) && !string.IsNullOrWhiteSpace(placed.Symbol.Symbol.Value))
            {
                attributes[placed.Symbol.DrawnByFieldName] = placed.Symbol.Symbol.Value;
            }

            try
            {
                var result = await GisSymbolPaletteService.AddPointAsync(
                    placed.Symbol.LayerUrl, wgs84.X, wgs84.Y, 4326, attributes, token);

                if (result.Succeeded) { added++; }
                else { failures.Add(placed.Symbol.Label + ": " + (result.Error ?? "refused without a reason")); }
            }
            catch (Exception ex)
            {
                failures.Add(placed.Symbol.Label + ": " + ex.Message);
            }
        }

        if (failures.Count == 0)
        {
            _placedPaletteFeatures.Clear();
            _placedFeatureOverlay?.Graphics.Clear();
            return added + (added == 1 ? " placed feature was" : " placed features were") + " added to GIS.";
        }

        return added + " of " + (added + failures.Count) + " placed features were added to GIS. "
               + string.Join(" ", failures);
    }
}
