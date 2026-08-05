using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.Security;
using NG.GIS.CAD.Exporter.Auth;
using NG.GIS.CAD.Exporter.ViewModels;

// GlobalUsings.cs pulls in System.Net, which also has an AuthenticationManager.
using AuthenticationManager = Esri.ArcGISRuntime.Security.AuthenticationManager;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Loads the configured portal web map into the extent page (page 2) MapView so the page shows
/// the operational layers the web map was authored with, rather than a bare basemap.
/// </summary>
public partial class ExporterWindow
{
    private const string PortalRootUrl = "https://gis.nationalgrid.com/portal";
    private const string PortalSharingRestUrl = PortalRootUrl + "/sharing/rest";
    private const string ExtentWebMapItemId = "c214d72caefb40699b129bc47b1b22a7";
    private const string MaterialViewLayerName = "Material_View_MA";
    private const string MaterialViewMapServerUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";

    private const int MaxMapLayerDepth = 4;

    private bool _extentWebMapLoaded;
    private readonly Dictionary<string, ILayerContent> _mapLayerContentByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the MapView's map with the portal web map. Graphics overlays are owned by the
    /// MapView rather than by the Map, so the work order buffer and the proposed main imported
    /// from page 1 survive this swap and keep drawing on top of the web map's layers.
    /// </summary>
    private async Task LoadExtentWebMapAsync()
    {
        if (_extentWebMapLoaded || _mapView == null) { return; }

        var token = GetPortalAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            SetExtentMapStatus("Portal web map not loaded: no ArcGIS portal token is available. Re-run NGGIS to sign in.");
            return;
        }

        try
        {
            RegisterPortalCredential(token, ArcGisPortalOAuth.CurrentTokenExpiresUtc);

            var portal = await ArcGISPortal.CreateAsync(new Uri(PortalSharingRestUrl));
            var item = await PortalItem.CreateAsync(portal, ExtentWebMapItemId);
            var map = new Map(item);
            await map.LoadAsync();

            if (map.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
            {
                SetExtentMapStatus("Portal web map did not load: " + (map.LoadError?.Message ?? "unknown error") + ". Keeping basemap only.");
                return;
            }

            _mapView.Map = map;
            _extentWebMapLoaded = true;

            await EnsureMaterialViewLayerAsync(map);
            await ApplyMapDataSourcesAsync();
            ListBaseLayersAsDataSources(map);

            SetExtentMapStatus("Portal web map loaded: " + item.Title + ". Operational layers: " + map.OperationalLayers.Count + ".");
        }
        catch (Exception ex)
        {
            SetExtentMapStatus("Portal web map failed to load: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Lists the layers that came with the map alongside the ones the user added, so the data sources
    /// panel shows everything on the map rather than only the additions.
    ///
    /// These arrive from the portal web map and from Material_View_MA, and they are most of what page 2
    /// actually draws, so a panel that left them out was describing a fraction of the map while looking
    /// like it described all of it. Nobody is expected to turn them off often, but being able to is the
    /// difference between a list you can work with and a list you can only read.
    ///
    /// Kept apart from the profile. Toggling one changes what the map draws for this session; it does not
    /// write a user data source, because these are not the user's to own and would come back next time
    /// from the web map regardless.
    ///
    /// The layers the user's own data sources put on the map are left out. They are on the map by the
    /// time this runs, so listing everything on it gave each of them a second tile that claimed to have
    /// come with the web map. Removing the real tile then left the impostor behind, describing a layer
    /// that was no longer there and offering a tick box that could not put it back.
    /// </summary>
    private void ListBaseLayersAsDataSources(Map map)
    {
        if (DataContext is not ViewModels.ExporterViewModel vm) { return; }

        var fromDataSources = new HashSet<Layer>(_dataSourceLayersByUrl.Values);

        vm.SetBaseMapLayers(map.OperationalLayers
            .Where(layer => !fromDataSources.Contains(layer))
            .Select(layer => new ViewModels.BaseMapLayerHandle(
                string.IsNullOrWhiteSpace(layer.Name) ? "Unnamed layer" : layer.Name,
                layer,
                () => layer.IsVisible,
                visible => layer.IsVisible = visible,
                () => map.OperationalLayers.Remove(layer)))
            .ToList());
    }

    /// <summary>
    /// The web map is the source of truth for page 2's layers. Material_View_MA is only added
    /// separately when the web map does not already contain it, so the previous behaviour is
    /// preserved without duplicating a layer the web map already supplies.
    /// </summary>
    private async Task EnsureMaterialViewLayerAsync(Map map)
    {
        var alreadyPresent = map.OperationalLayers.Any(l =>
            (l.Name ?? string.Empty).Contains(MaterialViewLayerName, StringComparison.OrdinalIgnoreCase));
        if (alreadyPresent) { return; }

        try
        {
            var layer = new ArcGISMapImageLayer(new Uri(MaterialViewMapServerUrl)) { Name = MaterialViewLayerName, IsVisible = true };
            map.OperationalLayers.Add(layer);
            await layer.LoadAsync();

            if (layer.LoadStatus == Esri.ArcGISRuntime.LoadStatus.Loaded)
            {
                foreach (var sublayer in layer.Sublayers) { sublayer.IsVisible = true; }
            }
            else if (layer.LoadError != null)
            {
                map.OperationalLayers.Remove(layer);
                SetExtentMapStatus("Material_View_MA could not be added alongside the web map: " + layer.LoadError.Message);
            }
        }
        catch (Exception ex)
        {
            SetExtentMapStatus("Material_View_MA could not be added alongside the web map: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Builds the page 2 layer toggle list from the loaded map. Every operational layer and every
    /// sublayer beneath it is walked through ILayerContent, which covers group layers and map image
    /// sublayers alike. Visibility saved in the profile wins; layers the profile says nothing about
    /// keep the visibility the web map was authored with.
    /// </summary>
    private async Task BuildMapLayerTogglesAsync(Map map)
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        foreach (var layer in map.OperationalLayers)
        {
            // Sublayers are only populated once the layer itself has loaded.
            try { await layer.LoadAsync(); } catch { }
        }

        foreach (var stale in ExporterViewModel.FlattenMapLayers(vm.MapLayers))
        {
            stale.VisibilityChanged -= OnMapLayerToggled;
        }
        vm.MapLayers.Clear();
        _mapLayerContentByPath.Clear();

        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Counted down, because the last layer in the collection is the one drawn on top and a layer
        // list is read with the top of the list on top. Counted up, the tree ran the opposite way round
        // to the data sources panel beside it, and dragging in one would have appeared to move things
        // the wrong way compared with the other.
        for (var i = map.OperationalLayers.Count - 1; i >= 0; i--)
        {
            var layer = map.OperationalLayers[i];

            // Only the top level gets a way off the map, or a way to be reordered. A sublayer belongs to
            // the service that carries it and cannot be detached from it or moved out of it, so its tick
            // box is the whole of what can be done to one; offering a button that could not work would
            // be worse than not offering it.
            AddMapLayerToggle(vm, layer, null, null, 0, usedPaths, removable: layer);
        }

        // Pages 3 and 4 list the layers on this map, so they are rebuilt from the new tree.
        vm.OnMapLayersChanged();

        // Swatches fill in behind the tree rather than holding it up. See ExporterWindow.MapLegend.cs.
        StartMapLayerLegendLoad(vm);
    }

    /// <summary>
    /// Adds one node for <paramref name="content"/> beneath <paramref name="parent"/>, then recurses
    /// into its sublayers. Content whose visibility cannot be changed gets no node of its own, so its
    /// children are attached to the nearest ancestor that does have one.
    /// </summary>
    /// <summary>
    /// Works out the REST URL for a map layer so pages 3 and 4 can read its fields.
    ///
    /// Sublayers are addressed as the parent service plus their id, which is the ArcGIS REST
    /// convention and avoids depending on a sublayer reporting its own source. Top level layers
    /// carry their service URL directly, and a feature layer's lives on its service table.
    /// </summary>
    private static string? GetLayerServiceUrl(ILayerContent content, string? parentServiceUrl)
    {
        switch (content)
        {
            case ArcGISSublayer sublayer:
                return string.IsNullOrWhiteSpace(parentServiceUrl)
                    ? null
                    : parentServiceUrl.TrimEnd('/') + "/" + sublayer.Id;
            case FeatureLayer featureLayer:
                return (featureLayer.FeatureTable as ServiceFeatureTable)?.Source?.ToString();
            case ArcGISMapImageLayer mapImageLayer:
                return mapImageLayer.Source?.ToString();
            case ArcGISTiledLayer tiledLayer:
                return tiledLayer.Source?.ToString();
            default:
                return null;
        }
    }

    private void AddMapLayerToggle(ExporterViewModel vm, ILayerContent content, MapLayerToggleViewModel? parent, string? parentPath, int depth, HashSet<string> usedPaths, string? parentServiceUrl = null, Layer? removable = null)
    {
        if (content == null || depth > MaxMapLayerDepth) { return; }

        var name = string.IsNullOrWhiteSpace(content.Name) ? "(unnamed layer)" : content.Name;
        var basePath = parentPath == null ? name : parentPath + "/" + name;
        var serviceUrl = GetLayerServiceUrl(content, parentServiceUrl);

        // Two sublayers can share a name, so make the persisted key unique per position.
        var path = basePath;
        var suffix = 2;
        while (!usedPaths.Add(path))
        {
            path = basePath + " (" + suffix + ")";
            suffix++;
        }

        var children = content.SublayerContents;
        var isLeaf = children == null || children.Count == 0;

        var node = parent;
        if (content.CanChangeVisibility)
        {
            var saved = vm.GetSavedMapLayerVisibility(path);
            if (saved.HasValue) { content.IsVisible = saved.Value; }

            var toggle = new MapLayerToggleViewModel(path, name, content.IsVisible, serviceUrl, isLeaf);
            toggle.VisibilityChanged += OnMapLayerToggled;
            if (removable != null)
            {
                toggle.Remove = () => RemoveMapLayerFromMap(removable);

                // The same layer, kept so a drop in the tree can be turned into a move of the tile that
                // stands for it. Reordering goes through the data sources panel rather than touching the
                // map here, so the two lists cannot end up disagreeing about the order.
                toggle.LayerRef = removable;
            }
            _mapLayerContentByPath[path] = content;

            if (parent == null)
            {
                vm.MapLayers.Add(toggle);
            }
            else
            {
                // The link upward is what lets a node tell whether every group above it is visible.
                toggle.Parent = parent;
                parent.Children.Add(toggle);
            }

            node = toggle;
        }

        if (children == null) { return; }

        // Sublayers are addressed off the service root and are flat: ".../MapServer/54", never
        // ".../MapServer/3/54". So a sublayer passes the root it was given straight down rather than
        // its own URL, which for a group sublayer would otherwise nest and give every layer inside
        // that group an address the service does not answer on.
        var childServiceUrl = content is ArcGISSublayer ? parentServiceUrl : serviceUrl ?? parentServiceUrl;
        foreach (var child in children)
        {
            AddMapLayerToggle(vm, child, node, path, depth + 1, usedPaths, childServiceUrl);
        }
    }

    private void ExpandAllMapLayers_Click(object sender, RoutedEventArgs e) => SetAllMapLayersExpanded(true);

    private void CollapseAllMapLayers_Click(object sender, RoutedEventArgs e) => SetAllMapLayersExpanded(false);

    private void SetAllMapLayersExpanded(bool expanded)
    {
        if (DataContext is not ExporterViewModel vm) { return; }
        foreach (var node in ExporterViewModel.FlattenMapLayers(vm.MapLayers))
        {
            node.IsExpanded = expanded;
        }
    }

    private void OnMapLayerToggled(MapLayerToggleViewModel toggle)
    {
        if (_mapLayerContentByPath.TryGetValue(toggle.Path, out var content))
        {
            content.IsVisible = toggle.IsVisible;
        }
        if (DataContext is ExporterViewModel vm)
        {
            _ = vm.SaveMapLayerVisibilityAsync();

            // Toggling a group changes what is drawn for everything beneath it, so the export
            // selection on pages 3 and 4 is brought back in line straight away.
            vm.RefreshLayerSelectionFromMap();
        }
    }

    /// <summary>
    /// Hands the portal OAuth token to the ArcGIS Runtime so every secured service the web map
    /// references authenticates. Registering against the portal root covers the federated
    /// services published under it, which appending a token to a service URL does not: the
    /// Runtime rebuilds its own request URLs for tile and export calls, so a token baked into
    /// the service URI is not carried through to them.
    ///
    /// PregeneratedTokenCredential is the credential type for a token obtained outside the
    /// Runtime, which is what the OAuth flow in ArcGisPortalOAuth produces.
    /// </summary>
    private static void RegisterPortalCredential(string token, DateTime expiresUtc)
    {
        var manager = AuthenticationManager.Current;
        manager.RemoveAllCredentials();

        var expiration = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc));
        var tokenInfo = new TokenInfo(token, expiration, true);
        manager.AddCredential(new PregeneratedTokenCredential(new Uri(PortalRootUrl), tokenInfo, null));
    }

    private static string GetPortalAccessToken() => ArcGisPortalOAuth.CurrentAccessToken ?? string.Empty;

    private void SetExtentMapStatus(string message)
    {
        if (DataContext is ExporterViewModel vm) { vm.Status = message; }
    }

    /// <summary>
    /// Takes one layer off the map for this session.
    ///
    /// A layer added from a data source is unticked as well as removed. Removing it on its own would
    /// last until the next reconcile, which reads the tile list, finds the source still enabled and
    /// puts the layer straight back -- so the button would look broken rather than undone. Unticking
    /// it leaves the source in the list to be turned on again, which is the recoverable version of
    /// what was asked for.
    ///
    /// A layer the web map brought with it has nothing behind it to untick, so it goes for this
    /// session and returns when the map next loads. That is the same bargain the data source panel
    /// already offers for the map's own layers, and it is said rather than implied.
    /// </summary>
    private async void RemoveMapLayerFromMap(Layer layer)
    {
        if (_mapView?.Map is not Map map) { return; }
        if (DataContext is not ExporterViewModel vm) { return; }

        // Two names for two jobs. The sentence wants something readable; the tile is found by the name
        // it was built with, which is the same fallback ListBaseLayersAsDataSources uses. Using the
        // readable one to find the tile would miss an unnamed layer's tile entirely.
        var name = string.IsNullOrWhiteSpace(layer.Name) ? "That layer" : layer.Name;
        var tileName = string.IsNullOrWhiteSpace(layer.Name) ? "Unnamed layer" : layer.Name;

        var url = _dataSourceLayersByUrl
            .FirstOrDefault(pair => ReferenceEquals(pair.Value, layer)).Key;

        if (!string.IsNullOrEmpty(url))
        {
            var source = vm.MapDataSources.FirstOrDefault(s =>
                string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));

            if (source != null)
            {
                // Unticking it removes the layer through the ordinary reconcile, so there is no need
                // to take it off here as well and no chance of the two disagreeing.
                source.Enabled = false;
                vm.Status = name + " has been turned off. Its data source is still in the list, so it "
                    + "can be turned back on there.";
                return;
            }
        }

        map.OperationalLayers.Remove(layer);

        // The data sources panel lists the map's own layers alongside the profile's, so its tile goes
        // too. Just the one: relisting them all rebuilds every map owned tile at the bottom of the
        // panel and would throw away an order the user had dragged into place.
        vm.RemoveBaseMapLayerTile(tileName);
        await BuildMapLayerTogglesAsync(map);
        vm.Status = name + " has been taken off the map for this session. It came with the web map "
            + "rather than from the profile, so it will be back the next time the map loads.";
    }

    /// <summary>
    /// The Remove button on a layer in the tree. The node carries what to do rather than the view
    /// working it out from the tree, because the node is the only thing that knows which layer it was
    /// built from once the tree has been flattened into paths.
    /// </summary>
    private void RemoveMapLayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MapLayerToggleViewModel node)
        {
            node.Remove?.Invoke();
        }
    }
}
