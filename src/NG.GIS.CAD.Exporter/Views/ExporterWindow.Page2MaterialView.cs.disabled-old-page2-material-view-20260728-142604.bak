using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.UI.Controls;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private bool _page2WebMapAutoloadInstalled;
    private DispatcherTimer? _page2WebMapRetryTimer;
    private int _page2WebMapRetryCount;
    private const string Page2PortalRootUrl = "https://gis.nationalgrid.com/portal";
    private const string Page2GasMaterialViewWebMapItemId = "c214d72caefb40699b129bc47b1b22a7";

    private void InstallPage2MaterialViewAutoload()
    {
        if (_page2WebMapAutoloadInstalled) return;
        _page2WebMapAutoloadInstalled = true;
        Loaded += async (_, __) => await EnsureGasMaterialViewWebMapLoadedAsync("window loaded");
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => ScheduleGasMaterialViewWebMapRetries("button action")), true);
        ScheduleGasMaterialViewWebMapRetries("install");
    }

    private void ScheduleGasMaterialViewWebMapRetries(string reason)
    {
        _page2WebMapRetryCount = 0;
        if (_page2WebMapRetryTimer == null)
        {
            _page2WebMapRetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _page2WebMapRetryTimer.Tick += async (_, __) =>
            {
                _page2WebMapRetryCount++;
                await EnsureGasMaterialViewWebMapLoadedAsync("retry " + _page2WebMapRetryCount);
                if (_page2WebMapRetryCount >= 15)
                {
                    _page2WebMapRetryTimer.Stop();
                }
            };
        }
        _page2WebMapRetryTimer.Stop();
        _page2WebMapRetryTimer.Start();
        _ = Dispatcher.BeginInvoke(async () => await EnsureGasMaterialViewWebMapLoadedAsync(reason), DispatcherPriority.Loaded);
    }

    private async Task EnsureGasMaterialViewWebMapLoadedAsync(string reason = "manual")
    {
        try
        {
            var mapView = FindPage2GasMaterialViewChild<MapView>(this);
            if (mapView == null)
            {
                AppendPage2MaterialViewStatus("GasMaterialView_MA webmap not loaded yet: no ArcGIS MapView found (" + reason + ").");
                return;
            }
            var currentItemId = mapView.Map?.Item?.ItemId;
            if (string.Equals(currentItemId, Page2GasMaterialViewWebMapItemId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var portal = await ArcGISPortal.CreateAsync(new Uri(Page2PortalRootUrl));
            var item = await PortalItem.CreateAsync(portal, Page2GasMaterialViewWebMapItemId);
            var webMap = new Map(item);
            mapView.Map = webMap;
            await webMap.LoadAsync();
            AppendPage2MaterialViewStatus("GasMaterialView_MA webmap loaded on Page 2 (" + reason + "). Operational layer count: " + webMap.OperationalLayers.Count + ".");
        }
        catch (Exception ex)
        {
            AppendPage2MaterialViewStatus("GasMaterialView_MA webmap load failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static T? FindPage2GasMaterialViewChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindPage2GasMaterialViewChild<T>(child);
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