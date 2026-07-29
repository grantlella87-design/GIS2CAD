using NG.GIS.CAD.Exporter.Models;
namespace NG.GIS.CAD.Exporter.ViewModels;
public sealed partial class ExporterViewModel
{
    private bool _showMapLegend = true;

    /// <summary>
    /// Whether the layer tree shows each layer's symbols. On by default, because a legend is the
    /// point of asking for one, but a layer drawn by category takes a row per class and on a large
    /// portal item that adds up, so it can be turned off to get the compact list back.
    /// </summary>
    public bool ShowMapLegend
    {
        get => _showMapLegend;
        set => SetProperty(ref _showMapLegend, value);
    }

    public ExportExtent? CurrentExtent => _resolvedExtent;
    public void SetExtentFromMap(double xmin, double ymin, double xmax, double ymax, int wkid)
    {
        _resolvedExtent = new ExportExtent
        {
            Mode = "ArcGISMapViewVisibleExtent",
            XMin = xmin,
            YMin = ymin,
            XMax = xmax,
            YMax = ymax,
            Wkid = wkid,
            PaddingFeet = 0
        };
        RaisePropertyChanged(nameof(CurrentExtent));
        RaisePropertyChanged(nameof(ResolvedExtentText));
        Status = "ArcGIS MapView visible extent captured and set as export extent.";
    }
}
