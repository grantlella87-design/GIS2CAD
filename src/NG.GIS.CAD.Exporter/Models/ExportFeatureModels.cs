namespace NG.GIS.CAD.Exporter.Models;

/// <summary>A vertex in the output spatial reference, so already in drawing units.</summary>
public readonly struct ExportVertex
{
    public ExportVertex(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

/// <summary>
/// One feature on its way into the drawing.
///
/// Geometry is flattened to parts: a point has one part of one vertex, a polyline one part per path,
/// a polygon one part per ring. That is all the writer needs, and it keeps the CAD side free of the
/// ArcGIS geometry types.
/// </summary>
public sealed class ExportFeature
{
    public List<List<ExportVertex>> Parts { get; } = new();

    /// <summary>
    /// Attribute values for the fields chosen on page 3, keyed by field name. Carried so a rotation
    /// or scale driven by a field can be read, and so the writer can label what it draws.
    /// </summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Everything to draw for one GIS layer, with the rule that says how to draw it.</summary>
public sealed class ExportLayerFeatures
{
    public string LayerName { get; init; } = string.Empty;
    public string GeometryType { get; init; } = string.Empty;
    public CadTransformRule Transform { get; init; } = new();
    public List<ExportFeature> Features { get; } = new();
}

/// <summary>One strip map sheet frame, in the output spatial reference.</summary>
public sealed class ExportStripMapSheet
{
    public int Number { get; init; }
    public double LabelX { get; init; }
    public double LabelY { get; init; }

    /// <summary>Bearing the sheet is turned to, in degrees, so the number reads along the frame.</summary>
    public double RotationDegrees { get; init; }

    /// <summary>The four corners of the frame. Closed by the writer rather than repeated here.</summary>
    public List<ExportVertex> Corners { get; } = new();
}

/// <summary>What to write into the drawing.</summary>
public sealed class CadExportRequest
{
    public List<ExportLayerFeatures> Layers { get; } = new();

    /// <summary>
    /// Strip map sheet frames. These go onto their own CAD layer rather than onto any GIS layer's,
    /// because the index is not a GIS feature: it is a drafting aid that describes the sheet set.
    /// </summary>
    public List<ExportStripMapSheet> StripMapSheets { get; } = new();

    /// <summary>The layer the strip map frames and their numbers are drawn on.</summary>
    public string StripMapLayerName { get; init; } = "GIS_STRIP_MAP_INDEX";

    /// <summary>
    /// Height of the sheet number text, in drawing units. Settable rather than init-only because it is
    /// measured from the projected frames, which is only known once the sheets have been added.
    /// </summary>
    public double StripMapLabelHeight { get; set; } = 10.0;

    /// <summary>Template to pull missing blocks and line types from, when one was chosen on page 1.</summary>
    public string? TemplatePath { get; init; }
}

/// <summary>What the writer did, so the review page can report it rather than claim success.</summary>
public sealed class CadExportResult
{
    public int EntitiesWritten { get; set; }
    public int StripMapSheetsWritten { get; set; }
    public List<string> CadLayersCreated { get; } = new();

    /// <summary>
    /// Things that were drawn differently than asked, each said once. A block that is not in the
    /// drawing or the template, or a line type that could not be loaded, does not stop the export,
    /// but the export should not pretend it went exactly as specified either.
    /// </summary>
    public List<string> Warnings { get; } = new();
}
