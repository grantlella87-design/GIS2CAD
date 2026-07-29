using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>
/// Reads block, line type and layer names for the CAD transformation page.
///
/// Names can come from the drawing that is open, or from a template file chosen on page 1. A
/// template is often the more useful source: it holds the standard blocks and line types the export
/// is meant to match, which a working drawing may not have yet.
/// </summary>
public sealed class CadDrawingCatalog
{
    public IReadOnlyList<string> GetBlockNames(string? templatePath = null) => Read(templatePath, ReadBlockNames);

    public IReadOnlyList<string> GetLineTypes(string? templatePath = null) => Read(templatePath, ReadLineTypes);

    public IReadOnlyList<string> GetLayerNames(string? templatePath = null) => Read(templatePath, ReadLayerNames);

    /// <summary>
    /// Runs a table read against the template when one is given and readable, and against the active
    /// drawing otherwise. The template is opened in its own side database, so nothing touches the
    /// drawing the user has open.
    /// </summary>
    private static IReadOnlyList<string> Read(string? templatePath, Func<Database, Transaction, List<string>> read)
    {
        if (!string.IsNullOrWhiteSpace(templatePath))
        {
            // Falling back to the open drawing here would look like the template simply had no
            // blocks, which is the least useful thing this could do.
            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException("The CAD template could not be found: " + templatePath, templatePath);
            }

            using var templateDatabase = new Database(false, true);
            templateDatabase.ReadDwgFile(templatePath, FileOpenMode.OpenForReadAndAllShare, allowCPConversion: true, password: null);

            using var templateTransaction = templateDatabase.TransactionManager.StartTransaction();
            var fromTemplate = read(templateDatabase, templateTransaction);
            templateTransaction.Commit();
            return Sorted(fromTemplate);
        }

        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null) { return Array.Empty<string>(); }

        using var transaction = document.Database.TransactionManager.StartTransaction();
        var names = read(document.Database, transaction);
        transaction.Commit();
        return Sorted(names);
    }

    private static List<string> ReadBlockNames(Database database, Transaction transaction)
    {
        var names = new List<string>();
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!record.IsAnonymous && !record.IsLayout) { names.Add(record.Name); }
        }
        return names;
    }

    private static List<string> ReadLineTypes(Database database, Transaction transaction)
    {
        var names = new List<string>();
        var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var record = (LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            names.Add(record.Name);
        }
        return names;
    }

    private static List<string> ReadLayerNames(Database database, Transaction transaction)
    {
        var names = new List<string>();
        var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var record = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            names.Add(record.Name);
        }
        return names;
    }

    private static IReadOnlyList<string> Sorted(List<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
}
