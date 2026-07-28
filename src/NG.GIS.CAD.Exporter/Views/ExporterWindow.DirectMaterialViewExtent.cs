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
    private DispatcherTimer? _directMaterialViewRetryTimer;
    private int _directMaterialViewRetryCount;
    private const string DirectMaterialViewMapServerUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";
    private const string DirectMaterialViewPortalItemId = "c214d72caefb40699b129bc47b1b22a7";

    private void DirectScheduleMaterialViewRetries(string reason)
    {
        _directMaterialViewRetryCount = 0;
        if (_directMaterialViewRetryTimer == null)
        {
            _directMaterialViewRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
            _directMaterialViewRetryTimer.Tick += async (_, __) =>
            {
                _directMaterialViewRetryCount++;
                await DirectEnsureMaterialViewOnExtentMapAsync("retry " + _directMaterialViewRetryCount);
                if (_directMaterialViewRetryCount >= 20) _directMaterialViewRetryTimer.Stop();
            };
            AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => DirectScheduleMaterialViewRetries("button action")), true);
        }
        _directMaterialViewRetryTimer.Stop();
        _directMaterialViewRetryTimer.Start();
    }

    private async Task DirectEnsureMaterialViewOnExtentMapAsync(string reason)
    {
        DirectScheduleMaterialViewRetries(reason);
        try
        {
            var mapViews = DirectFindAllMapViews().Distinct().ToList();
            if (mapViews.Count == 0)
            {
                DirectAppendStatus("Material_View_MA not added: no ArcGIS MapView found in window (" + reason + ").");
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
                var layer = new ArcGISMapImageLayer(new Uri(DirectMaterialViewMapServerUrl)) { Name = "Material_View_MA", IsVisible = true };
                mapView.Map.OperationalLayers.Insert(0, layer);
                await layer.LoadAsync();
                if (layer.LoadStatus == Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    foreach (var sublayer in layer.Sublayers) sublayer.IsVisible = true;
                    DirectAppendStatus("Material_View_MA MapServer added directly after Extent render (" + reason + "). Sublayers: " + layer.Sublayers.Count + "; operational layers: " + mapView.Map.OperationalLayers.Count + "; portal item: " + DirectMaterialViewPortalItemId + ".");
                }
                else if (layer.LoadError != null)
                {
                    DirectAppendStatus("Material_View_MA load error: " + layer.LoadError.GetType().Name + ": " + layer.LoadError.Message);
                }
            }
        }
        catch (Exception ex)
        {
            DirectAppendStatus("Material_View_MA add failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private IEnumerable<MapView> DirectFindAllMapViews()
    {
        foreach (var mv in DirectFindVisualChildren<MapView>(this)) yield return mv;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var field in GetType().GetFields(flags))
        {
            object? value = null;
            try { value = field.GetValue(this); } catch { }
            if (value is MapView mv) yield return mv;
        }
        foreach (var prop in GetType().GetProperties(flags))
        {
            object? value = null;
            try { value = prop.GetValue(this); } catch { }
            if (value is MapView mv) yield return mv;
        }
    }

    private static IEnumerable<T> DirectFindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in DirectFindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void DirectAppendStatus(string message)
    {
        try
        {
            var boxes = DirectFindVisualChildren<TextBox>(this).ToList();
            var blocks = DirectFindVisualChildren<TextBlock>(this).ToList();
            var targetBox = boxes.FirstOrDefault(b => b.Name == "WorkOrderGeometryTextBox") ?? boxes.FirstOrDefault();
            if (targetBox != null && !targetBox.Text.Contains(message))
            {
                targetBox.Text = string.IsNullOrWhiteSpace(targetBox.Text) ? message : targetBox.Text + Environment.NewLine + message;
            }
            foreach (var block in blocks.Where(b => (b.Text ?? string.Empty).Contains("Native ArcGIS MapView loaded") || (b.Text ?? string.Empty).Contains("Extent page")))
            {
                block.Text = message;
            }
        }
        catch { }
    }
}