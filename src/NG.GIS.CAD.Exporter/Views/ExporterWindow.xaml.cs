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
using NG.GIS.CAD.Exporter.Models;
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
    private ExportDisplayMode _displayMode = ExportDisplayMode.WorkOrder;
    private bool _mapInitialized;
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
            UpdatePageVisibility();
            ApplyDisplayModeToExtentPage();

            // Startup work runs in page order so page 1 is never waiting behind a later page: the
            // NG_ODS query first, then the page 2 map, then what pages 3 and 4 need. Everything
            // still loads at startup, it is only sequenced.
            await EnsureNgOdsWorkOrdersLoadedAsync(userInitiated: false);
            await InitializeArcGisMapAsync();
            if (DataContext is ExporterViewModel viewModel) { await viewModel.PreloadLaterPagesAsync(); }
        };
        // Delete removes the manual segment being edited. On the window rather than the map, because the
        // map view is built later and the key has to reach it wherever focus happens to be.
        PreviewKeyDown += ExporterWindow_PreviewKeyDown;

        DataContextChanged += (_, _) =>
        {
            UpdatePageVisibility();
            AttachViewModelEvents();

            // The radio that starts checked raises Checked during InitializeComponent, before there is
            // a view model to tell, so the opening method is published here instead. Without it the view
            // model would only learn the method once the user changed it.
            PublishExportMethodToViewModel();
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

            // Only acted on while a segment is being picked, so an ordinary click on the map still does
            // nothing. Wired once here because the map view is built once.
            _mapView.GeoViewTapped += ExporterMapView_GeoViewTapped;
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
        var previous = _displayMode;

        if (ReferenceEquals(sender, WorkOrderDrivenExportRadio)) { _displayMode = ExportDisplayMode.WorkOrder; }
        else if (ReferenceEquals(sender, VisibleMapExportRadio)) { _displayMode = ExportDisplayMode.VisibleMap; }
        else if (ReferenceEquals(sender, ManualProposedPipelineRadio)) { _displayMode = ExportDisplayMode.ManualProposedPipeline; }

        // Only on a real change. This also fires as the window loads, for the radio that starts checked,
        // and clearing there would be clearing nothing while looking like a decision.
        if (_displayMode != previous) { ClearProposedMainForMethodChange(); }

        PublishExportMethodToViewModel();
        ApplyDisplayModeToExtentPage();
    }

    /// <summary>
    /// Clears the proposed main drawn on page 2 when the export method changes on page 1.
    ///
    /// A main drawn or imported under one method does not carry over to another: the methods differ in
    /// what the export is scoped to, so a line left on the map from the previous one would still be
    /// feeding the buffer while belonging to a question no longer being asked. Leaving it also reads as
    /// though it had been kept deliberately.
    ///
    /// The buffer, the committed extent and the strip map index go with it. All three were derived from
    /// the main being cleared or from the method being left, so keeping any of them would leave the
    /// export working to something nobody asked for under the new method. An extent in particular
    /// carries the mode it was resolved from, so a stale one looks settled while describing the wrong
    /// area, which is worse than page 2 saying there is no extent yet.
    /// </summary>
    private void ClearProposedMainForMethodChange()
    {
        TryStopGeometryEditor();

        _manualProposedPipelineSegmentGeometries.Clear();
        _manualProposedPipelineEndpointSnapCount = 0;
        _editingManualProposedPipelineSegmentIndex = -1;
        _pickingManualProposedPipelineSegment = false;
        _manualProposedPipelineSegmentOverlay?.Graphics.Clear();
        SetLegacySingleManualPipelineGeometry(null);

        // The imported or hand edited work order main lives on its own overlay, and is just as much a
        // line drawn under the old method as the hand drawn segments are.
        _workOrderOverlay?.Graphics.Clear();
        _editableImportedProposedMainGeometry = null;
        _editableImportedProposedMainWorkOrderId = null;
        _isEditingImportedProposedMain = false;
        _lastProposedMainEndpointSnapCount = 0;

        // The index is a run of sheets along the main just cleared, so it has nothing left to describe.
        // Left behind it would still be written into the drawing, which is the trap the viewport method
        // already had to be guarded against.
        _stripMapSheets = Array.Empty<Services.StripMapSheet>();
        _stripMapOverlay?.Graphics.Clear();

        RememberProposedMainBuffer(null);

        if (DataContext is ExporterViewModel vm)
        {
            vm.ClearResolvedExtent();
            vm.StripMapSheets = _stripMapSheets;
            vm.StripMapSummary = "Export method changed, so the proposed main, the extent and any strip "
                                 + "map sheets were cleared.";
        }

        if (IsLoaded) { UpdateManualProposedPipelineSegmentSummary(); }
    }

    /// <summary>
    /// Tells the view model which method is selected.
    ///
    /// The radios on page 1 set only this window's display mode, and nothing ever set the view model's,
    /// so it sat on its default whatever was picked. Everything keyed to the method read that default:
    /// the export was scoped as a corridor job even for a viewport export, and the review page described
    /// the scope as a buffer regardless.
    ///
    /// The hand drawn segments map to <see cref="ExportMethod.DrawPipelineRoute"/> because that is what
    /// they are, a pipeline route the user drew, and it scopes to the buffer as the work order does.
    /// </summary>
    private void PublishExportMethodToViewModel()
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        vm.SelectedMethod = _displayMode switch
        {
            ExportDisplayMode.WorkOrder => ExportMethod.WorkOrder,
            ExportDisplayMode.ManualProposedPipeline => ExportMethod.DrawPipelineRoute,
            ExportDisplayMode.VisibleMap => ExportMethod.CurrentDrawingView,
            _ => vm.SelectedMethod
        };
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
