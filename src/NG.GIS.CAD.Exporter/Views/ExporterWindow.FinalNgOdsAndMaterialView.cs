using System;
using System.Collections.Generic;
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
    private bool _finalNggisFlowInstalled;
    private bool _finalNgOdsWarmupStarted;
    private DispatcherTimer? _finalMaterialRetryTimer;
    private int _finalMaterialRetryCount;
    private const string FinalMaterialViewMapServerUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";
    private const string FinalPortalWebMapItemId = "c214d72caefb40699b129bc47b1b22a7";

    private void InstallFinalNgOdsAndMaterialViewFlow()
    {
        if (_finalNggisFlowInstalled) return;
        _finalNggisFlowInstalled = true;
        Loaded += async (_, __) =>
        {
            await FinalWarmNgOdsFirstAsync();
            FinalScheduleMaterialViewRetries("window loaded");
        };
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => FinalScheduleMaterialViewRetries("button action")), true);
        _ = Dispatcher.BeginInvoke(async () =>
        {
            await FinalWarmNgOdsFirstAsync();
            FinalScheduleMaterialViewRetries("startup");
        }, DispatcherPriority.Loaded);
    }

    private async Task FinalWarmNgOdsFirstAsync()
    {
        if (_finalNgOdsWarmupStarted) return;
        _finalNgOdsWarmupStarted = true;
        await Task.Yield();
        var invoked = false;
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var values = GetType().GetFields(flags).Select(f => FinalSafeGet(() => f.GetValue(this)))
                .Concat(GetType().GetProperties(flags).Select(p => FinalSafeGet(() => p.GetValue(this))));
            foreach (var value in values)
            {
                if (value == null) continue;
                var typeName = value.GetType().Name;
                if (!typeName.Contains("NgOds", StringComparison.OrdinalIgnoreCase) && !typeName.Contains("WorkOrderLookup", StringComparison.OrdinalIgnoreCase)) continue;
                invoked = await FinalTryInvokeWarmupAsync(value) || invoked;
            }
            FinalAppendStatus(invoked ? "NG_ODS startup preload invoked before user interaction." : "NG_ODS startup preload did not find a warm-up method; NG_ODS will initialize on first lookup.");
        }
        catch (Exception ex)
        {
            FinalAppendStatus("NG_ODS startup preload failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static object? FinalSafeGet(Func<object?> getter)
    {
        try { return getter(); } catch { return null; }
    }

    private static async Task<bool> FinalTryInvokeWarmupAsync(object target)
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

    private void FinalScheduleMaterialViewRetries(string reason)
    {
        _finalMaterialRetryCount = 0;
        if (_finalMaterialRetryTimer == null)
        {
            _finalMaterialRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(650) };
            _finalMaterialRetryTimer.Tick += async (_, __) =>
            {
                _finalMaterialRetryCount++;
                await FinalEnsureMaterialViewOnMapsAsync("retry " + _finalMaterialRetryCount);
                if (_finalMaterialRetryCount >= 25) _finalMaterialRetryTimer.Stop();
            };
        }
        _finalMaterialRetryTimer.Stop();
        _finalMaterialRetryTimer.Start();
        _ = Dispatcher.BeginInvoke(async () => await FinalEnsureMaterialViewOnMapsAsync(reason), DispatcherPriority.Loaded);
    }

    private async Task FinalEnsureMaterialViewOnMapsAsync(string reason)
    {
        try
        {
            var mapViews = FinalFindChildren<MapView>(this).Distinct().ToList();
            if (mapViews.Count == 0)
            {
                FinalAppendStatus("Material_View_MA not added yet: no MapView found (" + reason + ").");
                return;
            }
            foreach (var mapView in mapViews)
            {
                if (mapView.Map == null) mapView.Map = new Map(BasemapStyle.ArcGISTopographic);
                var existing = mapView.Map.OperationalLayers.FirstOrDefault(l => string.Equals(l.Name, "Material_View_MA", StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsVisible = true;
                    continue;
                }
                var material = new ArcGISMapImageLayer(new Uri(FinalMaterialViewMapServerUrl)) { Name = "Material_View_MA", IsVisible = true };
                mapView.Map.OperationalLayers.Insert(0, material);
                await material.LoadAsync();
                if (material.LoadStatus == Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    foreach (var sublayer in material.Sublayers) sublayer.IsVisible = true;
                    FinalAppendStatus("Material_View_MA REST MapServer layer added (" + reason + "). Sublayers: " + material.Sublayers.Count + "; map operational layers: " + mapView.Map.OperationalLayers.Count + "; portal item reference: " + FinalPortalWebMapItemId + ".");
                }
                else if (material.LoadError != null)
                {
                    FinalAppendStatus("Material_View_MA load error: " + material.LoadError.GetType().Name + ": " + material.LoadError.Message);
                }
            }
        }
        catch (Exception ex)
        {
            FinalAppendStatus("Material_View_MA add failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static IEnumerable<T> FinalFindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FinalFindChildren<T>(child)) yield return nested;
        }
    }

    private void FinalAppendStatus(string message)
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