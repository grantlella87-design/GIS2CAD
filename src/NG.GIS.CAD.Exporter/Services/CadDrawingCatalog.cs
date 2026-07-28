using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
namespace NG.GIS.CAD.Exporter.Services;
public sealed class CadDrawingCatalog
{
    public IReadOnlyList<string> GetBlockNames()
    {
        var names = new List<string>();
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return names;
        }
        var db = document.Database;
        using var transaction = db.TransactionManager.StartTransaction();
        var table = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!record.IsAnonymous && !record.IsLayout)
            {
                names.Add(record.Name);
            }
        }
        transaction.Commit();
        return names.OrderBy(x => x).ToList();
    }
    public IReadOnlyList<string> GetLineTypes()
    {
        var names = new List<string>();
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return names;
        }
        var db = document.Database;
        using var transaction = db.TransactionManager.StartTransaction();
        var table = (LinetypeTable)transaction.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var record = (LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            names.Add(record.Name);
        }
        transaction.Commit();
        return names.OrderBy(x => x).ToList();
    }
    public IReadOnlyList<string> GetLayerNames()
    {
        var names = new List<string>();
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return names;
        }
        var db = document.Database;
        using var transaction = db.TransactionManager.StartTransaction();
        var table = (LayerTable)transaction.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var record = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            names.Add(record.Name);
        }
        transaction.Commit();
        return names.OrderBy(x => x).ToList();
    }
}
