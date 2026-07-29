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
    public ExporterViewModel(AppServices services)
    {
        _services = services;
        ProfilePath = _services.ProfileStore.GetDefaultProfilePath();
        Layers = new ObservableCollection<LayerSelectionViewModel>();
        MapLayers = new ObservableCollection<MapLayerToggleViewModel>();
        TransformRules = new ObservableCollection<CadTransformRuleViewModel>();
        WorkOrderOptions = new ObservableCollection<string>();
        Blocks = new ObservableCollection<string>(_services.CadDrawingCatalog.GetBlockNames());
        LineTypes = new ObservableCollection<string>(_services.CadDrawingCatalog.GetLineTypes());
        CadLayers = new ObservableCollection<string>(_services.CadDrawingCatalog.GetLayerNames());
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
        ExportCommand = new RelayCommand(_ => BuildReviewAsync());
        _ = LoadProfileAsync();
        _ = LoadWorkOrdersAsync();
    }
    public ObservableCollection<LayerSelectionViewModel> Layers { get; }
    public ObservableCollection<MapLayerToggleViewModel> MapLayers { get; }
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
    public bool IsMethodPage => PageIndex == 0;
    public bool IsExtentPage => PageIndex == 1;
    public bool IsLayerPage => PageIndex == 2;
    public bool IsTransformPage => PageIndex == 3;
    public bool IsReviewPage => PageIndex == 4;
    public string PageTitle => PageIndex switch { 0 => "1. Export Method", 1 => "2. Extent", 2 => "3. Layers + Fields", 3 => "4. CAD Transformation", _ => "5. Review + Export" };
    public int PageIndex { get => _pageIndex; set { if (SetProperty(ref _pageIndex, value)) { RaisePageFlags(); } } }
    public string ProfilePath { get => _profilePath; set => SetProperty(ref _profilePath, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public ExportMethod SelectedMethod { get => _selectedMethod; set { if (SetProperty(ref _selectedMethod, value)) { RaiseMethodFlags(); } } }
    public bool IsWorkOrderMethod { get => SelectedMethod == ExportMethod.WorkOrder; set { if (value) { SelectedMethod = ExportMethod.WorkOrder; } } }
    public bool IsDrawPipelineRouteMethod { get => SelectedMethod == ExportMethod.DrawPipelineRoute; set { if (value) { SelectedMethod = ExportMethod.DrawPipelineRoute; } } }
    public bool IsCurrentDrawingViewMethod { get => SelectedMethod == ExportMethod.CurrentDrawingView; set { if (value) { SelectedMethod = ExportMethod.CurrentDrawingView; } } }
    public bool IsManualMethod { get => SelectedMethod == ExportMethod.ManualExtent; set { if (value) { SelectedMethod = ExportMethod.ManualExtent; } } }
    public string WorkOrderId { get => _workOrderId; set => SetProperty(ref _workOrderId, value); }
    public double PaddingFeet { get => _paddingFeet; set => SetProperty(ref _paddingFeet, value); }
    public double ManualXMin { get => _manualXMin; set => SetProperty(ref _manualXMin, value); }
    public double ManualYMin { get => _manualYMin; set => SetProperty(ref _manualYMin, value); }
    public double ManualXMax { get => _manualXMax; set => SetProperty(ref _manualXMax, value); }
    public double ManualYMax { get => _manualYMax; set => SetProperty(ref _manualYMax, value); }
    public int ExtentWkid { get => _extentWkid; set => SetProperty(ref _extentWkid, value); }
    public string ResolvedExtentText => _resolvedExtent == null ? "No extent set." : $"{_resolvedExtent.Mode}: {_resolvedExtent.XMin:0.###}, {_resolvedExtent.YMin:0.###}, {_resolvedExtent.XMax:0.###}, {_resolvedExtent.YMax:0.###}, WKID {_resolvedExtent.Wkid}";
    public LayerSelectionViewModel? SelectedLayer { get => _selectedLayer; set => SetProperty(ref _selectedLayer, value); }
    public CadTransformRuleViewModel? SelectedTransform { get => _selectedTransform; set => SetProperty(ref _selectedTransform, value); }
    private Task NextAsync() { if (PageIndex < 4) { PageIndex++; } return Task.CompletedTask; }
    private Task BackAsync() { if (PageIndex > 0) { PageIndex--; } return Task.CompletedTask; }
    private async Task LoadProfileAsync()
    {
        try
        {
            Status = "Loading profile and GIS layer metadata...";
            var profile = await _services.ProfileStore.LoadAsync(ProfilePath, CancellationToken.None);
            _profile = profile;
            var userSettings = await _services.UserSettingsStore.LoadAsync(CancellationToken.None);
            Layers.Clear();
            TransformRules.Clear();
            foreach (var service in profile.Services.Where(s => s.Enabled))
            {
                var metadata = await _services.ArcGisRestClient.LoadServiceLayersAsync(service.ServiceUrl, CancellationToken.None);
                foreach (var layer in metadata)
                {
                    var state = new LayerSelectionViewState(layer);
                    ApplySavedSettings(state, userSettings);
                    var layerVm = new LayerSelectionViewModel(state);
                    Layers.Add(layerVm);
                    TransformRules.Add(new CadTransformRuleViewModel(new CadTransformRule { LayerUrl = layer.Url, LayerName = layer.Name, GeometryType = layer.GeometryType, CadLayerName = layer.Name.Replace(" ", "_"), LineType = "ByLayer", ColorMode = "ByLayer" }));
                }
            }
            SelectedLayer = Layers.FirstOrDefault();
            SelectedTransform = TransformRules.FirstOrDefault();
            Status = $"Loaded {Layers.Count} layers.";
        }
        catch (Exception ex) { Status = "Load failed: " + ex.Message; }
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
        await _services.ExportCoordinator.SaveSelectionsAsync(Layers.Select(l => l.State), ProfilePath, CancellationToken.None);
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
            foreach (var layer in MapLayers)
            {
                _profile.MapLayerVisibility[layer.Path] = layer.IsVisible;
            }
            await _services.ProfileStore.SaveAsync(_profile, ProfilePath, CancellationToken.None);
        }
        catch (Exception ex) { Status = "Saving layer visibility failed: " + ex.Message; }
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
            var selected = Layers.Where(l => l.Enabled).ToList();
            if (selected.Count == 0) { Status = "Select at least one layer before building the export plan."; return; }
            Status = "Building dry-run export plan...";
            var plan = new ExportPlan { Extent = _resolvedExtent };
            foreach (var layer in selected)
            {
                var count = await _services.ArcGisRestClient.QueryCountAsync(layer.Url, _resolvedExtent, CancellationToken.None);
                var transform = TransformRules.FirstOrDefault(t => string.Equals(t.LayerUrl, layer.Url, StringComparison.OrdinalIgnoreCase));
                plan.Layers.Add(new ExportPlanLayer { Name = layer.Name, Url = layer.Url, GeometryType = layer.GeometryType, FeatureCount = count, SelectedFields = layer.Fields.Where(f => f.Selected).Select(f => f.Name).ToList(), Transform = transform?.Rule ?? new CadTransformRule() });
            }
            var path = await _services.ExportPlanStore.SaveLatestAsync(plan, CancellationToken.None);
            Status = $"Export plan saved: {path}";
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
    private void RaiseMethodFlags() { RaisePropertyChanged(nameof(IsWorkOrderMethod)); RaisePropertyChanged(nameof(IsDrawPipelineRouteMethod)); RaisePropertyChanged(nameof(IsCurrentDrawingViewMethod)); RaisePropertyChanged(nameof(IsManualMethod)); }
    private void RaisePageFlags() { RaisePropertyChanged(nameof(IsMethodPage)); RaisePropertyChanged(nameof(IsExtentPage)); RaisePropertyChanged(nameof(IsLayerPage)); RaisePropertyChanged(nameof(IsTransformPage)); RaisePropertyChanged(nameof(IsReviewPage)); RaisePropertyChanged(nameof(PageTitle)); }
}
