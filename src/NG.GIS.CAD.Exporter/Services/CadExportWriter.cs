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
        using var transaction = database.TransactionManager.StartTransaction();

        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        // Opened once and reused. A template holds the standard blocks and line types, and pulling one
        // in per feature would reopen it hundreds of times.
        using var templateDatabase = OpenTemplate(request.TemplatePath, result);

        foreach (var layer in request.Layers)
        {
            WriteLayer(request, layer, database, transaction, modelSpace, templateDatabase, result);
        }

        if (request.StripMapSheets.Count > 0)
        {
            WriteStripMapIndex(request, database, transaction, modelSpace, result);
        }

        if (request.ExtentOutlines.Count > 0)
        {
            WriteExtentOutlines(request, database, transaction, modelSpace, result);
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
        BlockTableRecord modelSpace, Database? templateDatabase, CadExportResult result)
    {
        var cadLayerName = string.IsNullOrWhiteSpace(layer.Transform.CadLayerName)
            ? SanitizeSymbolName(layer.LayerName)
            : SanitizeSymbolName(layer.Transform.CadLayerName);

        EnsureLayer(database, transaction, cadLayerName, BuildColor(layer.Transform), result);

        var lineType = ResolveLineType(database, transaction, layer.Transform.LineType, templateDatabase, result);
        var isPoint = layer.GeometryType.Contains("Point", StringComparison.OrdinalIgnoreCase);
        ObjectId blockId = ObjectId.Null;

        if (isPoint && !string.IsNullOrWhiteSpace(layer.Transform.BlockName))
        {
            blockId = ResolveBlock(database, transaction, layer.Transform.BlockName, templateDatabase, result);
        }

        foreach (var feature in layer.Features)
        {
            foreach (var part in feature.Parts)
            {
                if (part.Count == 0) { continue; }

                var entity = isPoint
                    ? BuildPointEntity(part[0], layer.Transform, blockId)
                    : BuildLinearEntity(part, layer.GeometryType);

                if (entity == null) { continue; }

                entity.Layer = cadLayerName;
                ApplyEntityColor(entity, layer.Transform);
                if (lineType != null) { entity.Linetype = lineType; }

                modelSpace.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
                result.EntitiesWritten++;
            }
        }
    }

    private static Entity? BuildPointEntity(ExportVertex vertex, CadTransformRule transform, ObjectId blockId)
    {
        var position = new Point3d(vertex.X, vertex.Y, 0);

        // Without a block there is still a feature to record, and a point is the honest way to record
        // it. Dropping it would silently lose data the user asked to export.
        if (blockId.IsNull) { return new DBPoint(position); }

        var reference = new BlockReference(position, blockId)
        {
            Rotation = transform.RotationOffsetDegrees * DegreesToRadians
        };
        var scale = transform.ScaleValue > 0 ? transform.ScaleValue : 1.0;
        reference.ScaleFactors = new Scale3d(scale);
        return reference;
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
    /// Draws the export extent and the padding buffer as closed polylines, on a layer of their own.
    ///
    /// These say what was asked for rather than what was found, so they belong apart from the data: a
    /// drawing that is being issued usually wants the boundary off, and one being checked wants it on.
    /// </summary>
    private void WriteExtentOutlines(
        CadExportRequest request, Database database, Transaction transaction,
        BlockTableRecord modelSpace, CadExportResult result)
    {
        var layerName = SanitizeSymbolName(request.ExtentLayerName);
        EnsureLayer(database, transaction, layerName, AcadColor.FromColorIndex(AcadColorMethod.ByAci, 8), result);

        foreach (var outline in request.ExtentOutlines)
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
            result.ExtentOutlinesWritten++;
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
        Database database, Transaction transaction, string? lineType, Database? templateDatabase, CadExportResult result)
    {
        if (string.IsNullOrWhiteSpace(lineType)
            || string.Equals(lineType, "ByLayer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(lineType)) { return lineType; }

        if (templateDatabase != null && TryImportSymbol(database, templateDatabase, lineType, isBlock: false))
        {
            return lineType;
        }

        result.Warnings.Add("Line type '" + lineType + "' is not in the drawing"
            + (templateDatabase == null ? " and no template was set" : " or the template")
            + ", so those features were drawn ByLayer.");
        return null;
    }

    /// <summary>
    /// The block to insert, or a null id to fall back to plain points. Imported from the template when
    /// the drawing does not have it, which is the point of choosing a template on page 1.
    /// </summary>
    private static ObjectId ResolveBlock(
        Database database, Transaction transaction, string blockName, Database? templateDatabase, CadExportResult result)
    {
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        if (blockTable.Has(blockName)) { return blockTable[blockName]; }

        if (templateDatabase != null && TryImportSymbol(database, templateDatabase, blockName, isBlock: true))
        {
            // Re-read: the import added to the table this transaction is holding open.
            var refreshed = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (refreshed.Has(blockName)) { return refreshed[blockName]; }
        }

        result.Warnings.Add("Block '" + blockName + "' is not in the drawing"
            + (templateDatabase == null ? " and no template was set" : " or the template")
            + ", so those features were drawn as points instead.");
        return ObjectId.Null;
    }

    /// <summary>
    /// Copies one named block or line type across from the template. WblockCloneObjects would be the
    /// general tool, but Insert already does exactly this for a block, and a line type comes across
    /// with it as a dependency.
    /// </summary>
    private static bool TryImportSymbol(Database database, Database templateDatabase, string name, bool isBlock)
    {
        try
        {
            if (isBlock)
            {
                database.Insert(name, name, templateDatabase, true);
                return true;
            }

            using var templateTransaction = templateDatabase.TransactionManager.StartTransaction();
            var templateTable = (LinetypeTable)templateTransaction.GetObject(templateDatabase.LinetypeTableId, OpenMode.ForRead);
            if (!templateTable.Has(name)) { templateTransaction.Commit(); return false; }

            var ids = new ObjectIdCollection { templateTable[name] };
            var mapping = new IdMapping();
            database.WblockCloneObjects(ids, database.LinetypeTableId, mapping, DuplicateRecordCloning.Ignore, false);
            templateTransaction.Commit();
            return true;
        }
        catch
        {
            return false;
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
