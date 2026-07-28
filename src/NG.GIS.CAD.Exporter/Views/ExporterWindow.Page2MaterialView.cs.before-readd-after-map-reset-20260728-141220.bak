using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private bool _page2MaterialViewLoadStarted;
    private const string Page2MaterialViewServiceUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";
    private const string Page2MaterialViewWebMapItemId = "c214d72caefb40699b129bc47b1b22a7";

    private async Task EnsurePage2MaterialViewLoadedAsync()
    {
        if (_page2MaterialViewLoadStarted) return;
        _page2MaterialViewLoadStarted = true;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            var mapView = FindPage2MaterialViewChild<MapView>(this);
            if (mapView == null)
            {
                AppendPage2MaterialViewStatus("Material view load skipped: no ArcGIS MapView was found in the window visual tree.");
                return;
            }
            if (mapView.Map == null)
            {
                mapView.Map = new Map();
            }
            var alreadyLoaded = mapView.Map.OperationalLayers.Any(l => string.Equals(l.Name, "Material_View_MA", StringComparison.OrdinalIgnoreCase));
            if (!alreadyLoaded)
            {
                var materialLayer = new ArcGISMapImageLayer(new Uri(Page2MaterialViewServiceUrl)) { Name = "Material_View_MA" };
                mapView.Map.OperationalLayers.Insert(0, materialLayer);
                await materialLayer.LoadAsync();
                if (materialLayer.FullExtent != null)
                {
                    await mapView.SetViewpointGeometryAsync(materialLayer.FullExtent, 250);
                }
                AppendPage2MaterialViewStatus("Material_View_MA loaded on Page 2 from webmap item " + Page2MaterialViewWebMapItemId + ".");
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
            if (textBox != null)
            {
                textBox.Text = string.IsNullOrWhiteSpace(textBox.Text) ? message : textBox.Text + Environment.NewLine + message;
            }
        }
        catch { }
    }
}