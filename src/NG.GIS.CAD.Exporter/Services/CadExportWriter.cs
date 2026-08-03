using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using NG.GIS.CAD.Exporter.Models;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using AcadColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>
/// Writes the exported features into the drawing that is open.
///
/// Coordinates arrive in the output spatial reference, so they are drawing coordinates already and
/// nothing here transforms them. Getting the projection right is the REST query's job, which asks the
/// service for the output spatial reference directly.
///
/// Everything happens in one transaction. A half written export is worse than none: it leaves the
/// drawing with some layers populated and no way to tell which, so a failure aborts the lot.
/// </summary>
public sealed class CadExportWriter
{
    private const double DegreesToRadians = Math.PI / 180.0;

    public CadExportResult Write(CadExportRequest request)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            throw new InvalidOperationException("No drawing is open, so there is nothing to export into.");
        }

        var result = new CadExportResult();

        // Modeless window, so the lock is ours to take. Without it the native side throws.
        using var documentLock = document.LockDocument();
        var database = document.Database;

        // Opened once and reused. A template holds the standard blocks and line types, and pulling one
        // in per feature would reopen it hundreds of times.
        using var templateDatabase = OpenTemplate(request.TemplatePath, result);

        // Everything the rules name is brought across before the write transaction opens, and this
        // ordering is the whole of it. Importing a block opens the drawing's block table for write,
        // and the transaction below holds that same table open for read from its first line to its
        // last. Asking for it both ways at once is refused, so every import inside the transaction
        // failed -- silently, because the failure was caught and turned into "not in the drawing or
        // the template". A block plainly sitting in the template reported as missing.
        ImportTemplateSymbols(request, database, templateDatabase, result);

        using var transaction = database.TransactionManager.StartTransaction();

        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        foreach (var layer in request.Layers)
        {
            WriteLayer(request, layer, database, transaction, modelSpace, result);
        }

        if (request.StripMapSheets.Count > 0)
        {
            WriteStripMapIndex(request, database, transaction, modelSpace, result);
        }

        if (request.BoundaryOutlines.Count > 0)
        {
            WriteBoundaryOutlines(request, database, transaction, modelSpace, result);
        }

        // Last, so it can be sent behind everything already written in this pass.
        if (request.Basemap != null)
        {
            PlaceBasemap(request.Basemap, database, transaction, modelSpace, result);
        }

        transaction.Commit();
        return result;
    }

    /// <summary>
    /// Places the basemap image, georeferenced to the extent it was requested for, on its own layer and
    /// at the bottom of the draw order.
    ///
    /// Bottom of the draw order matters: a raster placed last draws over everything, which would hide
    /// every feature the export just wrote and make the result look empty.
    /// </summary>
    private static void PlaceBasemap(
        BasemapImagePlacement basemap, Database database, Transaction transaction,
        BlockTableRecord modelSpace, CadExportResult result)
    {
        if (!File.Exists(basemap.ImagePath))
        {
            result.Warnings.Add("The basemap image was not found at " + basemap.ImagePath + ", so no basemap was placed.");
            return;
        }

        if (basemap.Width <= 0 || basemap.Height <= 0)
        {
            result.Warnings.Add("The basemap extent has no area, so no basemap was placed.");
            return;
        }

        try
        {
            var layerName = SanitizeSymbolName(basemap.LayerName);
            EnsureLayer(database, transaction, layerName, null, result);

            // The image dictionary does not exist in a drawing that has never held a raster.
            var dictionaryId = RasterImageDef.GetImageDictionary(database);
            if (dictionaryId.IsNull) { dictionaryId = RasterImageDef.CreateImageDictionary(database); }

            var dictionary = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForWrite);
            var definitionName = RasterImageDef.SuggestName(dictionary, basemap.DefinitionName);

            var definition = new RasterImageDef { SourceFileName = basemap.ImagePath };
            definition.Load();

            var definitionId = dictionary.SetAt(definitionName, definition);
            transaction.AddNewlyCreatedDBObject(definition, true);

            var image = new RasterImage
            {
                ImageDefId = definitionId,

                // Origin plus the two edge vectors, which is how AutoCAD georeferences a raster: the
                // vectors are the full width and height of the image in drawing units, so the picture
                // lands on exactly the ground it was requested for.
                Orientation = new CoordinateSystem3d(
                    new Point3d(basemap.OriginX, basemap.OriginY, 0),
                    new Vector3d(basemap.Width, 0, 0),
                    new Vector3d(0, basemap.Height, 0)),
                ShowImage = true,
                Layer = layerName
            };
            image.AssociateRasterDef(definition);

            modelSpace.AppendEntity(image);
            transaction.AddNewlyCreatedDBObject(image, true);

            SendToBackOfDrawOrder(modelSpace, transaction, image.ObjectId, result);

            result.EntitiesWritten++;
            result.BasemapPlaced = true;
        }
        catch (Exception ex)
        {
            // A basemap is a backdrop. Losing it should not cost the features, which are the export.
            result.Warnings.Add("The basemap image could not be placed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void SendToBackOfDrawOrder(
        BlockTableRecord modelSpace, Transaction transaction, ObjectId imageId, CadExportResult result)
    {
        try
        {
            var drawOrder = (DrawOrderTable)transaction.GetObject(modelSpace.DrawOrderTableId, OpenMode.ForWrite);
            drawOrder.MoveToBottom(new ObjectIdCollection { imageId });
        }
        catch (Exception ex)
        {
            // The image is placed either way; it just sits on top until someone reorders it.
            result.Warnings.Add("The basemap was placed but could not be sent behind the features, so it may cover them: " + ex.Message);
        }
    }

    private static Database? OpenTemplate(string? templatePath, CadExportResult result)
    {
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath)) { return null; }

        try
        {
            var templateDatabase = new Database(false, true);
            templateDatabase.ReadDwgFile(templatePath, FileOpenMode.OpenForReadAndAllShare, allowCPConversion: true, password: "");
            return templateDatabase;
        }
        catch (Exception ex)
        {
            // The export is still worth running against whatever the drawing already has.
            result.Warnings.Add("The template could not be opened, so blocks and line types missing from the drawing were not imported: " + ex.Message);
            return null;
        }
    }

    private void WriteLayer(
        CadExportRequest request, ExportLayerFeatures layer, Database database, Transaction transaction,
        BlockTableRecord modelSpace, CadExportResult result)
    {
        var cadLayerName = string.IsNullOrWhiteSpace(layer.Transform.CadLayerName)
            ? SanitizeSymbolName(layer.LayerName)
            : SanitizeSymbolName(layer.Transform.CadLayerName);

        EnsureLayer(database, transaction, cadLayerName, BuildColor(layer.Transform), result);

        var lineType = ResolveLineType(database, transaction, layer.Transform.LineType);
        var isPoint = layer.GeometryType.Contains("Point", StringComparison.OrdinalIgnoreCase);
        ObjectId blockId = ObjectId.Null;

        if (isPoint && !string.IsNullOrWhiteSpace(layer.Transform.BlockName))
        {
            blockId = ResolveBlock(database, transaction, layer.Transform.BlockName);
        }

        var isPolygon = layer.GeometryType.Contains("Polygon", StringComparison.OrdinalIgnoreCase);
        var hatchPolygons = isPolygon && layer.Transform.HatchPolygons;

        foreach (var feature in layer.Features)
        {
            foreach (var part in feature.Parts)
            {
                if (part.Count == 0) { continue; }

                var entity = isPoint
                    ? BuildPointEntity(part[0], feature, layer.Transform, blockId)
                    : BuildLinearEntity(part, layer.GeometryType);

                if (entity == null) { continue; }

                entity.Layer = cadLayerName;
                ApplyEntityColor(entity, layer.Transform);
                if (lineType != null) { entity.Linetype = lineType; }

                modelSpace.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
                result.EntitiesWritten++;

                // After the entity is in the drawing, because extended data is stored against a
                // database object and there is nothing to store it against until then.
                if (request.AttachAttributes)
                {
                    AttachFeatureAttributes(entity, feature, request.AttributeAppName, database, transaction, result);
                }

                // A hatch needs a boundary that is already in the drawing, so this follows the outline
                // rather than replacing it. A ring with fewer than three vertices encloses nothing and
                // would give the hatch no area to fill.
                if (hatchPolygons && part.Count >= 3)
                {
                    WritePolygonHatch(entity, cadLayerName, layer.Transform, transaction, modelSpace, result);
                }
            }
        }
    }

    private static Entity? BuildPointEntity(ExportVertex vertex, ExportFeature feature, CadTransformRule transform, ObjectId blockId)
    {
        var position = new Point3d(vertex.X, vertex.Y, 0);

        // Without a block there is still a feature to record, and a point is the honest way to record
        // it. Dropping it would silently lose data the user asked to export.
        if (blockId.IsNull) { return new DBPoint(position); }

        var reference = new BlockReference(position, blockId)
        {
            Rotation = ResolveRotationDegrees(feature, transform) * DegreesToRadians
        };
        var scale = transform.ScaleValue > 0 ? transform.ScaleValue : 1.0;
        reference.ScaleFactors = new Scale3d(scale);
        return reference;
    }

    /// <summary>
    /// The angle to place one block at, in degrees.
    ///
    /// The rule names a field and the feature carries its value, and until now neither was read: the
    /// rotation field has been on the model, and pickable in the profile, while every block went in at
    /// the fixed offset regardless. A layer that records which way its valves face was drawn with all
    /// of them facing the same way.
    ///
    /// Every path ends at the fixed offset, which is what makes reading the field safe to do by
    /// default. A rule with no field, a feature missing that attribute, and a value that is not a
    /// number all land on the angle that would have been used anyway, so nothing is worse off than
    /// before for having tried.
    /// </summary>
    private static double ResolveRotationDegrees(ExportFeature feature, CadTransformRule transform)
    {
        if (string.Equals(transform.RotationMode, RotationModes.Fixed, StringComparison.OrdinalIgnoreCase))
        {
            return transform.RotationOffsetDegrees;
        }

        if (string.IsNullOrWhiteSpace(transform.RotationField)) { return transform.RotationOffsetDegrees; }
        if (!feature.Attributes.TryGetValue(transform.RotationField, out var raw)) { return transform.RotationOffsetDegrees; }
        if (string.IsNullOrWhiteSpace(raw)) { return transform.RotationOffsetDegrees; }

        // Invariant, because these values come off a REST service as JSON numbers and were turned into
        // strings on the way in. Parsing them back under a comma decimal separator would read 45.5 as
        // nothing at all and drop the feature to the default angle without saying so.
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var degrees))
        {
            return transform.RotationOffsetDegrees;
        }

        if (transform.InvertRotationField) { degrees = -degrees; }

        // The offset applies on top rather than instead, so a layer whose angles are right relative to
        // each other but a quarter turn out as a set can be corrected once for the whole layer.
        return degrees + transform.RotationOffsetDegrees;
    }

    private static Entity? BuildLinearEntity(List<ExportVertex> part, string geometryType)
    {
        if (part.Count < 2) { return null; }

        var polyline = new Polyline(part.Count);
        for (var i = 0; i < part.Count; i++)
        {
            polyline.AddVertexAt(i, new Point2d(part[i].X, part[i].Y), 0, 0, 0);
        }

        // A ring is a boundary, so it closes. Closing it rather than repeating the first vertex keeps
        // the polyline one segment shorter and makes it read as an area in CAD.
        if (geometryType.Contains("Polygon", StringComparison.OrdinalIgnoreCase))
        {
            polyline.Closed = true;
        }

        return polyline;
    }

    /// <summary>
    /// Fills a polygon outline with a hatch.
    ///
    /// The boundary has to be in the drawing before a hatch can point at it, and the hatch itself has to
    /// be in the drawing before its loop can be appended, which is why the order here looks fussier than
    /// it reads: append, pattern, loop, evaluate.
    ///
    /// Associative, so the fill follows the boundary if the outline is later stretched or its vertices
    /// moved. A fill that stayed behind when its boundary moved would be worse than no fill, because it
    /// would still look like an answer.
    ///
    /// A hatch that will not evaluate is reported and skipped. The outline is already written and is the
    /// part that carries the geometry, so a failure here costs presentation rather than data.
    /// </summary>
    private static void WritePolygonHatch(
        Entity boundary, string cadLayerName, CadTransformRule rule,
        Transaction transaction, BlockTableRecord modelSpace, CadExportResult result)
    {
        try
        {
            var hatch = new Hatch();
            hatch.Layer = cadLayerName;

            modelSpace.AppendEntity(hatch);
            transaction.AddNewlyCreatedDBObject(hatch, true);

            var pattern = string.IsNullOrWhiteSpace(rule.HatchPattern) ? "SOLID" : rule.HatchPattern.Trim();
            hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);

            // SOLID has nothing to space, and giving it a scale is the kind of setting that is quietly
            // ignored until someone changes the pattern and cannot see why the spacing is wrong.
            if (!pattern.Equals("SOLID", StringComparison.OrdinalIgnoreCase) && rule.HatchScale > 0)
            {
                hatch.PatternScale = rule.HatchScale;
                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
            }

            var inset = rule.HatchInsetDistance > 0 && boundary is Curve curve
                ? BuildInsetLoop(curve, rule.HatchInsetDistance)
                : null;

            if (inset != null)
            {
                // The inset ring is handed over as bare points rather than drawn. It is a boundary for
                // the fill, not something the user asked to have in their drawing, and adding it would
                // leave a second outline inside every polygon.
                //
                // Which also means the hatch cannot be associative: there is no entity to associate to.
                // A fill inset from a boundary that then moves has to be re-exported either way.
                hatch.Associative = false;
                hatch.AppendLoop(HatchLoopTypes.Polyline, inset, new DoubleCollection(new double[inset.Count]));
            }
            else
            {
                if (rule.HatchInsetDistance > 0)
                {
                    var note = "A polygon on layer " + cadLayerName + " was filled to its boundary rather "
                               + "than inset, because an inset of " + rule.HatchInsetDistance
                               + " leaves it nothing to fill.";
                    if (!result.Warnings.Contains(note)) { result.Warnings.Add(note); }
                }

                hatch.Associative = true;
                hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { boundary.ObjectId });
            }

            hatch.EvaluateHatch(true);

            // After evaluating, so the fill is drawn with the transparency rather than evaluated and then
            // changed, and only when asked for: zero here means opaque, which is AutoCAD's own reading.
            var transparency = Math.Clamp(rule.HatchTransparencyPercent, 0, 90);
            if (transparency > 0)
            {
                // AutoCAD stores transparency as an alpha, 255 being opaque, so a percentage has to be
                // turned around before it means the same thing.
                var alpha = (byte)Math.Round(255.0 * (100 - transparency) / 100.0);
                hatch.Transparency = new Autodesk.AutoCAD.Colors.Transparency(alpha);
            }

            ApplyEntityColor(hatch, rule);
            result.EntitiesWritten++;
            result.HatchesWritten++;
        }
        catch (Exception ex)
        {
            var note = "A polygon on layer " + cadLayerName + " was outlined but could not be hatched: "
                       + ex.Message;
            if (!result.Warnings.Contains(note)) { result.Warnings.Add(note); }
        }
    }

    /// <summary>
    /// Attaches a feature's GIS attributes to the entity drawn for it, as extended data.
    ///
    /// This is what makes an exported line answer questions. A polyline on its own says where something
    /// runs; carrying its diameter, material and work order it says what it is, which is most of why the
    /// fields on page 3 are worth choosing. AutoCAD shows it under Properties and any tool reading the
    /// drawing can pick it up, so nothing has to come back to the GIS to identify a line.
    ///
    /// Written as name and value pairs so the drawing is readable without a schema. That doubles the
    /// strings stored, which is worth it against the alternative of a positional list that means nothing
    /// once the field selection changes.
    ///
    /// Extended data caps a string at 255 characters and an application's data at about 16kB per entity.
    /// Values are cut to fit rather than dropped, because a shortened value still identifies a feature
    /// and a refused write would lose the lot.
    /// </summary>
    private static void AttachFeatureAttributes(
        Entity entity, ExportFeature feature, string appName,
        Database database, Transaction transaction, CadExportResult result)
    {
        if (feature.Attributes.Count == 0) { return; }

        var application = string.IsNullOrWhiteSpace(appName) ? "NGGIS" : SanitizeSymbolName(appName);

        try
        {
            EnsureRegisteredApplication(database, transaction, application);

            var buffer = new ResultBuffer();
            buffer.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, application));

            var used = 0;
            foreach (var pair in feature.Attributes)
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) { continue; }

                var name = Truncate(pair.Key, 255);
                var value = Truncate(pair.Value, 255);

                // Two strings and their overhead against the per application budget. Stopping short
                // keeps the entity writable rather than having AutoCAD refuse the whole buffer.
                used += name.Length + value.Length + 8;
                if (used > 15000) { break; }

                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, name));
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, value));
            }

            entity.XData = buffer;
            result.EntitiesWithAttributes++;
        }
        catch (Exception ex)
        {
            var note = "Attributes could not be attached to one or more entities: " + ex.Message;
            if (!result.Warnings.Contains(note)) { result.Warnings.Add(note); }
        }
    }

    /// <summary>
    /// Makes sure the application name exists in the drawing, since extended data filed under a name the
    /// drawing does not know is refused.
    /// </summary>
    private static void EnsureRegisteredApplication(Database database, Transaction transaction, string application)
    {
        var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (table.Has(application)) { return; }

        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = application };
        table.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];

    /// <summary>
    /// The polygon offset inwards, as bare points for a hatch loop, or null when there is nothing left.
    ///
    /// AutoCAD offsets a closed curve to one side or the other depending on which way its vertices run,
    /// and GIS rings come in both windings, so a sign cannot be assumed. Both are offered and the smaller
    /// result is the inward one, which is true whichever way the ring was wound.
    ///
    /// An offset can also come back as several curves, when a shape narrow enough to close up on itself
    /// breaks into pieces. The largest of those is taken: it is the body of the polygon, and the slivers
    /// are the corners that folded away.
    ///
    /// Null when the offset collapses entirely, which is an inset wider than the polygon is. The caller
    /// fills to the boundary instead and says why, rather than drawing a shape the inset did not ask for.
    /// </summary>
    private static Point2dCollection? BuildInsetLoop(Curve boundary, double distance)
    {
        var originalArea = SafeArea(boundary);
        if (originalArea <= 0) { return null; }

        Polyline? best = null;
        var bestArea = 0.0;

        foreach (var signed in new[] { -distance, distance })
        {
            DBObjectCollection? offsets = null;
            try { offsets = boundary.GetOffsetCurves(signed); }
            catch { continue; }

            using (offsets)
            {
                foreach (DBObject candidate in offsets)
                {
                    if (candidate is not Polyline polyline) { candidate.Dispose(); continue; }

                    var area = SafeArea(polyline);

                    // Smaller than what it came from is what makes it the inward one; larger is the
                    // outward offset, which would put the fill outside the polygon it belongs to.
                    if (area > 0 && area < originalArea && area > bestArea)
                    {
                        best?.Dispose();
                        best = polyline;
                        bestArea = area;
                        continue;
                    }

                    polyline.Dispose();
                }
            }
        }

        if (best == null) { return null; }

        using (best)
        {
            if (best.NumberOfVertices < 3) { return null; }

            var points = new Point2dCollection();
            for (var i = 0; i < best.NumberOfVertices; i++)
            {
                points.Add(best.GetPoint2dAt(i));
            }

            // Closed explicitly, because a hatch loop given as points is a list rather than a shape and
            // has no Closed of its own to read.
            if (points[0] != points[^1]) { points.Add(points[0]); }
            return points;
        }
    }

    /// <summary>
    /// A curve's enclosed area, or zero when it will not give one. An open or self crossing curve throws
    /// rather than answering, and for choosing between offsets that is the same as having no area.
    /// </summary>
    private static double SafeArea(Curve curve)
    {
        try { return Math.Abs(curve.Area); }
        catch { return 0.0; }
    }

    /// <summary>
    /// Draws the strip map sheets as closed polylines with their numbers, on a layer of their own.
    ///
    /// Its own layer because the index is not a GIS feature. It describes the sheet set rather than
    /// anything in the ground, so it has to be switchable and plottable separately from the data.
    /// </summary>
    private void WriteStripMapIndex(
        CadExportRequest request, Database database, Transaction transaction,
        BlockTableRecord modelSpace, CadExportResult result)
    {
        var layerName = SanitizeSymbolName(request.StripMapLayerName);
        EnsureLayer(database, transaction, layerName, AcadColor.FromColorIndex(AcadColorMethod.ByAci, 4), result);

        foreach (var sheet in request.StripMapSheets)
        {
            if (sheet.Corners.Count >= 3)
            {
                var frame = new Polyline(sheet.Corners.Count);
                for (var i = 0; i < sheet.Corners.Count; i++)
                {
                    frame.AddVertexAt(i, new Point2d(sheet.Corners[i].X, sheet.Corners[i].Y), 0, 0, 0);
                }
                frame.Closed = true;
                frame.Layer = layerName;

                modelSpace.AppendEntity(frame);
                transaction.AddNewlyCreatedDBObject(frame, true);
                result.EntitiesWritten++;
            }

            var label = new DBText
            {
                TextString = sheet.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Position = new Point3d(sheet.LabelX, sheet.LabelY, 0),
                Height = request.StripMapLabelHeight > 0 ? request.StripMapLabelHeight : 10.0,
                Rotation = sheet.RotationDegrees * DegreesToRadians,
                Layer = layerName
            };

            modelSpace.AppendEntity(label);
            transaction.AddNewlyCreatedDBObject(label, true);
            result.EntitiesWritten++;
            result.StripMapSheetsWritten++;
        }
    }

    /// <summary>
    /// Draws the boundary that scoped the import as a closed polyline, on a layer of its own.
    ///
    /// It says what was asked for rather than what was found, so it belongs apart from the data: a
    /// drawing that is being issued usually wants the boundary off, and one being checked wants it on.
    /// </summary>
    private void WriteBoundaryOutlines(
        CadExportRequest request, Database database, Transaction transaction,
        BlockTableRecord modelSpace, CadExportResult result)
    {
        var layerName = SanitizeSymbolName(request.BoundaryLayerName);
        EnsureLayer(database, transaction, layerName, AcadColor.FromColorIndex(AcadColorMethod.ByAci, 8), result);

        foreach (var outline in request.BoundaryOutlines)
        {
            if (outline.Vertices.Count < 3) { continue; }

            var polyline = new Polyline(outline.Vertices.Count);
            for (var i = 0; i < outline.Vertices.Count; i++)
            {
                polyline.AddVertexAt(i, new Point2d(outline.Vertices[i].X, outline.Vertices[i].Y), 0, 0, 0);
            }
            polyline.Closed = true;
            polyline.Layer = layerName;

            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            result.EntitiesWritten++;
            result.BoundaryOutlinesWritten++;
        }
    }

    private static void EnsureLayer(
        Database database, Transaction transaction, string layerName, AcadColor? color, CadExportResult result)
    {
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(layerName)) { return; }

        layerTable.UpgradeOpen();
        var record = new LayerTableRecord { Name = layerName };
        if (color != null) { record.Color = color; }

        layerTable.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
        result.CadLayersCreated.Add(layerName);
    }

    /// <summary>
    /// The colour to give a newly created CAD layer. ByLayer on the rule means the layer itself
    /// decides, so nothing is forced and AutoCAD's default stands.
    /// </summary>
    private static AcadColor? BuildColor(CadTransformRule transform)
    {
        if (string.Equals(transform.ColorMode, "ACI", StringComparison.OrdinalIgnoreCase))
        {
            return AcadColor.FromColorIndex(AcadColorMethod.ByAci, (short)Math.Clamp(transform.AciColor, 0, 256));
        }

        if (string.Equals(transform.ColorMode, "RGB", StringComparison.OrdinalIgnoreCase)
            && TryParseRgb(transform.RgbColor, out var red, out var green, out var blue))
        {
            return AcadColor.FromRgb(red, green, blue);
        }

        return null;
    }

    /// <summary>
    /// Entities are left ByLayer unless the rule names a colour, so the layer stays the one place the
    /// colour is set and a later change to the layer still takes effect.
    /// </summary>
    private static void ApplyEntityColor(Entity entity, CadTransformRule transform)
    {
        var color = BuildColor(transform);
        if (color != null) { entity.Color = color; }
    }

    private static bool TryParseRgb(string? value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (string.IsNullOrWhiteSpace(value)) { return false; }

        var digits = value.Trim();
        if (digits.StartsWith("#", StringComparison.Ordinal)) { digits = digits[1..]; }
        if (digits.Length != 6) { return false; }

        try
        {
            red = Convert.ToByte(digits[..2], 16);
            green = Convert.ToByte(digits[2..4], 16);
            blue = Convert.ToByte(digits[4..6], 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The line type to set, or null to leave the entity ByLayer. A line type named on a rule but not
    /// in the drawing is imported from the template when there is one.
    /// </summary>
    private static string? ResolveLineType(
        Database database, Transaction transaction, string? lineType)
    {
        if (string.IsNullOrWhiteSpace(lineType)
            || string.Equals(lineType, "ByLayer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A lookup and nothing more. Anything that had to be brought in from the template was brought
        // in before this transaction opened, and told the user if it could not be.
        var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        return table.Has(lineType) ? lineType : null;
    }

    /// <summary>
    /// The block to insert, or a null id to fall back to plain points. Imported from the template when
    /// the drawing does not have it, which is the point of choosing a template on page 1.
    /// </summary>
    private static ObjectId ResolveBlock(
        Database database, Transaction transaction, string blockName)
    {
        // A lookup and nothing more, for the same reason as the line types above. Importing from here
        // was the bug: this transaction is holding the block table open for read, and an import has to
        // open it for write.
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        return blockTable.Has(blockName) ? blockTable[blockName] : ObjectId.Null;
    }

    /// <summary>
    /// Brings every block and line type the rules name into the drawing, before anything is written.
    ///
    /// Runs outside the write transaction on purpose. Both routes into the drawing -- Insert for a
    /// block, WblockCloneObjects for a line type -- have to open a symbol table for write, and the
    /// write transaction holds the block table open for read for its whole life. The two cannot both
    /// hold, so an import attempted from inside it never had a chance.
    /// </summary>
    private static void ImportTemplateSymbols(
        CadExportRequest request, Database database, Database? templateDatabase, CadExportResult result)
    {
        var blocks = new List<string>();
        var lineTypes = new List<string>();

        foreach (var layer in request.Layers)
        {
            // Only point layers are drawn as block references, so a block named on a line rule is not
            // going to be used and is not worth importing or warning about.
            var block = layer.Transform.BlockName;
            if (layer.GeometryType.Contains("Point", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(block)
                && !blocks.Contains(block, StringComparer.OrdinalIgnoreCase))
            {
                blocks.Add(block);
            }

            var lineType = layer.Transform.LineType;
            if (!string.IsNullOrWhiteSpace(lineType)
                && !string.Equals(lineType, "ByLayer", StringComparison.OrdinalIgnoreCase)
                && !lineTypes.Contains(lineType, StringComparer.OrdinalIgnoreCase))
            {
                lineTypes.Add(lineType);
            }
        }

        foreach (var name in blocks) { EnsureSymbol(database, templateDatabase, name, isBlock: true, result); }
        foreach (var name in lineTypes) { EnsureSymbol(database, templateDatabase, name, isBlock: false, result); }
    }

    /// <summary>
    /// Makes sure one named symbol is in the drawing, importing it from the template if it is not, and
    /// says what went wrong if it still is not afterwards.
    /// </summary>
    private static void EnsureSymbol(
        Database database, Database? templateDatabase, string name, bool isBlock, CadExportResult result)
    {
        if (SymbolExists(database, name, isBlock)) { return; }

        var what = (isBlock ? "Block '" : "Line type '") + name + "'";
        var instead = isBlock
            ? ", so those features were drawn as points instead."
            : ", so those features were drawn ByLayer.";

        if (templateDatabase == null)
        {
            result.Warnings.Add(what + " is not in the drawing and no template was set" + instead);
            return;
        }

        var failure = TryImportSymbol(database, templateDatabase, name, isBlock);

        // Asked of the drawing rather than taken from the return. An import that reports success and
        // leaves nothing behind is the case that produced a confusing report last time.
        if (failure == null && SymbolExists(database, name, isBlock)) { return; }

        result.Warnings.Add(what + " could not be brought in from the template"
            + (failure == null ? " (the import reported success but the drawing still does not have it)" : ": " + failure)
            + instead);
    }

    /// <summary>
    /// Whether the drawing already holds a symbol by this name.
    ///
    /// On its own short lived transaction, so the table is closed again the moment the question is
    /// answered. Anything still holding it open is what stops the import that may follow.
    /// </summary>
    private static bool SymbolExists(Database database, string name, bool isBlock)
    {
        try
        {
            using var transaction = database.TransactionManager.StartOpenCloseTransaction();
            var tableId = isBlock ? database.BlockTableId : database.LinetypeTableId;
            var table = (SymbolTable)transaction.GetObject(tableId, OpenMode.ForRead);
            var has = table.Has(name);
            transaction.Commit();
            return has;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies one named block or line type across from the template.
    ///
    /// Both go through WblockCloneObjects. Blocks used to go through Database.Insert, which reads as the
    /// purpose built call for exactly this and answered eSelfReference instead -- that is the error for
    /// a database being asked to copy from itself, so the overload does not mean what it looks like it
    /// means here. Line types were already cloned this way and were already working, so the same route
    /// is now taken for both rather than keeping a second mechanism that only fails.
    ///
    /// Returns null when it worked, and the reason when it did not. The reason used to be thrown away,
    /// which is how a block that was present in the template and named correctly on the rule could be
    /// reported as simply not existing.
    /// </summary>
    private static string? TryImportSymbol(Database database, Database templateDatabase, string name, bool isBlock)
    {
        // Cloning between databases reads the working one to resolve what it is copying. This runs from
        // a modeless window, where nothing has made the document's database the working one, and the
        // same omission is what answered eNoDatabase when the geographic location was being set.
        var previousWorkingDatabase = HostApplicationServices.WorkingDatabase;

        try
        {
            HostApplicationServices.WorkingDatabase = database;

            using var templateTransaction = templateDatabase.TransactionManager.StartTransaction();

            var sourceTableId = isBlock ? templateDatabase.BlockTableId : templateDatabase.LinetypeTableId;
            var sourceTable = (SymbolTable)templateTransaction.GetObject(sourceTableId, OpenMode.ForRead);
            if (!sourceTable.Has(name))
            {
                templateTransaction.Commit();
                return "the template has no " + (isBlock ? "block" : "line type") + " by that name";
            }

            // Ignore rather than Replace: this only runs for a symbol the drawing was found not to
            // have, so anything already there under this name got there during this same call and is
            // the thing being brought in. Replacing would redefine whatever the drawing had.
            var ids = new ObjectIdCollection { sourceTable[name] };
            var destinationId = isBlock ? database.BlockTableId : database.LinetypeTableId;
            var mapping = new IdMapping();
            database.WblockCloneObjects(ids, destinationId, mapping, DuplicateRecordCloning.Ignore, false);

            templateTransaction.Commit();
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (previousWorkingDatabase != null)
            {
                try { HostApplicationServices.WorkingDatabase = previousWorkingDatabase; } catch { }
            }
        }
    }

    /// <summary>
    /// Makes a name AutoCAD will accept as a symbol table entry. GIS layer names carry characters that
    /// are not legal here, and a rejected name would fail the whole export over a punctuation mark.
    /// </summary>
    private static string SanitizeSymbolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return "GIS_LAYER"; }

        var invalid = new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };
        var cleaned = new System.Text.StringBuilder(name.Length);
        foreach (var character in name.Trim())
        {
            cleaned.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var value = cleaned.ToString().Trim();
        return string.IsNullOrEmpty(value) ? "GIS_LAYER" : value;
    }
}
