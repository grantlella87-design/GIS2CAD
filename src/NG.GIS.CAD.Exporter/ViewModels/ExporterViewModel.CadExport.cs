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
