using NG.GIS.CAD.Exporter.Models;
using NG.GIS.CAD.Exporter.Services;
namespace NG.GIS.CAD.Exporter.ViewModels;
public sealed partial class ExporterViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _status = "Choose an export method.";
    private string _profilePath = string.Empty;
    private int _pageIndex;
    private string _workOrderId = string.Empty;
    private double _paddingFeet = 300;
    private double _manualXMin;
    private double _manualYMin;
    private double _manualXMax;
    private double _manualYMax;
    private int _extentWkid = 2249;
    private ExportMethod _selectedMethod = ExportMethod.WorkOrder;
    private ExportExtent? _resolvedExtent;
    private LayerSelectionViewModel? _selectedLayer;
    private CadTransformRuleViewModel? _selectedTransform;
    private ExportProfile _profile = new();
    private string _newMapDataSourceName = string.Empty;
    private string _newMapDataSourceUrl = string.Empty;
    private bool _layerMetadataLoaded;
    private bool _cadCatalogLoaded;

    /// <summary>
    /// Every layer the page 2 map can export, each kept beside the map node it came from, whether or
    /// not that node is currently shown. Pages 3 and 4 are filtered views of this list.
    ///
    /// Metadata is fetched for hidden layers too, so ticking a group back on is instant rather than
    /// another round of network calls, and so the field choices and transform rules made against a
    /// layer survive it being hidden and shown again.
    /// </summary>
    private readonly List<MapBackedLayer> _mapBackedLayers = new();
    private string? _templatePath;
    private bool _useTemplateSymbols;
    public ExporterViewModel(AppServices services)
    {
        _services = services;
        ProfilePath = _services.ProfileStore.GetDefaultProfilePath();
        Layers = new ObservableCollection<LayerSelectionViewModel>();
        MapLayers = new ObservableCollection<MapLayerToggleViewModel>();
        MapDataSources = new ObservableCollection<MapDataSourceViewModel>();
        TransformRules = new ObservableCollection<CadTransformRuleViewModel>();
        WorkOrderOptions = new ObservableCollection<string>();
        // Filled when the CAD transformation page is first shown. Reading them here meant three
        // drawing database transactions on the UI thread before the window appeared, delaying the
        // work order lookup for values that are only used on page 4.
        Blocks = new ObservableCollection<string>();
        LineTypes = new ObservableCollection<string>();
        CadLayers = new ObservableCollection<string>();
        NextCommand = new RelayCommand(_ => NextAsync());
        BackCommand = new RelayCommand(_ => BackAsync());
        LoadProfileCommand = new RelayCommand(_ => LoadProfileAsync());
        SaveSettingsCommand = new RelayCommand(_ => SaveSettingsAsync());
        ResolveWorkOrderExtentCommand = new RelayCommand(_ => ResolveWorkOrderExtentAsync());
        LoadWorkOrdersCommand = new RelayCommand(_ => LoadWorkOrdersAsync());
        DrawPipelineRouteCommand = new RelayCommand(_ => DrawPipelineRouteAsync());
        UseCurrentViewExtentCommand = new RelayCommand(_ => UseCurrentViewExtentAsync());
        SetManualExtentCommand = new RelayCommand(_ => SetManualExtentAsync());
        BuildReviewCommand = new RelayCommand(_ => BuildReviewAsync());
        // Export now writes into the drawing. The dry-run plan stays on its own command, because
        // seeing what would be written without writing it is still worth having.
        ExportCommand = new RelayCommand(_ => ExportToCadAsync());
        AddMapDataSourceCommand = new RelayCommand(_ => AddMapDataSourceAsync());
        RemoveMapDataSourceCommand = new RelayCommand(RemoveMapDataSourceAsync);
        // LoadWorkOrdersAsync is deliberately not started here. It queries work order IDs from the
        // ArcGIS proposed main layer into WorkOrderOptions, which nothing binds to: the dropdowns on
        // page 1 are filled from NG_ODS instead. Running it at startup spent a network round trip on
        // a result that is never shown and overwrote Status while the NG_ODS load was reporting
        // progress. It stays available behind LoadWorkOrdersCommand.
        _ = LoadProfileAsync();
    }
    public ObservableCollection<LayerSelectionViewModel> Layers { get; }
    public ObservableCollection<MapLayerToggleViewModel> MapLayers { get; }
    public ObservableCollection<MapDataSourceViewModel> MapDataSources { get; }
    public ObservableCollection<CadTransformRuleViewModel> TransformRules { get; }
    public ObservableCollection<string> WorkOrderOptions { get; }
    public ObservableCollection<string> Blocks { get; }
    public ObservableCollection<string> LineTypes { get; }
    public ObservableCollection<string> CadLayers { get; }
    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand LoadProfileCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResolveWorkOrderExtentCommand { get; }
    public ICommand LoadWorkOrdersCommand { get; }
    public ICommand DrawPipelineRouteCommand { get; }
    public ICommand UseCurrentViewExtentCommand { get; }
    public ICommand SetManualExtentCommand { get; }
    public ICommand BuildReviewCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand AddMapDataSourceCommand { get; }
    public ICommand RemoveMapDataSourceCommand { get; }
    /// <summary>Where the SharePoint template browser looks, taken from the loaded profile.</summary>
    public SharePointTemplateSettings SharePointTemplateSettings => _profile.SharePointTemplates ??= new SharePointTemplateSettings();
    public string NewMapDataSourceName { get => _newMapDataSourceName; set => SetProperty(ref _newMapDataSourceName, value); }
    public string NewMapDataSourceUrl { get => _newMapDataSourceUrl; set => SetProperty(ref _newMapDataSourceUrl, value); }

    /// <summary>
    /// Raised when a source is added, removed or toggled. The view reapplies the data source layers
    /// to the extent page map and rebuilds the layer tree.
    /// </summary>
    public event Action? MapDataSourcesChanged;
    public bool IsMethodPage => PageIndex == 0;
    public bool IsExtentPage => PageIndex == 1;
    public bool IsLayerPage => PageIndex == 2;
    public bool IsTransformPage => PageIndex == 3;
    public bool IsReviewPage => PageIndex == 4;
    public string PageTitle => PageIndex switch { 0 => "1. Export Method", 1 => "2. Extent", 2 => "3. Layers + Fields", 3 => "4. CAD Transformation", _ => "5. Review + Export" };
    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            if (!SetProperty(ref _pageIndex, value)) { return; }
            RaisePageFlags();

            // Work that only matters from a given page is done on arrival at it, so none of it
            // competes with the work order query while page 1 is on screen.
            if (IsLayerPage) { _ = EnsureLayerMetadataLoadedAsync(); }
            if (IsTransformPage) { EnsureCadCatalogLoaded(); }

            // The map may have been re-toggled since these pages were built, so the selection is
            // brought back in line on arrival.
            if (IsLayerPage || IsTransformPage || IsReviewPage) { RefreshLayerSelectionFromMap(); }
        }
    }
    public string ProfilePath { get => _profilePath; set => SetProperty(ref _profilePath, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public ExportMethod SelectedMethod { get => _selectedMethod; set { if (SetProperty(ref _selectedMethod, value)) { RaiseMethodFlags(); } } }
    public bool IsWorkOrderMethod { get => SelectedMethod == ExportMethod.WorkOrder; set { if (value) { SelectedMethod = ExportMethod.WorkOrder; } } }
    public bool IsDrawPipelineRouteMethod { get => SelectedMethod == ExportMethod.DrawPipelineRoute; set { if (value) { SelectedMethod = ExportMethod.DrawPipelineRoute; } } }
    public bool IsCurrentDrawingViewMethod { get => SelectedMethod == ExportMethod.CurrentDrawingView; set { if (value) { SelectedMethod = ExportMethod.CurrentDrawingView; } } }
    public bool IsManualMethod { get => SelectedMethod == ExportMethod.ManualExtent; set { if (value) { SelectedMethod = ExportMethod.ManualExtent; } } }
    public string WorkOrderId { get => _workOrderId; set => SetProperty(ref _workOrderId, value); }
    public double PaddingFeet
    {
        get => _paddingFeet;
        // The padding is the buffer's width, so the review page's account of the scope moves with it.
        set { if (SetProperty(ref _paddingFeet, value)) { RaisePropertyChanged(nameof(BoundaryDescription)); } }
    }
    public double ManualXMin { get => _manualXMin; set => SetProperty(ref _manualXMin, value); }
    public double ManualYMin { get => _manualYMin; set => SetProperty(ref _manualYMin, value); }
    public double ManualXMax { get => _manualXMax; set => SetProperty(ref _manualXMax, value); }
    public double ManualYMax { get => _manualYMax; set => SetProperty(ref _manualYMax, value); }
    public int ExtentWkid { get => _extentWkid; set => SetProperty(ref _extentWkid, value); }
    public string ResolvedExtentText => _resolvedExtent == null
        ? "No extent set."
        : $"{_resolvedExtent.Mode}: {_resolvedExtent.XMin:0.###}, {_resolvedExtent.YMin:0.###}, {_resolvedExtent.XMax:0.###}, {_resolvedExtent.YMax:0.###}"
          + $"\nCoordinates in {SpatialReferenceNames.Describe(_resolvedExtent.Wkid)}";

    /// <summary>
    /// The system the drawing's coordinates come out in. Shown by name, because the WKID alone says
    /// nothing to most people and this is the setting that decides where the features land.
    /// </summary>
    public string OutputSpatialReferenceText =>
        SpatialReferenceNames.Describe(_profile.DefaultOutputSpatialReferenceWkid);
    public LayerSelectionViewModel? SelectedLayer { get => _selectedLayer; set => SetProperty(ref _selectedLayer, value); }
    public CadTransformRuleViewModel? SelectedTransform { get => _selectedTransform; set => SetProperty(ref _selectedTransform, value); }
    private Task NextAsync() { if (PageIndex < 4) { PageIndex++; } return Task.CompletedTask; }
    private Task BackAsync() { if (PageIndex > 0) { PageIndex--; } return Task.CompletedTask; }
    /// <summary>
    /// Reads the profile file and the map data sources. This is the only part needed before the user
    /// picks a work order, so it is all that runs at startup. The per-service layer metadata, which
    /// is one network call per enabled service, is fetched when the layers page is first shown.
    /// </summary>
    private async Task LoadProfileAsync()
    {
        try
        {
            var profile = await _services.ProfileStore.LoadAsync(ProfilePath, CancellationToken.None);
            _profile = profile;
            LoadMapDataSources();
            LoadStripMapSettings();

            // Deliberately no success status here: the work order lookup owns the status line while
            // page 1 is on screen, and overwriting it would hide the lookup's own progress.
            _layerMetadataLoaded = false;
            _mapBackedLayers.Clear();
            Layers.Clear();
            TransformRules.Clear();

            // Reloading the profile from a page that shows layers must refill them straight away,
            // rather than leaving that page empty until it is navigated to again.
            if (IsLayerPage || IsTransformPage || IsReviewPage) { await EnsureLayerMetadataLoadedAsync(); }
        }
        catch (Exception ex) { Status = "Profile load failed: " + ex.Message; }
    }

    /// <summary>
    /// Loads everything pages 3 and 4 need. Called once the work order query has finished, so page 1
    /// gets the network and the UI thread first without leaving the later pages empty on arrival.
    /// Runs quietly, because the status line belongs to the work order lookup at that point.
    /// </summary>
    public async Task PreloadLaterPagesAsync()
    {
        await EnsureLayerMetadataLoadedAsync(announce: false);
        EnsureCadCatalogLoaded();
    }

    /// <summary>Fetches layer and field metadata for every enabled service, once.</summary>
    private async Task EnsureLayerMetadataLoadedAsync(bool announce = true)
    {
        if (_layerMetadataLoaded) { return; }
        _layerMetadataLoaded = true;
        try
        {
            if (announce) { Status = "Loading GIS layer metadata..."; }
            var userSettings = await _services.UserSettingsStore.LoadAsync(CancellationToken.None);
            _mapBackedLayers.Clear();
            Layers.Clear();
            TransformRules.Clear();

            // Only leaves are used. A parent's URL is the whole service, which would pull in every
            // sublayer already listed beneath it. The same service can also back more than one node,
            // so URLs are de-duplicated.
            var mapLayers = FlattenMapLayers(MapLayers)
                .Where(l => l.IsLeaf && !string.IsNullOrWhiteSpace(l.ServiceUrl))
                .GroupBy(l => l.ServiceUrl!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (mapLayers.Count == 0)
            {
                Status = "No exportable layers on the page 2 map yet. Layers appear here once the map has loaded.";
                _layerMetadataLoaded = false;
                return;
            }

            foreach (var mapLayer in mapLayers)
            {
                var metadata = await _services.ArcGisRestClient.LoadServiceLayersAsync(mapLayer.ServiceUrl!, CancellationToken.None);
                foreach (var layer in metadata)
                {
                    var state = new LayerSelectionViewState(layer);

                    // A layer only reaches these pages when the map is showing it, so it starts
                    // ticked for export. A choice this user saved earlier still wins over that.
                    state.Enabled = true;
                    ApplySavedSettings(state, userSettings);

                    _mapBackedLayers.Add(new MapBackedLayer(
                        mapLayer,
                        new LayerSelectionViewModel(state),
                        new CadTransformRuleViewModel(new CadTransformRule { LayerUrl = layer.Url, LayerName = layer.Name, GeometryType = layer.GeometryType, CadLayerName = layer.Name.Replace(" ", "_"), LineType = "ByLayer", ColorMode = "ByLayer" })));
                }
            }

            RefreshLayerSelectionFromMap();
            if (announce)
            {
                var hidden = _mapBackedLayers.Count - Layers.Count;
                Status = hidden > 0
                    ? $"Loaded {Layers.Count} layers from the page 2 map. {hidden} more are hidden there, so they are not listed here."
                    : $"Loaded {Layers.Count} layers from the page 2 map.";
            }
        }
        catch (Exception ex)
        {
            // Failures are always reported, even on the quiet startup pass.
            _layerMetadataLoaded = false;
            Status = "Layer metadata load failed: " + ex.Message;
        }
    }

    /// <summary>Reads block, line type and layer names from the chosen source, once.</summary>
    private void EnsureCadCatalogLoaded()
    {
        if (_cadCatalogLoaded) { return; }
        _cadCatalogLoaded = true;
        ReloadCadCatalog();
    }

    /// <summary>
    /// Refills the block, line type and layer lists from whichever source is selected: the drawing
    /// that is open, or the template picked on page 1.
    /// </summary>
    public void ReloadCadCatalog()
    {
        try
        {
            var source = UseTemplateSymbols ? TemplatePath : null;

            // Read before clearing, so a failure leaves the previous lists in place rather than
            // emptying the page and then reporting why.
            var catalog = _services.CadDrawingCatalog.Read(source);

            Blocks.Clear();
            LineTypes.Clear();
            CadLayers.Clear();

            foreach (var name in catalog.Blocks) { Blocks.Add(name); }
            foreach (var name in catalog.LineTypes) { LineTypes.Add(name); }
            foreach (var name in catalog.Layers) { CadLayers.Add(name); }
            LoadStripMapViewports(catalog.Viewports);

            Status = UseTemplateSymbols
                ? $"Read {Blocks.Count} blocks and {LineTypes.Count} line types from {System.IO.Path.GetFileName(TemplatePath)}."
                : $"Read {Blocks.Count} blocks and {LineTypes.Count} line types from the open drawing.";
        }
        catch (Exception ex)
        {
            // Name the source and the exception type. "External component has thrown an exception"
            // on its own says nothing about which drawing was being read or why.
            _cadCatalogLoaded = false;
            var source = UseTemplateSymbols
                ? "the CAD template " + System.IO.Path.GetFileName(TemplatePath)
                : "the open drawing";
            Status = "Reading blocks, line types and layers from " + source + " failed: "
                + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// The .dwt chosen on page 1. Setting it makes the template a usable symbol source; clearing it
    /// falls the page back to the open drawing.
    /// </summary>
    public string? TemplatePath
    {
        get => _templatePath;
        set
        {
            if (!SetProperty(ref _templatePath, value)) { return; }
            RaisePropertyChanged(nameof(HasTemplate));

            // Picking a template is a statement of intent, so its symbols become the source straight
            // away. Leaving the lists on the open drawing until a radio button is found on page 4
            // makes the template look like it did nothing.
            if (!HasTemplate)
            {
                if (UseTemplateSymbols) { UseTemplateSymbols = false; }
                return;
            }
            if (!UseTemplateSymbols) { UseTemplateSymbols = true; }
            else { ReloadCadCatalog(); }
        }
    }

    public bool HasTemplate => !string.IsNullOrWhiteSpace(TemplatePath);

    /// <summary>Whether blocks and line types are read from the template rather than the open drawing.</summary>
    public bool UseTemplateSymbols
    {
        get => _useTemplateSymbols;
        set
        {
            if (!SetProperty(ref _useTemplateSymbols, value)) { return; }
            RaisePropertyChanged(nameof(UseDrawingSymbols));
            ReloadCadCatalog();
        }
    }

    public bool UseDrawingSymbols
    {
        get => !UseTemplateSymbols;
        set { if (value) { UseTemplateSymbols = false; } }
    }

    /// <summary>
    /// Whether a block of this name exists in the drawing that is open. The block editor works on the
    /// open drawing, so a name taken from a template is not necessarily editable.
    /// </summary>
    public bool BlockExistsInOpenDrawing(string blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName)) { return false; }
        try
        {
            return _services.CadDrawingCatalog.GetBlockNames(null)
                .Contains(blockName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private async Task LoadWorkOrdersAsync()
    {
        try
        {
            Status = "Loading work orders from proposed main layer...";
            var proposedLayer = "https" + "://" + "gis.nationalgrid.com" + "/arcgis/rest/services/MA/Material_View_MA/MapServer/54";
            var workOrders = await _services.ArcGisRestClient.QueryDistinctWorkOrderIdsAsync(proposedLayer, CancellationToken.None);
            WorkOrderOptions.Clear();
            foreach (var workOrder in workOrders) { WorkOrderOptions.Add(workOrder); }
            Status = $"Loaded {WorkOrderOptions.Count} work order IDs from proposed main layer.";
        }
        catch (Exception ex) { Status = "Load work orders failed: " + ex.Message; }
    }
    private async Task SaveSettingsAsync()
    {
        // Layer selections are only written once the metadata behind them has loaded. Saving from
        // page 1, before the layers page has ever been shown, would otherwise persist an empty set
        // over the field choices already stored for this user.
        if (_layerMetadataLoaded)
        {
            // Saved from every layer the map can export, not just the ones it is showing. Saving from
            // the visible list would drop the field choices made against a layer that happens to be
            // hidden right now, the next time settings are written.
            await _services.ExportCoordinator.SaveSelectionsAsync(
                _mapBackedLayers.Select(b => b.Layer.State), ProfilePath, CancellationToken.None);
        }
        await SaveMapLayerVisibilityAsync();
        Status = "Settings saved.";
    }

    /// <summary>
    /// Visibility saved in the profile for a map layer path, or null when the profile says nothing
    /// about it and the web map's own authored visibility should win.
    /// </summary>
    public bool? GetSavedMapLayerVisibility(string path)
    {
        // An explicit "mapLayerVisibility": null in the profile deserializes to a null dictionary.
        _profile.MapLayerVisibility ??= new(StringComparer.OrdinalIgnoreCase);
        return _profile.MapLayerVisibility.TryGetValue(path, out var visible) ? visible : null;
    }

    /// <summary>
    /// Writes the current state of every map layer toggle into the profile on disk. Called when a
    /// checkbox changes so the choice survives a restart.
    /// </summary>
    public async Task SaveMapLayerVisibilityAsync()
    {
        if (MapLayers.Count == 0) { return; }
        try
        {
            _profile.MapLayerVisibility ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in FlattenMapLayers(MapLayers))
            {
                _profile.MapLayerVisibility[layer.Path] = layer.IsVisible;
            }
            await _services.ProfileStore.SaveAsync(_profile, ProfilePath, CancellationToken.None);
        }
        catch (Exception ex) { Status = "Saving layer visibility failed: " + ex.Message; }
    }

    /// <summary>Rebuilds the data source list from the loaded profile.</summary>
    private void LoadMapDataSources()
    {
        foreach (var stale in MapDataSources) { stale.EnabledChanged -= OnMapDataSourceEnabledChanged; }
        MapDataSources.Clear();

        _profile.MapDataSources ??= new List<MapDataSource>();
        foreach (var source in _profile.MapDataSources)
        {
            var sourceVm = new MapDataSourceViewModel(source);
            sourceVm.EnabledChanged += OnMapDataSourceEnabledChanged;
            MapDataSources.Add(sourceVm);
        }

        // The profile and the map load independently. If the map got there first it has already run
        // with an empty list, so signal it to reconcile now that the sources are known.
        MapDataSourcesChanged?.Invoke();
    }

    private void OnMapDataSourceEnabledChanged(MapDataSourceViewModel source)
    {
        _ = SaveMapDataSourcesAsync();
        MapDataSourcesChanged?.Invoke();
    }

    private async Task AddMapDataSourceAsync()
    {
        var url = (NewMapDataSourceUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Status = "Enter a service URL to add as a data source.";
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            Status = "That data source URL is not a valid absolute URL.";
            return;
        }
        if (MapDataSources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "That data source is already in the list.";
            return;
        }

        var name = (NewMapDataSourceName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) { name = DeriveMapDataSourceName(url); }

        var source = new MapDataSource { Name = name, Url = url, Enabled = true };
        _profile.MapDataSources ??= new List<MapDataSource>();
        _profile.MapDataSources.Add(source);

        var sourceVm = new MapDataSourceViewModel(source);
        sourceVm.EnabledChanged += OnMapDataSourceEnabledChanged;
        MapDataSources.Add(sourceVm);

        NewMapDataSourceName = string.Empty;
        NewMapDataSourceUrl = string.Empty;

        await SaveMapDataSourcesAsync();
        MapDataSourcesChanged?.Invoke();
        Status = "Added data source: " + name + ".";
    }

    private async Task RemoveMapDataSourceAsync(object? parameter)
    {
        if (parameter is not MapDataSourceViewModel sourceVm) { return; }

        sourceVm.EnabledChanged -= OnMapDataSourceEnabledChanged;
        MapDataSources.Remove(sourceVm);
        _profile.MapDataSources?.Remove(sourceVm.Source);

        await SaveMapDataSourcesAsync();
        MapDataSourcesChanged?.Invoke();
        Status = "Removed data source: " + sourceVm.Name + ".";
    }

    /// <summary>Uses the service name segment of the URL when the user did not supply a name.</summary>
    private static string DeriveMapDataSourceName(string url)
    {
        var segments = url.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];
            if (segment.EndsWith("Server", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (int.TryParse(segment, out _)) { continue; }
            return segment;
        }
        return url;
    }

    public Task SaveMapDataSourcesAsync() => SaveProfileAsync();

    /// <summary>Writes the whole profile back to disk.</summary>
    public async Task SaveProfileAsync()
    {
        try
        {
            await _services.ProfileStore.SaveAsync(_profile, ProfilePath, CancellationToken.None);
        }
        catch (Exception ex) { Status = "Saving the profile failed: " + ex.Message; }
    }

    /// <summary>
    /// Rebuilds pages 3 and 4 to hold exactly the layers the page 2 map is showing. Called when a
    /// layer is toggled on page 2 and when one of the later pages is opened, because those pages are
    /// built once and would otherwise keep describing whatever the map looked like at that moment.
    ///
    /// A hidden layer is removed from both pages rather than merely unticked on page 3. Page 4 has no
    /// tick at all, so unticking was invisible there, and a rule for a layer that is not on the map
    /// is a rule for a layer that will not be exported.
    ///
    /// The view models themselves are kept and re-added, never rebuilt, so field choices and
    /// transform rules the user has edited survive a layer being hidden and shown again.
    /// </summary>
    public void RefreshLayerSelectionFromMap()
    {
        if (_mapBackedLayers.Count == 0) { return; }

        var previousLayer = SelectedLayer;
        var previousTransform = SelectedTransform;

        Layers.Clear();
        TransformRules.Clear();
        foreach (var backed in _mapBackedLayers)
        {
            // Effective visibility, so a layer inside an unticked group counts as hidden even though
            // its own box is still ticked.
            if (!backed.MapNode.IsEffectivelyVisible) { continue; }
            Layers.Add(backed.Layer);
            TransformRules.Add(backed.Rule);
        }

        // Keep whatever was being edited selected, as long as it is still on the map.
        SelectedLayer = previousLayer != null && Layers.Contains(previousLayer) ? previousLayer : Layers.FirstOrDefault();
        SelectedTransform = previousTransform != null && TransformRules.Contains(previousTransform) ? previousTransform : TransformRules.FirstOrDefault();
    }

    /// <summary>One exportable layer, tied to the page 2 map node that decides whether it is shown.</summary>
    private sealed record MapBackedLayer(
        MapLayerToggleViewModel MapNode,
        LayerSelectionViewModel Layer,
        CadTransformRuleViewModel Rule);

    /// <summary>
    /// Called once the page 2 map's layer tree has been rebuilt. Pages 3 and 4 are derived from that
    /// tree, so their contents are discarded and rebuilt on next use rather than describing a map
    /// that no longer exists.
    /// </summary>
    public void OnMapLayersChanged()
    {
        _layerMetadataLoaded = false;
        _mapBackedLayers.Clear();
        Layers.Clear();
        TransformRules.Clear();
        SelectedLayer = null;
        SelectedTransform = null;

        // Rebuild straight away when one of those pages is on screen, otherwise leave it until the
        // user gets there.
        if (IsLayerPage || IsTransformPage || IsReviewPage) { _ = EnsureLayerMetadataLoadedAsync(); }
    }

    /// <summary>Walks the map layer tree depth first so every node is persisted, not just the roots.</summary>
    public static IEnumerable<MapLayerToggleViewModel> FlattenMapLayers(IEnumerable<MapLayerToggleViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenMapLayers(node.Children))
            {
                yield return child;
            }
        }
    }
    private async Task ResolveWorkOrderExtentAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(WorkOrderId)) { Status = "Enter or select a work order ID first."; return; }
            var proposedLayer = "https" + "://" + "gis.nationalgrid.com" + "/arcgis/rest/services/MA/Material_View_MA/MapServer/54";
            _resolvedExtent = await _services.ArcGisRestClient.ResolveWorkOrderExtentAsync(proposedLayer, WorkOrderId.Trim(), PaddingFeet, ExtentWkid, CancellationToken.None);
            RaisePropertyChanged(nameof(ResolvedExtentText));
            Status = "Work order extent resolved and bounding box is ready.";
        }
        catch (Exception ex) { Status = "Resolve extent failed: " + ex.Message; }
    }
    private Task DrawPipelineRouteAsync()
    {
        try { Status = "Switch to AutoCAD and pick route points. Press Enter to finish."; _resolvedExtent = _services.CadExtentService.PromptForPipelineRouteExtent(ExtentWkid, PaddingFeet); RaisePropertyChanged(nameof(ResolvedExtentText)); Status = "Pipeline route drawn and route bounding box is ready."; }
        catch (Exception ex) { Status = "Draw route failed: " + ex.Message; }
        return Task.CompletedTask;
    }
    private Task UseCurrentViewExtentAsync()
    {
        try { _resolvedExtent = _services.CadExtentService.GetCurrentViewExtent(ExtentWkid, PaddingFeet); RaisePropertyChanged(nameof(ResolvedExtentText)); Status = "Current AutoCAD view extent set as export extent."; }
        catch (Exception ex) { Status = "Current view extent failed: " + ex.Message; }
        return Task.CompletedTask;
    }
    private Task SetManualExtentAsync()
    {
        _resolvedExtent = new ExportExtent { Mode = "Manual", XMin = ManualXMin, YMin = ManualYMin, XMax = ManualXMax, YMax = ManualYMax, Wkid = ExtentWkid, PaddingFeet = 0 };
        RaisePropertyChanged(nameof(ResolvedExtentText));
        Status = "Manual bounding box set as export extent.";
        return Task.CompletedTask;
    }
    private async Task BuildReviewAsync()
    {
        try
        {
            if (_resolvedExtent == null) { Status = "Set or resolve an extent before building the export plan."; return; }

            // Reachable without ever showing the layers page, so make sure the metadata it needs is
            // present rather than reporting that nothing is selected.
            await EnsureLayerMetadataLoadedAsync();

            var selected = Layers.Where(l => l.Enabled).ToList();
            if (selected.Count == 0) { Status = "Select at least one layer before building the export plan."; return; }
            Status = "Building dry-run export plan...";
            var plan = new ExportPlan { Extent = _resolvedExtent };

            // The same boundary the export itself will use. A dry run counted against a wider shape than
            // the export would fetch is worse than no dry run, because it reads as a checked number.
            var boundary = BuildExportBoundary(_profile.DefaultOutputSpatialReferenceWkid);

            foreach (var layer in selected)
            {
                var count = await _services.ArcGisRestClient.QueryCountAsync(layer.Url, _resolvedExtent, CancellationToken.None, boundary);
                var transform = TransformRules.FirstOrDefault(t => string.Equals(t.LayerUrl, layer.Url, StringComparison.OrdinalIgnoreCase));
                plan.Layers.Add(new ExportPlanLayer { Name = layer.Name, Url = layer.Url, GeometryType = layer.GeometryType, FeatureCount = count, SelectedFields = layer.Fields.Where(f => f.Selected).Select(f => f.Name).ToList(), Transform = transform?.Rule ?? new CadTransformRule() });
            }
            var path = await _services.ExportPlanStore.SaveLatestAsync(plan, CancellationToken.None);
            var scope = boundary?.Kind == ExportBoundaryKind.Buffer ? "padding buffer" : "extent bounding box";
            Status = $"Export plan saved: {path}. Counted against the {scope}."
                     + (string.IsNullOrEmpty(_boundaryNote) ? string.Empty : " " + _boundaryNote);
        }
        catch (Exception ex) { Status = "Export plan failed: " + ex.Message; }
    }
    private static void ApplySavedSettings(LayerSelectionViewState state, UserExportSettings settings)
    {
        if (!settings.Layers.TryGetValue(state.Layer.Url, out var saved)) { return; }
        state.Enabled = saved.Enabled;
        var selected = saved.SelectedFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in state.Fields) { field.Selected = field.Field.Required || selected.Contains(field.Field.Name); }
    }
    // The method decides the boundary, so the review page's account of the scope follows the choice.
    private void RaiseMethodFlags() { RaisePropertyChanged(nameof(IsWorkOrderMethod)); RaisePropertyChanged(nameof(IsDrawPipelineRouteMethod)); RaisePropertyChanged(nameof(IsCurrentDrawingViewMethod)); RaisePropertyChanged(nameof(IsManualMethod)); RaisePropertyChanged(nameof(BoundaryKind)); RaisePropertyChanged(nameof(BoundaryDescription)); }
    private void RaisePageFlags() { RaisePropertyChanged(nameof(IsMethodPage)); RaisePropertyChanged(nameof(IsExtentPage)); RaisePropertyChanged(nameof(IsLayerPage)); RaisePropertyChanged(nameof(IsTransformPage)); RaisePropertyChanged(nameof(IsReviewPage)); RaisePropertyChanged(nameof(PageTitle)); }
}
