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

    /// <summary>
    /// The basemap image to place under the features, or null for none. Set once the image has been
    /// downloaded, so the writer only ever deals with a file already on disk.
    /// </summary>
    public BasemapImagePlacement? Basemap { get; set; }

    /// <summary>
    /// The export extent, and the padding buffer around the proposed main where there is one. Their own
    /// layer, because they describe what was asked for rather than anything found in the ground, and
    /// because being able to switch off the boundary you worked to is the point of having it separate.
    /// </summary>
    public List<ExportOutline> ExtentOutlines { get; } = new();

    public string ExtentLayerName { get; init; } = "GIS_EXPORT_EXTENT";
}

/// <summary>A boundary drawn as a polyline, with a name saying which boundary it is.</summary>
public sealed class ExportOutline
{
    public string Label { get; init; } = string.Empty;
    public List<ExportVertex> Vertices { get; } = new();
}

/// <summary>
/// A basemap image and the ground it covers, in drawing coordinates.
///
/// The corners are what georeference it: AutoCAD places a raster from an origin and two vectors giving
/// the full width and height of the image, so the extent the image was requested for is exactly the
/// extent it has to be placed at.
/// </summary>
public sealed class BasemapImagePlacement
{
    public required string ImagePath { get; init; }
    public required string LayerName { get; init; }

    /// <summary>Lower left corner of the image, in drawing units.</summary>
    public double OriginX { get; init; }
    public double OriginY { get; init; }

    /// <summary>Ground size the image spans, in drawing units.</summary>
    public double Width { get; init; }
    public double Height { get; init; }

    /// <summary>Name the image is filed under in the drawing's image dictionary.</summary>
    public string DefinitionName { get; init; } = "NGGIS_BASEMAP";
}

/// <summary>What the writer did, so the review page can report it rather than claim success.</summary>
public sealed class CadExportResult
{
    public int EntitiesWritten { get; set; }
    public int StripMapSheetsWritten { get; set; }
    public int ExtentOutlinesWritten { get; set; }
    public bool BasemapPlaced { get; set; }
    public List<string> CadLayersCreated { get; } = new();

    /// <summary>
    /// Things that were drawn differently than asked, each said once. A block that is not in the
    /// drawing or the template, or a line type that could not be loaded, does not stop the export,
    /// but the export should not pretend it went exactly as specified either.
    /// </summary>
    public List<string> Warnings { get; } = new();
}
