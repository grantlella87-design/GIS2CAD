using System.Globalization;
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
    /// The padding buffer drawn around the proposed main on page 2, in Web Mercator. Set by the view
    /// whenever it draws one, so the export can put the same shape in the drawing rather than a
    /// recomputed one that might differ.
    /// </summary>
    public Geometry? ProposedMainBufferOutline { get; set; }

    private bool _includeBoundaryInExport = true;
    private string _boundaryCadLayerName = "GIS_EXPORT_BOUNDARY";
    private bool _attachAttributesToEntities = true;
    private string _attributeAppName = "NGGIS";

    /// <summary>
    /// Whether the fields chosen on page 3 travel with the entities drawn from them.
    ///
    /// On by default. The fields are already fetched, and until now nothing but a block's rotation or
    /// scale read them, so choosing a field and having it reach the drawing as nothing was not what
    /// choosing it looked like.
    /// </summary>
    public bool AttachAttributesToEntities
    {
        get => _attachAttributesToEntities;
        set => SetProperty(ref _attachAttributesToEntities, value);
    }

    /// <summary>The name the extended data is filed under, so it can be found again in the drawing.</summary>
    public string AttributeAppName
    {
        get => _attributeAppName;
        set => SetProperty(ref _attributeAppName, value);
    }

    /// <summary>
    /// Anything the export did differently from what the settings imply, read once it has finished: a
    /// boundary that is not the one the method calls for, or an index that was left out.
    ///
    /// Held rather than announced because the status line is rewritten several times on the way through,
    /// and a warning that scrolls past unread is no warning at all.
    /// </summary>
    private string _exportNote = string.Empty;

    /// <summary>Adds to the note rather than replacing it, so a second reason is not lost to the first.</summary>
    private void AddExportNote(string note) =>
        _exportNote = string.IsNullOrEmpty(_exportNote) ? note : _exportNote + " " + note;

    /// <summary>
    /// Whether the boundary that scoped the import is drawn in the drawing. On by default: it is one
    /// polyline on a layer of its own, and knowing where the exported area stops is worth having.
    /// </summary>
    public bool IncludeBoundaryInExport
    {
        get => _includeBoundaryInExport;
        set => SetProperty(ref _includeBoundaryInExport, value);
    }

    public string BoundaryCadLayerName
    {
        get => _boundaryCadLayerName;
        set => SetProperty(ref _boundaryCadLayerName, value);
    }

    /// <summary>
    /// Which shape scopes the import, decided by the method chosen on page 1 rather than by a setting.
    ///
    /// A work order or a drawn pipeline is a corridor: the padding buffer around the main is what was
    /// actually asked about, and a rectangle around a main running diagonally would pull in a great deal
    /// of ground nobody wanted. The current drawing view is a rectangle to begin with — it is literally
    /// what is on screen — and a typed extent is one by definition, so buffering either would invent a
    /// shape the user never drew.
    /// </summary>
    public ExportBoundaryKind BoundaryKind => SelectedMethod switch
    {
        ExportMethod.WorkOrder => ExportBoundaryKind.Buffer,
        ExportMethod.DrawPipelineRoute => ExportBoundaryKind.Buffer,
        _ => ExportBoundaryKind.ExtentBox
    };

    /// <summary>What the review page says about the boundary, so the scope is stated before the write.</summary>
    public string BoundaryDescription => BoundaryKind == ExportBoundaryKind.Buffer
        ? $"Everything intersecting the {PaddingFeet:0.#} ft buffer around the proposed main is imported, "
          + "and that buffer is the only boundary drawn. The extent rectangle around it is not written."
        : "Everything intersecting the extent bounding box is imported, and that box is what gets drawn.";

    private bool _includeBasemapInExport;
    private BasemapChoice _selectedExportBasemap = BasemapImageService.DefaultChoice;
    private string _basemapCadLayerName = "GIS_BASEMAP";
    private int _basemapImagePixels = 2048;
    private string _customBasemapUrl = string.Empty;

    /// <summary>The basemaps offered for the export, plus None.</summary>
    public IReadOnlyList<BasemapChoice> ExportBasemapChoices { get; } = BasemapImageService.DefaultChoices;

    /// <summary>
    /// Whether a basemap image goes into the drawing.
    ///
    /// Off by default. Seeing where the export landed is what AutoCAD's own geographic map is for, and
    /// that is switched on after every export without writing anything: it costs the drawing nothing
    /// and can be turned off again from AutoCAD.
    ///
    /// This is the other kind of basemap, a real raster written beside the profile that the drawing
    /// then references by path, so the drawing gains a dependency on a file that can be moved or
    /// deleted. That is worth having when the backdrop must travel with the drawing to someone who
    /// cannot fetch a map themselves, which is a deliberate choice rather than a default.
    ///
    /// When it is turned on, <see cref="SelectedExportBasemap"/> already names a real service, so the
    /// tick alone is enough and does not silently produce nothing.
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
                BoundaryLayerName = BoundaryCadLayerName,
                TemplatePath = HasTemplate ? TemplatePath : null,
                StripMapLabelHeight = 10.0,
                AttachAttributes = AttachAttributesToEntities,
                AttributeAppName = AttributeAppName
            };

            // Worked out once, before anything is fetched, and then used for both the queries and the
            // outline written into the drawing. That is the whole point: the layer the drawing ends up
            // with is the shape that decided what came in, not a second shape drawn to look like it.
            var boundary = BuildExportBoundary(outWkid);

            Status = $"Fetching features for {selected.Count} layer(s)...";
            var totalFeatures = 0;

            foreach (var layer in selected)
            {
                var transform = TransformRules
                    .FirstOrDefault(t => string.Equals(t.LayerUrl, layer.Url, StringComparison.OrdinalIgnoreCase));

                var fields = layer.Fields.Where(f => f.Selected).Select(f => f.Name).ToList();

                // The rotation field is fetched whether or not it was ticked on page 3. Those ticks say
                // which attributes travel into the drawing as data; this one is read to place the block
                // and need not be written out at all. Asking only for the ticked ones would leave the
                // rotation field out of the answer, and every block would quietly come back at the
                // default angle -- the setting doing nothing, with nothing to say it had not.
                var rotationField = transform?.Rule.RotationField;
                if (!string.IsNullOrWhiteSpace(rotationField)
                    && fields.Count > 0
                    && !fields.Contains(rotationField, StringComparer.OrdinalIgnoreCase))
                {
                    fields.Add(rotationField);
                }

                var features = await _services.ArcGisRestClient.QueryFeaturesAsync(
                    layer.Url, _resolvedExtent, fields, outWkid, CancellationToken.None, boundary);

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
            AddBoundaryOutlineToRequest(request, boundary);
            await AddBasemapToRequestAsync(request, outWkid, boundary);

            Status = $"Writing {totalFeatures} feature(s) into the drawing...";
            var result = _services.CadExportWriter.Write(request);

            // Only once the write has succeeded. Turning the map on after a failed export would put a
            // backdrop under nothing and read as though something had landed.
            //
            // Anchored at the centre of what was exported, in drawing coordinates. Any point in the
            // drawing would do, since the drawing is already in the output system and the location is
            // an identity, but the middle of the work is the one that stays sensible if that ever
            // stops being true.
            var anchorX = boundary != null ? (boundary.XMin + boundary.XMax) / 2.0 : 0.0;
            var anchorY = boundary != null ? (boundary.YMin + boundary.YMax) / 2.0 : 0.0;
            var geoMap = await _services.CadGeoMapService.TurnOnAsync(outWkid, anchorX, anchorY);

            var message = DescribeExportResult(result, totalFeatures, outWkid, boundary);
            Status = string.IsNullOrEmpty(_exportNote) ? message : message + " " + _exportNote;
            Status += " " + geoMap;
        }
        catch (Exception ex)
        {
            Status = "Export to CAD failed: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// Works out the one shape that scopes this export, in the output spatial reference.
    ///
    /// Built in drawing coordinates rather than the extent's own so a single object can serve both the
    /// query and the outline written into the drawing. The service is told which spatial reference the
    /// query geometry is in, so handing it drawing coordinates costs nothing and removes the chance of
    /// the drawn boundary and the queried one being different shapes.
    ///
    /// Returns null when no boundary could be worked out, which leaves the query on the plain extent it
    /// has always used. That is the old behaviour, so a failure here narrows nothing.
    /// </summary>
    private ExportBoundary? BuildExportBoundary(int outWkid)
    {
        _exportNote = string.Empty;
        if (_resolvedExtent == null) { return null; }

        if (BoundaryKind == ExportBoundaryKind.Buffer)
        {
            var buffer = BuildBufferBoundary(outWkid);
            if (buffer != null) { return buffer; }

            // No buffer to be had: no padding was set, or no main was imported or drawn. The box is then
            // the only honest answer, and saying so matters because it is a wider scope than was asked
            // for rather than a narrower one.
            //
            // Kept for the end rather than put on the status line now, because everything that follows
            // overwrites the status and the one line the user reads is the one left when it finishes.
            AddExportNote("This method scopes to the padding buffer, but none was available, so the "
                            + "extent bounding box was used to fetch and more ground came in than the "
                            + "corridor. No boundary was drawn, because a rectangle is not the shape you "
                            + "asked to work to. Set a padding distance on page 2 to scope to the "
                            + "corridor instead.");
        }

        return BuildExtentBoxBoundary(outWkid);
    }

    /// <summary>
    /// The padding buffer, from the map where one was drawn and from the picked route otherwise.
    ///
    /// The map's buffer is preferred and handed over rather than recomputed, so the shape in the drawing
    /// is the shape that was on screen. The route is the fallback for the AutoCAD picking method, which
    /// draws its line in the drawing rather than on the map and so never produces one.
    /// </summary>
    private ExportBoundary? BuildBufferBoundary(int outWkid)
    {
        try
        {
            var target = SpatialReference.Create(outWkid);
            var source = ProposedMainBufferOutline;

            if (source == null || source.IsEmpty)
            {
                source = BuildRouteBuffer();
                if (source == null) { return null; }
            }

            if (GeometryEngine.Project(source, target) is not Polygon projected || projected.IsEmpty)
            {
                return null;
            }

            var boundary = new ExportBoundary
            {
                Kind = ExportBoundaryKind.Buffer,
                Wkid = outWkid,
                Label = "Padding buffer",
                XMin = projected.Extent.XMin,
                YMin = projected.Extent.YMin,
                XMax = projected.Extent.XMax,
                YMax = projected.Extent.YMax
            };

            // Every ring, not just the outer one: a buffer around a main that doubles back on itself can
            // enclose a hole, and dropping it would both draw the boundary as solid where it is not and
            // pull in the features standing in the hole.
            foreach (var ring in ReadAllRings(projected))
            {
                if (ring.Count >= 3) { boundary.Rings.Add(ring); }
            }

            return boundary.Rings.Count > 0 ? boundary : null;
        }
        catch (Exception ex)
        {
            AddExportNote("The padding buffer could not be prepared, so the extent bounding box was "
                            + "used instead: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Buffers the route picked in AutoCAD, for the method that draws its line in the drawing.
    ///
    /// The padding is applied in the extent's own units, which is what the extent itself already does
    /// when it pads its bounds, so this introduces no assumption that was not there before.
    ///
    /// Built as JSON and handed to <c>Geometry.FromJson</c>, which is how the rest of this codebase puts
    /// ArcGIS geometry together.
    /// </summary>
    private Geometry? BuildRouteBuffer()
    {
        if (_resolvedExtent == null || _resolvedExtent.RouteVertices.Count < 2) { return null; }
        if (_resolvedExtent.PaddingFeet <= 0) { return null; }

        var builder = new StringBuilder();
        builder.Append("{\"paths\":[[");
        for (var i = 0; i < _resolvedExtent.RouteVertices.Count; i++)
        {
            var vertex = _resolvedExtent.RouteVertices[i];
            if (i > 0) { builder.Append(','); }
            builder.Append('[').Append(vertex.X.ToString("R", CultureInfo.InvariantCulture))
                   .Append(',').Append(vertex.Y.ToString("R", CultureInfo.InvariantCulture)).Append(']');
        }
        builder.Append("]],\"spatialReference\":{\"wkid\":")
               .Append(_resolvedExtent.Wkid.ToString(CultureInfo.InvariantCulture))
               .Append("}}");

        if (Geometry.FromJson(builder.ToString()) is not Polyline route || route.IsEmpty) { return null; }
        return GeometryEngine.Buffer(route, _resolvedExtent.PaddingFeet);
    }

    /// <summary>The extent as a rectangle, in drawing coordinates.</summary>
    private ExportBoundary? BuildExtentBoxBoundary(int outWkid)
    {
        if (_resolvedExtent == null) { return null; }

        try
        {
            var corners = ProjectExtentCorners(_resolvedExtent, outWkid);
            return new ExportBoundary
            {
                Kind = ExportBoundaryKind.ExtentBox,
                Wkid = outWkid,
                Label = "Export extent",
                XMin = corners.MinX,
                YMin = corners.MinY,
                XMax = corners.MaxX,
                YMax = corners.MaxY
            };
        }
        catch (Exception ex)
        {
            AddExportNote("The export extent could not be projected into drawing coordinates, so no "
                            + "boundary was drawn: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Draws the boundary that scoped the import, as a closed polyline on a layer of its own.
    ///
    /// It says what was asked for rather than what was found, so it belongs apart from the data: a
    /// drawing that is being issued usually wants the boundary off, and one being checked wants it on.
    /// </summary>
    private void AddBoundaryOutlineToRequest(CadExportRequest request, ExportBoundary? boundary)
    {
        if (!IncludeBoundaryInExport || boundary == null) { return; }

        if (boundary.HasRings)
        {
            foreach (var ring in boundary.Rings)
            {
                var outline = new ExportOutline { Label = boundary.Label };
                outline.Vertices.AddRange(ring);
                request.BoundaryOutlines.Add(outline);
            }
            return;
        }

        // A corridor job never draws a rectangle. The buffer is the boundary that means anything there,
        // and the extent around it is an artefact of how the area was worked out rather than something
        // the user asked for, so it does not belong in the drawing.
        //
        // This reaches here only when the buffer could not be built and the box was used to fetch. The
        // scope is then wider than the drawn boundary would suggest, which is exactly why no boundary is
        // drawn rather than a misleading one: the status line reports the fallback instead.
        if (BoundaryKind == ExportBoundaryKind.Buffer) { return; }

        var rectangle = new ExportOutline { Label = boundary.Label };
        rectangle.Vertices.Add(new ExportVertex(boundary.XMin, boundary.YMin));
        rectangle.Vertices.Add(new ExportVertex(boundary.XMax, boundary.YMin));
        rectangle.Vertices.Add(new ExportVertex(boundary.XMax, boundary.YMax));
        rectangle.Vertices.Add(new ExportVertex(boundary.XMin, boundary.YMax));
        request.BoundaryOutlines.Add(rectangle);
    }

    /// <summary>
    /// Downloads the chosen basemap over the exported area and adds it to the request.
    ///
    /// The area is requested in the output spatial reference for both the bounding box and the image, so
    /// the pixels are already in drawing coordinates. That is what lets the raster be placed on the
    /// extent it was asked for with no transform, and it is why the basemap lands accurately while the
    /// strip map frames go through a projection.
    ///
    /// It covers the boundary's bounds rather than the extent's. A raster can only ever be a rectangle,
    /// but on a corridor job the buffer's bounds are far tighter than the extent's, so the backdrop is
    /// spent on the ground the export actually covers rather than on the corners around it.
    /// </summary>
    private async Task AddBasemapToRequestAsync(CadExportRequest request, int outWkid, ExportBoundary? boundary)
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

            // The boundary is already in drawing coordinates, so the image and its placement come from
            // one set of numbers. Falling back to the extent keeps this working if no boundary was built.
            var corners = boundary != null
                ? (MinX: boundary.XMin, MinY: boundary.YMin, MaxX: boundary.XMax, MaxY: boundary.YMax)
                : ProjectExtentCorners(_resolvedExtent, outWkid);

            var imageExtent = new ExportExtent
            {
                Mode = _resolvedExtent.Mode,
                XMin = corners.MinX,
                YMin = corners.MinY,
                XMax = corners.MaxX,
                YMax = corners.MaxY,
                Wkid = boundary?.Wkid ?? outWkid
            };

            var image = await _services.BasemapImageService.DownloadAsync(
                serviceUrl, imageExtent, outWkid, BasemapImagePixels, directory, CancellationToken.None);

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

        // The viewport method hides the strip map pane, because an index is a run of sheets along a
        // proposed main and that method has no main to run along. Hiding the pane is not enough on its
        // own: an index built under another method would otherwise still be written, out of sight of the
        // page that would have explained it.
        if (SelectedMethod == ExportMethod.CurrentDrawingView)
        {
            AddExportNote("A strip map index was built under another method and was not written, "
                            + "because the visible map viewport export has no proposed main to lay "
                            + "sheets along.");
            return;
        }

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
        // A sheet frame is one rectangle, so the outer ring is the whole of it.
        var rings = ReadAllRings(polygon);
        return rings.Count == 0 ? new List<ExportVertex>() : rings[0];
    }

    private static List<List<ExportVertex>> ReadAllRings(Polygon polygon)
    {
        var result = new List<List<ExportVertex>>();

        using var document = JsonDocument.Parse(polygon.ToJson());
        if (!document.RootElement.TryGetProperty("rings", out var rings) || rings.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var ring in rings.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array) { continue; }

            var vertices = new List<ExportVertex>();
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

            // The JSON repeats the first point to close the ring, which the writer does with Closed.
            if (vertices.Count > 3
                && Math.Abs(vertices[0].X - vertices[^1].X) < 1e-9
                && Math.Abs(vertices[0].Y - vertices[^1].Y) < 1e-9)
            {
                vertices.RemoveAt(vertices.Count - 1);
            }

            if (vertices.Count >= 3) { result.Add(vertices); }
        }

        return result;
    }

    private static string DescribeExportResult(
        CadExportResult result, int totalFeatures, int outWkid, ExportBoundary? boundary)
    {
        var message = $"Exported {totalFeatures} feature(s) as {result.EntitiesWritten} entit(ies), "
            + $"in {SpatialReferenceNames.Describe(outWkid)}.";

        if (result.StripMapSheetsWritten > 0)
        {
            message += $" Strip map index: {result.StripMapSheetsWritten} sheet(s).";
        }
        if (result.BoundaryOutlinesWritten > 0)
        {
            // Named rather than counted, because which shape scoped the import is the useful fact.
            var name = boundary?.Kind == ExportBoundaryKind.Buffer ? "padding buffer" : "extent bounding box";
            message += $" Scoped to the {name}, drawn as {result.BoundaryOutlinesWritten} outline(s).";
        }
        if (result.HatchesWritten > 0)
        {
            message += $" Polygon fills: {result.HatchesWritten}.";
        }
        if (result.EntitiesWithAttributes > 0)
        {
            message += $" Attributes attached to {result.EntitiesWithAttributes} entit(ies).";
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
