using System;
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

            SetExtentMapStatus("Portal web map loaded: " + item.Title + ". Operational layers: " + map.OperationalLayers.Count + ".");
        }
        catch (Exception ex)
        {
            SetExtentMapStatus("Portal web map failed to load: " + ex.GetType().Name + ": " + ex.Message);
        }
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
        foreach (var layer in map.OperationalLayers)
        {
            AddMapLayerToggle(vm, layer, null, null, 0, usedPaths);
        }

        // Pages 3 and 4 list the layers on this map, so they are rebuilt from the new tree.
        vm.OnMapLayersChanged();
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

    private void AddMapLayerToggle(ExporterViewModel vm, ILayerContent content, MapLayerToggleViewModel? parent, string? parentPath, int depth, HashSet<string> usedPaths, string? parentServiceUrl = null)
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
}
