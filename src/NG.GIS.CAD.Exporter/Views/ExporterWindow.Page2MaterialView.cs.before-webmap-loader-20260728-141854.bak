using System;
using System.Linq;
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
    private bool _page2MaterialViewAutoloadInstalled;
    private DispatcherTimer? _page2MaterialViewRetryTimer;
    private int _page2MaterialViewRetryCount;
    private const string Page2MaterialViewServiceUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";
    private const string Page2MaterialViewWebMapItemId = "c214d72caefb40699b129bc47b1b22a7";

    private void InstallPage2MaterialViewAutoload()
    {
        if (_page2MaterialViewAutoloadInstalled) return;
        _page2MaterialViewAutoloadInstalled = true;
        Loaded += async (_, __) => await EnsurePage2MaterialViewLoadedAsync("window loaded");
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => SchedulePage2MaterialViewRetries("button action")), true);
        SchedulePage2MaterialViewRetries("install");
    }

    private void SchedulePage2MaterialViewRetries(string reason)
    {
        _page2MaterialViewRetryCount = 0;
        if (_page2MaterialViewRetryTimer == null)
        {
            _page2MaterialViewRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(700)
            };
            _page2MaterialViewRetryTimer.Tick += async (_, __) =>
            {
                _page2MaterialViewRetryCount++;
                await EnsurePage2MaterialViewLoadedAsync("retry " + _page2MaterialViewRetryCount);
                if (_page2MaterialViewRetryCount >= 12)
                {
                    _page2MaterialViewRetryTimer.Stop();
                }
            };
        }
        _page2MaterialViewRetryTimer.Stop();
        _page2MaterialViewRetryTimer.Start();
        _ = Dispatcher.BeginInvoke(async () => await EnsurePage2MaterialViewLoadedAsync(reason), DispatcherPriority.Loaded);
    }

    private async Task EnsurePage2MaterialViewLoadedAsync(string reason = "manual")
    {
        try
        {
            var mapView = FindPage2MaterialViewChild<MapView>(this);
            if (mapView == null)
            {
                AppendPage2MaterialViewStatus("Material_View_MA not loaded yet: no ArcGIS MapView found (" + reason + ").");
                return;
            }
            if (mapView.Map == null)
            {
                mapView.Map = new Map();
            }
            var existing = mapView.Map.OperationalLayers.FirstOrDefault(l => string.Equals(l.Name, "Material_View_MA", StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsVisible = true;
                return;
            }
            var materialLayer = new ArcGISMapImageLayer(new Uri(Page2MaterialViewServiceUrl))
            {
                Name = "Material_View_MA",
                IsVisible = true
            };
            mapView.Map.OperationalLayers.Insert(0, materialLayer);
            await materialLayer.LoadAsync();
            if (materialLayer.LoadStatus == Esri.ArcGISRuntime.LoadStatus.Loaded)
            {
                if (materialLayer.FullExtent != null)
                {
                    await mapView.SetViewpointGeometryAsync(materialLayer.FullExtent, 250);
                }
                AppendPage2MaterialViewStatus("Material_View_MA loaded on Page 2 (" + reason + ") from webmap item " + Page2MaterialViewWebMapItemId + ". Operational layer count: " + mapView.Map.OperationalLayers.Count + ".");
            }
            else if (materialLayer.LoadError != null)
            {
                AppendPage2MaterialViewStatus("Material_View_MA load error: " + materialLayer.LoadError.GetType().Name + ": " + materialLayer.LoadError.Message);
            }
        }
        catch (Exception ex)
        {
            AppendPage2MaterialViewStatus("Material_View_MA Page 2 load failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static T? FindPage2MaterialViewChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindPage2MaterialViewChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void AppendPage2MaterialViewStatus(string message)
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