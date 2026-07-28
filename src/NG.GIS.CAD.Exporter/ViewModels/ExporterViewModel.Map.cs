using NG.GIS.CAD.Exporter.Models;
namespace NG.GIS.CAD.Exporter.ViewModels;
public sealed partial class ExporterViewModel
{
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
