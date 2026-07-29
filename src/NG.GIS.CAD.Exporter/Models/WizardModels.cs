namespace NG.GIS.CAD.Exporter.Models;
public enum ExportMethod
{
    WorkOrder,
    DrawPipelineRoute,
    CurrentDrawingView,
    ManualExtent
}
public sealed class ExportExtent
{
    public string Mode { get; set; } = string.Empty;
    public string WorkOrderId { get; set; } = string.Empty;
    public double XMin { get; set; }
    public double YMin { get; set; }
    public double XMax { get; set; }
    public double YMax { get; set; }
    public int Wkid { get; set; } = 2249;
    public double PaddingFeet { get; set; } = 300;
}
public sealed class CadTransformRule
{
    public string LayerUrl { get; set; } = string.Empty;
    public string LayerName { get; set; } = string.Empty;
    public string GeometryType { get; set; } = string.Empty;
    public string CadLayerName { get; set; } = string.Empty;
    public string BlockName { get; set; } = string.Empty;
    public string LineType { get; set; } = "ByLayer";
    public string ColorMode { get; set; } = "ByLayer";
    public int AciColor { get; set; } = 256;

    /// <summary>
    /// The picked colour as "#RRGGBB". Carries the value for RGB mode, and doubles as the swatch for
    /// an ACI pick, since the colour dialog resolves an index to its RGB anyway.
    /// </summary>
    public string RgbColor { get; set; } = string.Empty;
    public double RotationOffsetDegrees { get; set; }
    public double ScaleValue { get; set; } = 1.0;
    public string RotationField { get; set; } = string.Empty;
    public string ScaleField { get; set; } = string.Empty;
}
public sealed class ExportPlanLayer
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string GeometryType { get; set; } = string.Empty;
    public int FeatureCount { get; set; }
    public List<string> SelectedFields { get; set; } = new();
    public CadTransformRule Transform { get; set; } = new();
}
public sealed class ExportPlan
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ExportExtent Extent { get; set; } = new();
    public List<ExportPlanLayer> Layers { get; set; } = new();
}
