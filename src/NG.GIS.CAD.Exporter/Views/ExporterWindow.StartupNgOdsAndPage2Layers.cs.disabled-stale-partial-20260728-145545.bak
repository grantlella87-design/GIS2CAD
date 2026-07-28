using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private bool _nggisStartupDataInstalled;
    private bool _nggisNgOdsWarmupStarted;
    private DispatcherTimer? _nggisPage2LayerRetryTimer;
    private int _nggisPage2LayerRetryCount;
    private const string NggisMaterialViewServiceUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";

    private void InstallNggisStartupDataFlow()
    {
        if (_nggisStartupDataInstalled) return;
        _nggisStartupDataInstalled = true;
        Loaded += async (_, __) =>
        {
            await WarmNgOdsFirstAsync();
            SchedulePage2MaterialViewRetries("window loaded");
        };
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => SchedulePage2MaterialViewRetries("button action")), true);
        _ = Dispatcher.BeginInvoke(async () =>
        {
            await WarmNgOdsFirstAsync();
            SchedulePage2MaterialViewRetries("startup");
        }, DispatcherPriority.Loaded);
    }

    private async Task WarmNgOdsFirstAsync()
    {
        if (_nggisNgOdsWarmupStarted) return;
        _nggisNgOdsWarmupStarted = true;
        await Task.Yield();
        try
        {
            var invoked = false;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var memberValue in GetType().GetFields(flags).Select(f => f.GetValue(this)).Concat(GetType().GetProperties(flags).Select(p => { try { return p.GetValue(this); } catch { return null; } })))
            {
                if (memberValue == null) continue;
                var typeName = memberValue.GetType().Name;
                if (!typeName.Contains("NgOds", StringComparison.OrdinalIgnoreCase) && !typeName.Contains("WorkOrderLookup", StringComparison.OrdinalIgnoreCase)) continue;
                invoked = await TryInvokeWarmupMethodAsync(memberValue) || invoked;
            }
            AppendNggisStartupStatus(invoked ? "NG_ODS startup preload invoked before user interaction." : "NG_ODS startup preload did not find a warm-up method; NG_ODS will initialize on first lookup.");
        }
        catch (Exception ex)
        {
            AppendNggisStartupStatus("NG_ODS startup preload failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task<bool> TryInvokeWarmupMethodAsync(object target)
    {
        var names = new[] { "Warm", "Preload", "Load", "Initialize", "Ensure", "Refresh", "Cache" };
        foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.GetParameters().Length == 0 && names.Any(n => m.Name.Contains(n, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                var result = method.Invoke(target, null);
                if (result is Task task) await task;
                return true;
            }
            catch { }
        }
        return false;
    }

    private void SchedulePage2MaterialViewRetries(string reason)
    {
        _nggisPage2LayerRetryCount = 0;
        if (_nggisPage2LayerRetryTimer == null)
        {
            _nggisPage2LayerRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
            _nggisPage2LayerRetryTimer.Tick += async (_, __) =>
            {
                _nggisPage2LayerRetryCount++;
                await EnsurePage2MaterialViewLayerAsync("retry " + _nggisPage2LayerRetryCount);
                if (_nggisPage2LayerRetryCount >= 16) _nggisPage2LayerRetryTimer.Stop();
            };
        }
        _nggisPage2LayerRetryTimer.Stop();
        _nggisPage2LayerRetryTimer.Start();
        _ = Dispatcher.BeginInvoke(async () => await EnsurePage2MaterialViewLayerAsync(reason), DispatcherPriority.Loaded);
    }

    private async Task EnsurePage2MaterialViewLayerAsync(string reason)
    {
        try
        {
            var mapView = FindNggisStartupChild<MapView>(this);
            if (mapView == null) return;
            if (mapView.Map == null) mapView.Map = new Map(BasemapStyle.ArcGISTopographic);
            var existing = mapView.Map.OperationalLayers.FirstOrDefault(l => string.Equals(l.Name, "Material_View_MA", StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsVisible = true;
                return;
            }
            var materialLayer = new ArcGISMapImageLayer(new Uri(NggisMaterialViewServiceUrl)) { Name = "Material_View_MA", IsVisible = true };
            mapView.Map.OperationalLayers.Insert(0, materialLayer);
            await materialLayer.LoadAsync();
            if (materialLayer.LoadStatus == Esri.ArcGISRuntime.LoadStatus.Loaded)
            {
                AppendNggisStartupStatus("Material_View_MA operational layer added to Page 2 map (" + reason + "). Operational layer count: " + mapView.Map.OperationalLayers.Count + ".");
            }
            else if (materialLayer.LoadError != null)
            {
                AppendNggisStartupStatus("Material_View_MA load error: " + materialLayer.LoadError.GetType().Name + ": " + materialLayer.LoadError.Message);
            }
        }
        catch (Exception ex)
        {
            AppendNggisStartupStatus("Material_View_MA Page 2 layer add failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static T? FindNggisStartupChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindNggisStartupChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void AppendNggisStartupStatus(string message)
    {
        try
        {
            var textBox = FindName("WorkOrderGeometryTextBox") as TextBox;
            if (textBox != null && !textBox.Text.Contains(message))
            {
                textBox.Text = string.IsNullOrWhiteSpace(textBox.Text) ? message : textBox.Text + Environment.NewLine + message;
            }
        }
        catch { }
    }
}