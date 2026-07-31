using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using NG.GIS.CAD.Exporter.Models;
namespace NG.GIS.CAD.Exporter.Services;
public sealed class CadExtentService
{
    public ExportExtent GetCurrentViewExtent(int wkid, double paddingFeet)
    {
        var document = Application.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No active AutoCAD document is available.");
        var view = document.Editor.GetCurrentView();
        var halfWidth = view.Width / 2.0;
        var halfHeight = view.Height / 2.0;
        var centerX = view.CenterPoint.X;
        var centerY = view.CenterPoint.Y;
        return new ExportExtent { Mode = "CurrentDrawingView", XMin = centerX - halfWidth - paddingFeet, YMin = centerY - halfHeight - paddingFeet, XMax = centerX + halfWidth + paddingFeet, YMax = centerY + halfHeight + paddingFeet, Wkid = wkid, PaddingFeet = paddingFeet };
    }
    public ExportExtent PromptForPipelineRouteExtent(int wkid, double paddingFeet)
    {
        var document = Application.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("No active AutoCAD document is available.");
        var editor = document.Editor;
        var database = document.Database;
        var points = new List<Point3d>();
        var first = editor.GetPoint(new PromptPointOptions("\nPick first pipeline route point: "));
        if (first.Status != PromptStatus.OK) { throw new InvalidOperationException("Pipeline route point selection was cancelled."); }
        points.Add(first.Value);
        while (true)
        {
            var nextOptions = new PromptPointOptions("\nPick next route point or press Enter to finish: ") { AllowNone = true, UseBasePoint = true, BasePoint = points.Last() };
            var next = editor.GetPoint(nextOptions);
            if (next.Status == PromptStatus.None) { break; }
            if (next.Status != PromptStatus.OK) { throw new InvalidOperationException("Pipeline route point selection was cancelled."); }
            points.Add(next.Value);
        }
        if (points.Count < 2) { throw new InvalidOperationException("At least two route points are required."); }
        using (document.LockDocument())
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            const string routeLayerName = "NGGIS_EXPORT_ROUTE";
            if (!layerTable.Has(routeLayerName))
            {
                layerTable.UpgradeOpen();
                var routeLayer = new LayerTableRecord { Name = routeLayerName };
                layerTable.Add(routeLayer);
                transaction.AddNewlyCreatedDBObject(routeLayer, true);
            }
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var polyline = new Polyline();
            for (var i = 0; i < points.Count; i++) { polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0); }
            polyline.Layer = routeLayerName;
            modelSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            transaction.Commit();
        }
        var extent = new ExportExtent { Mode = "DrawPipelineRoute", XMin = points.Min(p => p.X) - paddingFeet, YMin = points.Min(p => p.Y) - paddingFeet, XMax = points.Max(p => p.X) + paddingFeet, YMax = points.Max(p => p.Y) + paddingFeet, Wkid = wkid, PaddingFeet = paddingFeet };

        // The route itself, not just its bounds. The export is scoped to the corridor around this line,
        // and the padded box kept above is only the fallback for when there is no padding to buffer by.
        foreach (var point in points) { extent.RouteVertices.Add(new RouteVertex(point.X, point.Y)); }
        return extent;
    }
}
