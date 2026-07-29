using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Tasks.Geocoding;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Esri.ArcGISRuntime.UI.Editing;
using NG.GIS.CAD.Exporter.ViewModels;
using NG.GIS.CAD.Exporter.Auth;
namespace NG.GIS.CAD.Exporter.Views;
public partial class ExporterWindow : Window
{
    private enum ExportDisplayMode
    {
        WorkOrder,
        VisibleMap,
        ManualProposedPipeline
    }
    private readonly DispatcherTimer _extentRefreshTimer;
    private readonly HashSet<string> _workOrderSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private ExportDisplayMode _displayMode = ExportDisplayMode.WorkOrder;
    private bool _mapInitialized;
    private bool _handlingWorkOrderInput;
    private LocatorTask? _locatorTask;
    private MapView? _mapView;
    private GeometryEditor? _geometryEditor;
    private GraphicsOverlay? _proposedPipelineOverlay;
    private GraphicsOverlay? _workOrderOverlay;
    private Geometry? _proposedPipelineGeometry;
    public ExporterWindow()
    {
        // NGGIS_ARCGIS_RUNTIME_BOOTSTRAP
            NG.GIS.CAD.Exporter.Services.ArcGisRuntimeBootstrap.Initialize();
            InitializeComponent();
_extentRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _extentRefreshTimer.Tick += (_, _) =>
        {
            _extentRefreshTimer.Stop();
            if (_displayMode == ExportDisplayMode.VisibleMap) { CaptureAndDisplayVisibleExtent(false, false); }
        };
        Loaded += async (_, _) =>
        {
            LoadWorkOrderSuggestions();
            UpdatePageVisibility();
            ApplyDisplayModeToExtentPage();

            // Startup work runs in page order so page 1 is never waiting behind a later page: the
            // NG_ODS query first, then the page 2 map, then what pages 3 and 4 need. Everything
            // still loads at startup, it is only sequenced.
            await EnsureNgOdsWorkOrdersLoadedAsync(userInitiated: false);
            await InitializeArcGisMapAsync();
            if (DataContext is ExporterViewModel viewModel) { await viewModel.PreloadLaterPagesAsync(); }
        };
        DataContextChanged += (_, _) =>
        {
            UpdatePageVisibility();
            AttachViewModelEvents();
            ApplyDisplayModeToExtentPage();

            // The map is deliberately not started here. DataContext is assigned in the object
            // initializer before the window is shown, so doing map setup here would run it ahead of
            // the work order query. Loaded starts it, and reaching page 2 starts it if that has not
            // happened yet.
        };
    }
    private void AttachViewModelEvents()
    {
        if (DataContext is ExporterViewModel vm)
        {
            vm.PropertyChanged -= ViewModel_PropertyChanged;
            vm.PropertyChanged += ViewModel_PropertyChanged;
            vm.MapDataSourcesChanged -= ViewModel_MapDataSourcesChanged;
            vm.MapDataSourcesChanged += ViewModel_MapDataSourcesChanged;
        }
    }
    private async void ViewModel_MapDataSourcesChanged()
    {
        await ApplyMapDataSourcesAsync();
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (DataContext is not ExporterViewModel vm) { return; }
        if (args.PropertyName == nameof(vm.PageIndex))
        {
            UpdatePageVisibility();
            ApplyDisplayModeToExtentPage();
            if (vm.IsExtentPage)
            {
                await InitializeArcGisMapAsync();
                if (_displayMode == ExportDisplayMode.WorkOrder) { await RefreshWorkOrderGeometryAndBufferAsync(); }
            }
        }
    }
    private void UpdatePageVisibility()
    {
        if (DataContext is not ExporterViewModel vm) { return; }
        MethodPage.Visibility = vm.IsMethodPage ? Visibility.Visible : Visibility.Collapsed;
        ExtentPage.Visibility = vm.IsExtentPage ? Visibility.Visible : Visibility.Collapsed;
        LayerPage.Visibility = vm.IsLayerPage ? Visibility.Visible : Visibility.Collapsed;
        TransformPage.Visibility = vm.IsTransformPage ? Visibility.Visible : Visibility.Collapsed;
        ReviewPage.Visibility = vm.IsReviewPage ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task InitializeArcGisMapAsync()
    {
        if (_mapInitialized) { return; }
        _mapInitialized = true;
        try
        {
            AddArcGisRuntimeFoldersToPath();
            _mapView = new MapView();
            _mapView.ViewpointChanged += ArcGisMapView_ViewpointChanged;
            _workOrderOverlay = new GraphicsOverlay { Id = "Work Order Geometry + Buffer" };
            _proposedPipelineOverlay = new GraphicsOverlay { Id = "Manual Proposed Pipeline" };
            _mapView.GraphicsOverlays.Add(_workOrderOverlay);
            _mapView.GraphicsOverlays.Add(_proposedPipelineOverlay);
            _geometryEditor = new GeometryEditor();
            _mapView.GeometryEditor = _geometryEditor;
            ArcGisMapHost.Content = _mapView;
            // Basemap-only starting point. LoadExtentWebMapAsync swaps in the portal web map when
            // it resolves, and this stays as the fallback if it does not.
            var topoLayer = new ArcGISTiledLayer(new Uri("https" + "://" + "services.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer"));
            _mapView.Map = new Map(new Basemap(topoLayer));
            await LoadExtentWebMapAsync();
            var easternMass = new Envelope(-8070663.082512335, 5012341.663847514, -7736704.610132514, 5342463.601958378, SpatialReferences.WebMercator);
            await _mapView.SetViewpointGeometryAsync(easternMass, 50);
            _locatorTask = await LocatorTask.CreateAsync(new Uri("https" + "://" + "geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer"));
            if (ProposedPipelineTextBox != null) { ProposedPipelineTextBox.Text = "No proposed pipeline has been drawn."; }
            if (WorkOrderGeometryTextBox != null) { WorkOrderGeometryTextBox.Text = "Work Order driven export selected. Work order geometry/buffer will refresh when this page is shown or Refresh is clicked."; }
        }
        catch (Exception ex)
        {
            _mapInitialized = false;
            if (DataContext is ExporterViewModel vm) { vm.Status = "ArcGIS MapView failed: " + FlattenException(ex); }
        }
    }
    private void ExportDisplayMode_Checked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, WorkOrderDrivenExportRadio)) { _displayMode = ExportDisplayMode.WorkOrder; }
        else if (ReferenceEquals(sender, VisibleMapExportRadio)) { _displayMode = ExportDisplayMode.VisibleMap; }
        else if (ReferenceEquals(sender, ManualProposedPipelineRadio)) { _displayMode = ExportDisplayMode.ManualProposedPipeline; }
        ApplyDisplayModeToExtentPage();
    }
    private void WorkOrderInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        SwitchToWorkOrderMode("WO number or padding changed. Export method switched to Work Order driven export.");
    }
    private void WorkOrderSelectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkOrderSelectionComboBox?.SelectedItem != null)
        {
            WorkOrderSelectionComboBox.Text = WorkOrderSelectionComboBox.SelectedItem.ToString() ?? string.Empty;
            AddWorkOrderSuggestion(WorkOrderSelectionComboBox.Text);
            SwitchToWorkOrderMode("Work order selected. Export method switched to Work Order driven export.");
        }
    }
    private void WorkOrderSelectionComboBox_DropDownOpened(object sender, EventArgs e)
    {
        LoadWorkOrderSuggestions();
    }
    private void WorkOrderSelectionComboBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AddWorkOrderSuggestion(WorkOrderSelectionComboBox?.Text);
        SwitchToWorkOrderMode("Work order updated. Export method switched to Work Order driven export.");
    }
    private void SwitchToWorkOrderMode(string status)
    {
        if (_handlingWorkOrderInput) { return; }
        _handlingWorkOrderInput = true;
        try
        {
            _displayMode = ExportDisplayMode.WorkOrder;
            if (WorkOrderDrivenExportRadio != null) { WorkOrderDrivenExportRadio.IsChecked = true; }
            ApplyDisplayModeToExtentPage();
            if (DataContext is ExporterViewModel vm) { vm.Status = status; }
        }
        finally
        {
            _handlingWorkOrderInput = false;
        }
    }
    private void LoadWorkOrderSuggestions()
    {
        try
        {
            var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NationalGrid", "GisCadExporter", "ng-gis-export-profile.json");
            if (File.Exists(profilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
                ExtractWorkOrderSuggestions(doc.RootElement);
            }
            AddWorkOrderSuggestion(WorkOrderSelectionComboBox?.Text);
            if (WorkOrderSelectionComboBox == null) { return; }
            var currentText = WorkOrderSelectionComboBox.Text;
            WorkOrderSelectionComboBox.Items.Clear();
            foreach (var value in _workOrderSuggestions.OrderByDescending(x => x))
            {
                WorkOrderSelectionComboBox.Items.Add(value);
            }
            WorkOrderSelectionComboBox.Text = currentText;
        }
        catch
        {
        }
    }
    private void ExtractWorkOrderSuggestions(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var nameLooksRelevant = property.Name.Contains("work", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("wo", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("order", StringComparison.OrdinalIgnoreCase);
                    if (nameLooksRelevant && property.Value.ValueKind == JsonValueKind.String) { AddWorkOrderSuggestion(property.Value.GetString()); }
                    ExtractWorkOrderSuggestions(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray()) { ExtractWorkOrderSuggestions(child); }
                break;
            case JsonValueKind.String:
                AddWorkOrderSuggestion(element.GetString());
                break;
        }
    }
    private void AddWorkOrderSuggestion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return; }
        var text = value.Trim();
        if (text.Length < 4 || text.Length > 40) { return; }
        if (!Regex.IsMatch(text, "[0-9]{4,}")) { return; }
        _workOrderSuggestions.Add(text);
    }
    private void ApplyDisplayModeToExtentPage()
    {
        if (WorkOrderModePanel == null || VisibleMapModePanel == null || ManualProposedPipelineModePanel == null) { return; }

        // Each mode has a fixed header of buttons in one row and a resizable readout in another, so
        // both halves are shown and hidden together.
        var workOrder = _displayMode == ExportDisplayMode.WorkOrder ? Visibility.Visible : Visibility.Collapsed;
        var visibleMap = _displayMode == ExportDisplayMode.VisibleMap ? Visibility.Visible : Visibility.Collapsed;
        var manualPipeline = _displayMode == ExportDisplayMode.ManualProposedPipeline ? Visibility.Visible : Visibility.Collapsed;

        WorkOrderModePanel.Visibility = workOrder;
        VisibleMapModePanel.Visibility = visibleMap;
        ManualProposedPipelineModePanel.Visibility = manualPipeline;

        if (WorkOrderModeHeader != null) { WorkOrderModeHeader.Visibility = workOrder; }
        if (VisibleMapModeHeader != null) { VisibleMapModeHeader.Visibility = visibleMap; }
        if (ManualProposedPipelineModeHeader != null) { ManualProposedPipelineModeHeader.Visibility = manualPipeline; }
        if (ExtentModeTitle != null)
        {
            ExtentModeTitle.Text = _displayMode switch
            {
                ExportDisplayMode.WorkOrder => "Work Order driven export - geometry/buffer display",
                ExportDisplayMode.VisibleMap => "Visible map viewport export - live extent tracking",
                ExportDisplayMode.ManualProposedPipeline => "Manual proposed pipeline segments",
                _ => "Native ArcGIS Maps SDK MapView"
            };
        }
        _workOrderOverlay?.Graphics.Clear();
        if (_displayMode != ExportDisplayMode.ManualProposedPipeline && _geometryEditor != null && _geometryEditor.IsStarted) { _geometryEditor.Stop(); }
    }

    private async Task RefreshWorkOrderGeometryAndBufferAsync()
    {
        try
        {
            await InitializeArcGisMapAsync();
            AddWorkOrderSuggestion(WorkOrderSelectionComboBox?.Text);
            if (DataContext is not ExporterViewModel vm) { return; }
            if (vm.ResolveWorkOrderExtentCommand != null && vm.ResolveWorkOrderExtentCommand.CanExecute(null))
            {
                vm.ResolveWorkOrderExtentCommand.Execute(null);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var resolvedExtent = vm.CurrentExtent;
            if (resolvedExtent == null)
            {
                WorkOrderGeometryTextBox.Text = "Existing ResolveWorkOrderExtentCommand did not expose CurrentExtent yet. Check WO number/padding inputs and command status.";
                return;
            }
            var envelope = new Envelope(resolvedExtent.XMin, resolvedExtent.YMin, resolvedExtent.XMax, resolvedExtent.YMax, SpatialReferences.WebMercator);
            DrawWorkOrderBufferGraphic(envelope, resolvedExtent.PaddingFeet);
            var zoomEnvelope = ExpandEnvelopeByFeet(envelope, 200);
            if (_mapView != null) { await _mapView.SetViewpointGeometryAsync(zoomEnvelope, 20); }
            var captured = new
            {
                source = "ResolveWorkOrderExtentCommand + ExporterViewModel.CurrentExtent",
                displayMode = "Work Order driven export",
                wkid = resolvedExtent.Wkid,
                xmin = Math.Round(resolvedExtent.XMin, 3),
                ymin = Math.Round(resolvedExtent.YMin, 3),
                xmax = Math.Round(resolvedExtent.XMax, 3),
                ymax = Math.Round(resolvedExtent.YMax, 3),
                paddingFeet = resolvedExtent.PaddingFeet,
                viewportExtraOffsetFeet = 200,
                capturedLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
            var json = JsonSerializer.Serialize(captured, new JsonSerializerOptions { WriteIndented = true });
            WorkOrderGeometryTextBox.Text = json;
            try { Clipboard.SetText(json); } catch { }
            vm.Status = "Work order geometry/buffer displayed and map zoomed to buffer plus 200 ft viewport offset.";
        }
        catch (Exception ex)
        {
            WorkOrderGeometryTextBox.Text = FlattenException(ex);
            if (DataContext is ExporterViewModel vm) { vm.Status = "Work order geometry/buffer refresh failed: " + FlattenException(ex); }
        }
    }
    private void DrawWorkOrderBufferGraphic(Envelope envelope, double paddingFeet)
    {
        if (_workOrderOverlay == null) { return; }
        _workOrderOverlay.Graphics.Clear();
        var fill = new SimpleFillSymbol(SimpleFillSymbolStyle.Solid, Color.FromArgb(45, 0, 120, 255), new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.FromArgb(255, 0, 90, 255), 3));
        _workOrderOverlay.Graphics.Add(new Graphic(envelope, fill));
        var outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Dash, Color.FromArgb(255, 0, 60, 220), 3);
        _workOrderOverlay.Graphics.Add(new Graphic(envelope, outline));
    }
    private static Envelope ExpandEnvelopeByFeet(Envelope envelope, double feet)
    {
        var meters = feet * 0.3048;
        return new Envelope(envelope.XMin - meters, envelope.YMin - meters, envelope.XMax + meters, envelope.YMax + meters, envelope.SpatialReference);
    }
    private void ArcGisMapView_ViewpointChanged(object? sender, EventArgs e)
    {
        if (_displayMode == ExportDisplayMode.VisibleMap) { ScheduleLiveExtentRefresh(); }
    }
    private void ScheduleLiveExtentRefresh()
    {
        _extentRefreshTimer.Stop();
        _extentRefreshTimer.Start();
    }
    private static void AddArcGisRuntimeFoldersToPath()
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(assemblyDir)) { return; }
        var pathParts = new List<string> { assemblyDir };
        foreach (var dir in Directory.GetDirectories(assemblyDir, "arcgisruntime*", SearchOption.TopDirectoryOnly))
        {
            pathParts.Add(dir);
            foreach (var child in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(child);
                if (name.Contains("client", StringComparison.OrdinalIgnoreCase) || name.Contains("resources", StringComparison.OrdinalIgnoreCase) || name.Contains("runtimes", StringComparison.OrdinalIgnoreCase))
                {
                    pathParts.Add(child);
                }
            }
        }
        var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", string.Join(";", pathParts.Distinct()) + ";" + existing);
        Directory.SetCurrentDirectory(assemblyDir);
    }




    private void DrawProposedPipelineGraphic(Geometry geometry)
    {
        if (_proposedPipelineOverlay == null) { return; }
        _proposedPipelineOverlay.Graphics.Clear();
        var symbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.FromArgb(255, 255, 0, 0), 4);
        _proposedPipelineOverlay.Graphics.Add(new Graphic(geometry, symbol));
    }
    private void DisplayProposedPipelineGeometry(Geometry geometry, bool copyToClipboard)
    {
        var extent = geometry.Extent;
        var captured = new
        {
            source = "ArcGISRuntime.UI.Editing.GeometryEditor.Stop()",
            geometryType = geometry.GeometryType.ToString(),
            wkid = geometry.SpatialReference?.Wkid ?? 0,
            xmin = Math.Round(extent.XMin, 3),
            ymin = Math.Round(extent.YMin, 3),
            xmax = Math.Round(extent.XMax, 3),
            ymax = Math.Round(extent.YMax, 3),
            width = Math.Round(extent.Width, 3),
            height = Math.Round(extent.Height, 3),
            capturedLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        };
        var json = JsonSerializer.Serialize(captured, new JsonSerializerOptions { WriteIndented = true });
        ProposedPipelineTextBox.Text = json;
        if (copyToClipboard)
        {
            try { Clipboard.SetText(json); } catch { }
        }
    }
    private async void AddressSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) { return; }
        await GoToAddressAsync();
        e.Handled = true;
    }
    private async void GoToAddress_Click(object sender, RoutedEventArgs e)
    {
        await GoToAddressAsync();
    }
    private async Task GoToAddressAsync()
    {
        var address = AddressSearchTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            if (DataContext is ExporterViewModel vm) { vm.Status = "Enter an address before using Go 1:960."; }
            return;
        }
        try
        {
            await InitializeArcGisMapAsync();
            if (_locatorTask == null || _mapView == null) { throw new InvalidOperationException("ArcGIS MapView or locator service is not initialized."); }
            var parameters = new GeocodeParameters { MaxResults = 1, OutputSpatialReference = SpatialReferences.WebMercator };
            var results = await _locatorTask.GeocodeAsync(address, parameters);
            var first = results.FirstOrDefault();
            if (first == null || first.DisplayLocation == null)
            {
                if (DataContext is ExporterViewModel noneVm) { noneVm.Status = "No address result found."; }
                return;
            }
            await _mapView.SetViewpointCenterAsync(first.DisplayLocation, 960);
            if (_displayMode == ExportDisplayMode.VisibleMap) { CaptureAndDisplayVisibleExtent(false, false); }
            if (DataContext is ExporterViewModel vm) { vm.Status = "Address loaded at 1:960."; }
        }
        catch (Exception ex)
        {
            if (DataContext is ExporterViewModel vm) { vm.Status = "Go To Address failed: " + FlattenException(ex); }
        }
    }
    private async void ResetMapView_Click(object sender, RoutedEventArgs e)
    {
        await InitializeArcGisMapAsync();
        if (_mapView == null) { return; }
        var easternMass = new Envelope(-8070663.082512335, 5012341.663847514, -7736704.610132514, 5342463.601958378, SpatialReferences.WebMercator);
        await _mapView.SetViewpointGeometryAsync(easternMass, 50);
        if (_displayMode == ExportDisplayMode.VisibleMap) { CaptureAndDisplayVisibleExtent(false, false); }
        if (DataContext is ExporterViewModel vm) { vm.Status = "Native ArcGIS MapView reset to eastern Massachusetts."; }
    }
    private void SetExtentFromVisibleMap_Click(object sender, RoutedEventArgs e)
    {
        CaptureAndDisplayVisibleExtent(true, true);
    }
    private void CaptureAndDisplayVisibleExtent(bool writeToExporterModel, bool copyToClipboard)
    {
        try
        {
            var extent = GetVisibleWebMercatorExtent();
            if (extent == null)
            {
                CapturedExtentTextBox.Text = "MapView visible extent is not available yet.";
                if (DataContext is ExporterViewModel noExtentVm) { noExtentVm.Status = "ArcGIS MapView visible extent is not available yet."; }
                return;
            }
            var captured = new
            {
                source = "ArcGISRuntime.UI.Controls.MapView.VisibleArea.Extent",
                updateMode = writeToExporterModel ? "committed_to_export_extent" : "live_viewport_preview",
                wkid = 3857,
                xmin = Math.Round(extent.XMin, 3),
                ymin = Math.Round(extent.YMin, 3),
                xmax = Math.Round(extent.XMax, 3),
                ymax = Math.Round(extent.YMax, 3),
                width = Math.Round(extent.Width, 3),
                height = Math.Round(extent.Height, 3),
                centerX = Math.Round((extent.XMin + extent.XMax) / 2.0, 3),
                centerY = Math.Round((extent.YMin + extent.YMax) / 2.0, 3),
                capturedLocalTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            };
            var json = JsonSerializer.Serialize(captured, new JsonSerializerOptions { WriteIndented = true });
            CapturedExtentTextBox.Text = json;
            if (copyToClipboard)
            {
                try { Clipboard.SetText(json); } catch { }
            }
            if (writeToExporterModel && DataContext is ExporterViewModel vm)
            {
                vm.SetExtentFromMap(extent.XMin, extent.YMin, extent.XMax, extent.YMax, 3857);
                vm.Status = "Committed current MapView.VisibleArea extent, copied JSON to clipboard, and set it as export extent.";
            }
        }
        catch (Exception ex)
        {
            CapturedExtentTextBox.Text = FlattenException(ex);
            if (DataContext is ExporterViewModel vm) { vm.Status = "Visible extent capture failed: " + FlattenException(ex); }
        }
    }
    private Envelope? GetVisibleWebMercatorExtent()
    {
        var extent = _mapView?.VisibleArea?.Extent;
        if (extent == null) { return null; }
        if (extent.SpatialReference?.Wkid == 3857) { return extent; }
        return GeometryEngine.Project(extent, SpatialReferences.WebMercator) as Envelope;
    }
    private static string FlattenException(Exception ex)
    {
        var messages = new List<string>();
        var current = ex;
        while (current != null)
        {
            messages.Add(current.GetType().Name + ": " + current.Message);
            current = current.InnerException;
        }
        return string.Join(" -> ", messages);
    }

        private void PaddingInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
        }}
