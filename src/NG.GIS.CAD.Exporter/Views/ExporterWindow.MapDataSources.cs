using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Mapping;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Applies the user's extra data sources to the extent page map. These sit on top of the layers the
/// portal web map supplies and are added and removed without reloading the web map itself.
/// </summary>
public partial class ExporterWindow
{
    private static readonly Regex ServiceLayerIndexPattern =
        new(@"/(MapServer|FeatureServer)/\d+/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, Layer> _dataSourceLayersByUrl = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles the map's data source layers with the enabled entries in the profile, then rebuilds
    /// the layer tree so the new layers show up as toggles.
    /// </summary>
    private async Task ApplyMapDataSourcesAsync()
    {
        if (_mapView?.Map is not Map map) { return; }
        if (DataContext is not ExporterViewModel vm) { return; }

        var enabled = vm.MapDataSources.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        var wanted = new HashSet<string>(enabled.Select(s => s.Url), StringComparer.OrdinalIgnoreCase);

        foreach (var url in _dataSourceLayersByUrl.Keys.ToList())
        {
            if (wanted.Contains(url)) { continue; }
            if (_dataSourceLayersByUrl.TryGetValue(url, out var stale)) { map.OperationalLayers.Remove(stale); }
            _dataSourceLayersByUrl.Remove(url);
        }

        foreach (var source in enabled)
        {
            if (_dataSourceLayersByUrl.ContainsKey(source.Url)) { continue; }
            await AddMapDataSourceLayerAsync(map, source);
        }

        ReapplyMapDataSourceDrawOrder(map);
        await BuildMapLayerTogglesAsync(map);
    }

    /// <summary>
    /// Puts the data source layers on the map in the order the tiles are in, top of the list drawn on
    /// top, which is how a layer list is read everywhere else.
    ///
    /// Every one is lifted off and put back rather than shuffled in place. The map's own layers are in
    /// the same collection, and moving one data source past another without disturbing those means
    /// working out indices around layers that are none of this list's business. Taking them all off
    /// and re-adding them settles both questions at once: they end up in list order, and they end up
    /// above the web map's layers, which is where something a user added on purpose belongs.
    /// </summary>
    private void ReapplyMapDataSourceDrawOrder(Map map)
    {
        if (DataContext is not ExporterViewModel vm) { return; }
        if (_dataSourceLayersByUrl.Count == 0) { return; }

        foreach (var layer in _dataSourceLayersByUrl.Values)
        {
            map.OperationalLayers.Remove(layer);
        }

        // Reversed, because adding appends and the last one added draws on top.
        for (var i = vm.MapDataSources.Count - 1; i >= 0; i--)
        {
            var source = vm.MapDataSources[i];
            if (string.IsNullOrWhiteSpace(source.Url)) { continue; }
            if (_dataSourceLayersByUrl.TryGetValue(source.Url, out var layer))
            {
                map.OperationalLayers.Add(layer);
            }
        }
    }

    private async Task AddMapDataSourceLayerAsync(Map map, MapDataSourceViewModel source)
    {
        var layer = CreateLayerForDataSource(source.Url);
        if (layer == null)
        {
            source.Status = "Unsupported service type. Use a MapServer, FeatureServer layer, VectorTileServer or KML URL.";
            return;
        }

        layer.Name = source.Name;
        map.OperationalLayers.Add(layer);
        await layer.LoadAsync();

        // A MapServer with only a cached tile fused map does not serve export images, so an image
        // layer over it loads but never draws. Retry those as a tiled layer.
        if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded && layer is ArcGISMapImageLayer)
        {
            map.OperationalLayers.Remove(layer);
            var tiled = new ArcGISTiledLayer(new Uri(source.Url)) { Name = source.Name };
            map.OperationalLayers.Add(tiled);
            await tiled.LoadAsync();
            layer = tiled;
        }

        if (layer.LoadStatus == Esri.ArcGISRuntime.LoadStatus.Loaded)
        {
            _dataSourceLayersByUrl[source.Url] = layer;
            source.Status = "Loaded.";
        }
        else
        {
            map.OperationalLayers.Remove(layer);
            source.Status = "Failed: " + (layer.LoadError?.Message ?? "unknown error");
        }
    }

    /// <summary>
    /// Picks a layer type from the shape of the service URL. Returns null when the URL is not a form
    /// this build knows how to add.
    /// </summary>
    private static Layer? CreateLayerForDataSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { return null; }

        var trimmed = url.TrimEnd('/');

        // A specific layer index under either server type is a feature layer.
        if (ServiceLayerIndexPattern.IsMatch(url)) { return new FeatureLayer(uri); }

        if (trimmed.EndsWith("/VectorTileServer", StringComparison.OrdinalIgnoreCase)) { return new ArcGISVectorTiledLayer(uri); }
        if (trimmed.EndsWith("/MapServer", StringComparison.OrdinalIgnoreCase)) { return new ArcGISMapImageLayer(uri); }
        if (trimmed.EndsWith(".kml", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase)) { return new KmlLayer(uri); }

        // A FeatureServer root is a collection of layers rather than one layer, and an ImageServer
        // needs a raster wrapper this build does not set up. Both are reported to the user instead.
        return null;
    }
}
