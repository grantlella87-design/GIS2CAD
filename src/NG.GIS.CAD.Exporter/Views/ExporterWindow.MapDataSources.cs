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
    /// Puts every layer on the map in the order its tile is in, top of the list drawn on top, which is
    /// how a layer list is read everywhere else.
    ///
    /// The whole collection, not only the layers the data sources added. The tiles list the web map's
    /// layers too, and ordering one kind while leaving the other where it was is not an order at all:
    /// a source could be moved above another source and still be drawn under everything the web map
    /// brought with it.
    ///
    /// Anything on the map that no tile speaks for is left at the bottom rather than dropped, so a
    /// layer added by some other part of the page keeps its place on it.
    /// </summary>
    private void ReapplyMapDataSourceDrawOrder(Map map)
    {
        if (DataContext is not ExporterViewModel vm) { return; }
        if (vm.MapDataSources.Count == 0) { return; }

        // Top tile first, so this reads in the same direction as the panel does.
        var byTile = new List<Layer>();
        foreach (var source in vm.MapDataSources)
        {
            var layer = ResolveLayerForDataSource(source);
            if (layer != null && !byTile.Contains(layer)) { byTile.Add(layer); }
        }

        if (byTile.Count == 0) { return; }

        var spokenFor = new HashSet<Layer>(byTile);
        var ordered = map.OperationalLayers.Where(l => !spokenFor.Contains(l)).ToList();

        // Reversed onto the end, because the last layer in the collection is the one drawn on top.
        for (var i = byTile.Count - 1; i >= 0; i--) { ordered.Add(byTile[i]); }

        if (ordered.SequenceEqual(map.OperationalLayers)) { return; }

        map.OperationalLayers.Clear();
        foreach (var layer in ordered) { map.OperationalLayers.Add(layer); }
    }

    /// <summary>
    /// The layer one tile stands for: looked up by URL for a profile source, and carried on the tile
    /// itself for a layer the map brought with it.
    /// </summary>
    private Layer? ResolveLayerForDataSource(MapDataSourceViewModel source)
    {
        if (source.IsFromMap) { return source.MapLayerRef as Layer; }

        if (string.IsNullOrWhiteSpace(source.Url)) { return null; }
        return _dataSourceLayersByUrl.TryGetValue(source.Url, out var layer) ? layer : null;
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
