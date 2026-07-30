using System.Text.Json;
using Esri.ArcGISRuntime.Geometry;
using NG.GIS.CAD.Exporter.Models;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// Drives the actual write into the drawing: fetch the features for every selected layer, hand them
/// to the CAD writer along with the strip map index, and report what was written.
/// </summary>
public sealed partial class ExporterViewModel
{
    /// <summary>
    /// The strip map sheets as last laid out on page 2, in Web Mercator. Set by the view, which owns
    /// the layout, and read here so the export can put them in the drawing.
    /// </summary>
    public IReadOnlyList<StripMapSheet> StripMapSheets { get; set; } = Array.Empty<StripMapSheet>();

    /// <summary>The layer the strip map frames go on. Its own, because the index is not GIS data.</summary>
    public string StripMapCadLayerName { get; set; } = "GIS_STRIP_MAP_INDEX";

    private bool _includeBasemapInExport;
    private BasemapChoice _selectedExportBasemap = BasemapImageService.DefaultChoices[0];
    private string _basemapCadLayerName = "GIS_BASEMAP";
    private int _basemapImagePixels = 2048;
    private string _customBasemapUrl = string.Empty;

    /// <summary>The basemaps offered for the export, plus None.</summary>
    public IReadOnlyList<BasemapChoice> ExportBasemapChoices { get; } = BasemapImageService.DefaultChoices;

    /// <summary>
    /// Whether a basemap image goes into the drawing. Off by default: it is a real raster file written
    /// beside the drawing, which the drawing then depends on, and that is not something to do unasked.
    /// </summary>
    public bool IncludeBasemapInExport
    {
        get => _includeBasemapInExport;
        set => SetProperty(ref _includeBasemapInExport, value);
    }

    public BasemapChoice SelectedExportBasemap
    {
        get => _selectedExportBasemap;
        set => SetProperty(ref _selectedExportBasemap, value);
    }

    /// <summary>
    /// A service to use instead of the listed ones, for a basemap this organisation publishes itself.
    /// Takes precedence over the dropdown when it is filled in.
    /// </summary>
    public string CustomBasemapUrl
    {
        get => _customBasemapUrl;
        set => SetProperty(ref _customBasemapUrl, value);
    }

    /// <summary>The layer the basemap raster is placed on, so it can be turned off on its own.</summary>
    public string BasemapCadLayerName
    {
        get => _basemapCadLayerName;
        set => SetProperty(ref _basemapCadLayerName, value);
    }

    /// <summary>
    /// Long edge of the basemap image in pixels. More pixels means a sharper backdrop and a larger file;
    /// a service will not return more than 4096 on one request and says nothing when it clamps.
    /// </summary>
    public int BasemapImagePixels
    {
        get => _basemapImagePixels;
        set => SetProperty(ref _basemapImagePixels, Math.Clamp(value, 256, BasemapImageService.MaxImagePixels));
    }

    /// <summary>The service the export will actually ask, once the custom URL is taken into account.</summary>
    private string ResolveBasemapServiceUrl() =>
        string.IsNullOrWhiteSpace(CustomBasemapUrl) ? SelectedExportBasemap.ServiceUrl : CustomBasemapUrl.Trim();

    /// <summary>
    /// Writes the selected layers into the open drawing.
    ///
    /// The extent and the selections come from the earlier pages, so this only reports what is missing
    /// rather than trying to work around it: exporting the wrong area silently would be worse.
    /// </summary>
    public async Task ExportToCadAsync()
    {
        try
        {
            await EnsureLayerMetadataLoadedAsync(announce: false);

            if (_resolvedExtent == null)
            {
                Status = "No extent is set. Choose a method on page 1 and commit an extent on page 2 first.";
                return;
            }

            var selected = Layers.Where(l => l.Enabled).ToList();
            if (selected.Count == 0)
            {
                Status = "No layers are selected for export. Tick at least one on page 3.";
                return;
            }

            var outWkid = _profile.DefaultOutputSpatialReferenceWkid;
            var request = new CadExportRequest
            {
                StripMapLayerName = StripMapCadLayerName,
                TemplatePath = HasTemplate ? TemplatePath : null,
                StripMapLabelHeight = 10.0
            };

            Status = $"Fetching features for {selected.Count} layer(s)...";
            var totalFeatures = 0;

            foreach (var layer in selected)
            {
                var transform = TransformRules
                    .FirstOrDefault(t => string.Equals(t.LayerUrl, layer.Url, StringComparison.OrdinalIgnoreCase));

                var fields = layer.Fields.Where(f => f.Selected).Select(f => f.Name).ToList();
                var features = await _services.ArcGisRestClient.QueryFeaturesAsync(
                    layer.Url, _resolvedExtent, fields, outWkid, CancellationToken.None);

                var layerFeatures = new ExportLayerFeatures
                {
                    LayerName = layer.Name,
                    GeometryType = layer.GeometryType,
                    Transform = transform?.Rule ?? new CadTransformRule { CadLayerName = layer.Name }
                };
                layerFeatures.Features.AddRange(features);
                request.Layers.Add(layerFeatures);
                totalFeatures += features.Count;
            }

            AddStripMapSheetsToRequest(request, outWkid);
            await AddBasemapToRequestAsync(request, outWkid);

            Status = $"Writing {totalFeatures} feature(s) into the drawing...";
            var result = _services.CadExportWriter.Write(request);

            Status = DescribeExportResult(result, totalFeatures, outWkid);
        }
        catch (Exception ex)
        {
            Status = "Export to CAD failed: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Downloads the chosen basemap over the export extent and adds it to the request.
    ///
    /// The extent is requested in the output spatial reference for both the bounding box and the image,
    /// so the pixels are already in drawing coordinates. That is what lets the raster be placed on the
    /// extent it was asked for with no transform, and it is why the basemap lands accurately while the
    /// strip map frames go through a projection.
    /// </summary>
    private async Task AddBasemapToRequestAsync(CadExportRequest request, int outWkid)
    {
        if (!IncludeBasemapInExport || _resolvedExtent == null) { return; }

        var serviceUrl = ResolveBasemapServiceUrl();
        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            Status = "No basemap was chosen, so none was included. Pick one, or clear the include tick.";
            return;
        }

        try
        {
            Status = "Fetching the basemap image...";

            // Beside the profile, which is where this plug-in already keeps what it writes. The drawing
            // will reference this file by path, so it has to live somewhere lasting rather than in temp.
            var directory = Path.Combine(
                Path.GetDirectoryName(ProfilePath) ?? Path.GetTempPath(), "basemaps");

            var image = await _services.BasemapImageService.DownloadAsync(
                serviceUrl, _resolvedExtent, outWkid, BasemapImagePixels, directory, CancellationToken.None);

            // The extent is in its own WKID, which may not be the drawing's. The image was requested in
            // the output WKID, so the corners have to be too, or the raster would be placed at the
            // extent's coordinates rather than the drawing's.
            var corners = ProjectExtentCorners(_resolvedExtent, outWkid);

            request.Basemap = new BasemapImagePlacement
            {
                ImagePath = image.ImagePath,
                LayerName = BasemapCadLayerName,
                OriginX = corners.MinX,
                OriginY = corners.MinY,
                Width = corners.MaxX - corners.MinX,
                Height = corners.MaxY - corners.MinY
            };
        }
        catch (Exception ex)
        {
            // The features are the export. A basemap that will not come down should not stop them.
            Status = "The basemap could not be fetched, so the export continued without it: " + ex.Message;
        }
    }

    /// <summary>
    /// The export extent's corners in the output spatial reference. Returned unchanged when the extent
    /// is already in it, which is the common case and avoids a needless projection.
    /// </summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) ProjectExtentCorners(
        ExportExtent extent, int outWkid)
    {
        if (extent.Wkid == outWkid)
        {
            return (Math.Min(extent.XMin, extent.XMax), Math.Min(extent.YMin, extent.YMax),
                    Math.Max(extent.XMin, extent.XMax), Math.Max(extent.YMin, extent.YMax));
        }

        var source = new Envelope(extent.XMin, extent.YMin, extent.XMax, extent.YMax, SpatialReference.Create(extent.Wkid));
        if (GeometryEngine.Project(source, SpatialReference.Create(outWkid)) is not Envelope projected)
        {
            throw new InvalidOperationException(
                "The export extent could not be projected from WKID " + extent.Wkid + " to WKID " + outWkid + ".");
        }

        return (projected.XMin, projected.YMin, projected.XMax, projected.YMax);
    }

    /// <summary>
    /// Projects the strip map sheets into the output spatial reference and adds them to the request.
    ///
    /// The sheets are laid out in Web Mercator, which is what the page 2 map works in, but the drawing
    /// is in the output spatial reference. Projecting the frames rather than recomputing them keeps the
    /// sheets in the drawing identical to the ones shown on the map.
    /// </summary>
    private void AddStripMapSheetsToRequest(CadExportRequest request, int outWkid)
    {
        if (StripMapSheets.Count == 0) { return; }

        SpatialReference target;
        try
        {
            target = SpatialReference.Create(outWkid);
        }
        catch (Exception ex)
        {
            Status = "The strip map index could not be projected for the drawing: " + ex.Message;
            return;
        }

        var labelHeight = 0.0;

        foreach (var sheet in StripMapSheets)
        {
            var projected = GeometryEngine.Project(sheet.Outline, target) as Polygon;
            if (projected == null || projected.IsEmpty) { continue; }

            var corners = ReadRingVertices(projected);
            if (corners.Count < 3) { continue; }

            var exported = new ExportStripMapSheet
            {
                Number = sheet.Number,
                RotationDegrees = sheet.RotationDegrees,
                LabelX = corners.Average(c => c.X),
                LabelY = corners.Average(c => c.Y)
            };
            exported.Corners.AddRange(corners);
            request.StripMapSheets.Add(exported);

            // Sized from the frame itself rather than from a fixed number, so the label suits the
            // drawing's units whether they are feet or metres, and suits the scale it was laid out at.
            if (labelHeight <= 0)
            {
                var edge = Math.Sqrt(
                    Math.Pow(corners[1].X - corners[0].X, 2) + Math.Pow(corners[1].Y - corners[0].Y, 2));
                labelHeight = edge * 0.03;
            }
        }

        if (labelHeight > 0) { request.StripMapLabelHeight = labelHeight; }
    }

    /// <summary>
    /// Pulls the ring coordinates out of a projected frame. Read from the geometry's JSON, which is how
    /// the rest of this codebase takes ArcGIS geometry apart.
    /// </summary>
    private static List<ExportVertex> ReadRingVertices(Polygon polygon)
    {
        var vertices = new List<ExportVertex>();

        using var document = JsonDocument.Parse(polygon.ToJson());
        if (!document.RootElement.TryGetProperty("rings", out var rings) || rings.ValueKind != JsonValueKind.Array)
        {
            return vertices;
        }

        foreach (var ring in rings.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array) { continue; }
            foreach (var point in ring.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array) { continue; }
                var values = point.EnumerateArray().ToArray();
                if (values.Length < 2) { continue; }
                if (values[0].TryGetDouble(out var x) && values[1].TryGetDouble(out var y))
                {
                    vertices.Add(new ExportVertex(x, y));
                }
            }

            // Only the outer ring is wanted: a sheet frame is one rectangle.
            break;
        }

        // The JSON repeats the first point to close the ring, which the writer does with Closed.
        if (vertices.Count > 3
            && Math.Abs(vertices[0].X - vertices[^1].X) < 1e-9
            && Math.Abs(vertices[0].Y - vertices[^1].Y) < 1e-9)
        {
            vertices.RemoveAt(vertices.Count - 1);
        }

        return vertices;
    }

    private static string DescribeExportResult(CadExportResult result, int totalFeatures, int outWkid)
    {
        var message = $"Exported {totalFeatures} feature(s) as {result.EntitiesWritten} entit(ies) in WKID {outWkid}.";

        if (result.StripMapSheetsWritten > 0)
        {
            message += $" Strip map index: {result.StripMapSheetsWritten} sheet(s).";
        }
        if (result.BasemapPlaced)
        {
            message += " Basemap placed behind the features.";
        }
        if (result.CadLayersCreated.Count > 0)
        {
            message += $" Layers created: {string.Join(", ", result.CadLayersCreated)}.";
        }
        if (result.Warnings.Count > 0)
        {
            message += " " + string.Join(" ", result.Warnings);
        }

        return message;
    }
}
