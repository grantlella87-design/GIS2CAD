using NG.GIS.CAD.Exporter.Models;
namespace NG.GIS.CAD.Exporter.ViewModels;
public sealed partial class ExporterViewModel
{
    private bool _showMapLegend;

    /// <summary>
    /// Whether the layer tree shows each layer's symbols. Off to begin with: a layer drawn by category
    /// takes a row per class, and on a portal item this size that turns the list of layers into a wall
    /// of swatches before the user has asked for one. Ticking Legend brings them back.
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
