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

    /// <summary>
    /// The route as it was picked, for the methods that draw one. Kept because the corridor is what the
    /// export is scoped to, and a bounding box cannot be turned back into the line that produced it.
    /// Empty for the methods that are a rectangle to begin with.
    /// </summary>
    public List<RouteVertex> RouteVertices { get; } = new();
}

/// <summary>A picked route point, in the extent's own spatial reference.</summary>
public readonly struct RouteVertex
{
    public RouteVertex(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
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

    /// <summary>
    /// Whether polygon features are filled with a hatch as well as outlined.
    ///
    /// Off by default, and only ever acted on for polygon layers. An outline says where a boundary runs;
    /// a fill says the inside belongs to something, which is the useful reading for a parcel, an
    /// easement or a work area but is noise on a layer nobody is reading that way.
    /// </summary>
    public bool HatchPolygons { get; set; }

    /// <summary>
    /// The hatch pattern, by the name AutoCAD knows it: SOLID, ANSI31, EARTH and the rest that ship with
    /// it. SOLID because a filled area reads at any zoom, where a line pattern picked without knowing the
    /// scale is as likely to come out as a solid block or as nothing at all.
    /// </summary>
    public string HatchPattern { get; set; } = "SOLID";

    /// <summary>
    /// Spacing multiplier for a line pattern. Ignored by SOLID, which has nothing to space, and only
    /// matters once a pattern has been chosen that draws lines.
    /// </summary>
    public double HatchScale { get; set; } = 1.0;

    /// <summary>
    /// How see-through the fill is, 0 for opaque and 90 for nearly invisible, which is AutoCAD's own
    /// range for entity transparency.
    ///
    /// Defaults to mostly transparent, because a fill drawn over the top of the features it describes
    /// would otherwise hide them, and the point of hatching a polygon here is to show what it covers
    /// rather than to replace it.
    /// </summary>
    public int HatchTransparencyPercent { get; set; } = 70;
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
