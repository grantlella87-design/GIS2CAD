using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

// System variables live on the Core application type, which is a different class from the
// ApplicationServices.Application used for the document manager above.
using CoreApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>The block, line type and layer names read from one drawing in one pass.</summary>
public sealed class CadSymbolCatalog
{
    public IReadOnlyList<string> Blocks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LineTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Reads block, line type and layer names for the CAD transformation page.
///
/// Names can come from the drawing that is open, or from a template file chosen on page 1. A
/// template is often the more useful source: it holds the standard blocks and line types the export
/// is meant to match, which a working drawing may not have yet.
/// </summary>
public sealed class CadDrawingCatalog
{
    /// <summary>
    /// Reads all three tables from one open of the drawing. Reading them separately meant opening a
    /// template three times over, which is three loads of the same file and three chances for the
    /// native reader to fault on it.
    /// </summary>
    public CadSymbolCatalog Read(string? templatePath = null) =>
        string.IsNullOrWhiteSpace(templatePath) ? ReadOpenDrawing() : ReadTemplate(templatePath);

    public IReadOnlyList<string> GetBlockNames(string? templatePath = null) => Read(templatePath).Blocks;

    public IReadOnlyList<string> GetLineTypes(string? templatePath = null) => Read(templatePath).LineTypes;

    public IReadOnlyList<string> GetLayerNames(string? templatePath = null) => Read(templatePath).Layers;

    private static CadSymbolCatalog ReadOpenDrawing()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null) { return new CadSymbolCatalog(); }

        // The drawing database can only be touched under a document lock. Inside a command AutoCAD
        // holds one already, but these reads are driven from a modeless window: page navigation, a
        // button, or the startup pass. Without the lock the native side throws, which surfaces as
        // "External component has thrown an exception".
        using var documentLock = document.LockDocument();
        return ReadTables(document.Database);
    }

    /// <summary>
    /// Opens the template in its own side database, so nothing touches the drawing the user has open.
    /// </summary>
    private static CadSymbolCatalog ReadTemplate(string templatePath)
    {
        ValidateTemplateFile(templatePath);

        var localCopy = CopyToLocalTempFile(templatePath);
        try
        {
            // A side database is still the AutoCAD kernel, and this call comes from a modeless
            // window, so it is held under the same document lock the open drawing needs.
            var document = Application.DocumentManager.MdiActiveDocument;
            using var documentLock = document?.LockDocument();

            using var suppressedPrompts = new SuppressedDrawingLoadPrompts();
            using var templateDatabase = new Database(false, true);
            try
            {
                // An empty password, not null. This is marshalled straight to a native string.
                templateDatabase.ReadDwgFile(localCopy, FileOpenMode.OpenForReadAndAllShare, allowCPConversion: true, password: "");

                // Releases the input file, so deleting the local copy below is not racing the reader.
                try { templateDatabase.CloseInput(true); } catch { }
            }
            catch (SEHException ex)
            {
                // The native reader faults rather than reporting, so on its own this says only
                // "External component has thrown an exception". Name the file and what actually
                // tends to be behind it.
                throw new InvalidOperationException(
                    "AutoCAD could not read the template '" + Path.GetFileName(templatePath)
                    + "'. A drawing that carries custom objects from another application, missing SHX "
                    + "fonts, or xrefs it cannot resolve can stop the reader even though the file opens "
                    + "normally in AutoCAD itself. If it is open in AutoCAD now, switch this page to the "
                    + "open drawing to read its blocks and line types from there.", ex);
            }

            return ReadTables(templateDatabase);
        }
        finally
        {
            try { File.Delete(localCopy); } catch { }
        }
    }

    /// <summary>
    /// Silences the prompts AutoCAD raises while loading an awkward drawing, and puts them back
    /// afterwards.
    ///
    /// Opening the Boston template in AutoCAD by hand raises three dialogs: unavailable Civil 3D
    /// custom objects, missing SHX fonts, and xrefs it cannot find. A dialog is fine when a person
    /// opened the drawing, but the reader here runs under a modeless window, and a modal prompt
    /// raised from that context is what takes the native side down. Nothing here changes what is
    /// read: proxies still come through as proxies, and the symbol tables this class wants are not
    /// affected by a substituted font or an unloaded xref.
    /// </summary>
    private sealed class SuppressedDrawingLoadPrompts : IDisposable
    {
        // All three only suppress a notification or supply a substitute. None of them changes which
        // objects are read, which is why they are safe to force here.
        private readonly object? _proxyNotice = TrySetSystemVariable("PROXYNOTICE", (short)0, 0);
        private readonly object? _xrefNotify = TrySetSystemVariable("XREFNOTIFY", (short)0, 0);
        private readonly object? _alternateFont = TrySetSystemVariable("FONTALT", "simplex.shx");

        public void Dispose()
        {
            TryRestoreSystemVariable("FONTALT", _alternateFont);
            TryRestoreSystemVariable("XREFNOTIFY", _xrefNotify);
            TryRestoreSystemVariable("PROXYNOTICE", _proxyNotice);
        }
    }

    /// <summary>
    /// Sets a system variable, returning its previous value so it can be put back, or null when this
    /// build has no such variable or would not take any of the values offered. Integer variables are
    /// 16 bit in some cases and 32 bit in others, hence more than one candidate.
    /// </summary>
    private static object? TrySetSystemVariable(string name, params object[] candidateValues)
    {
        object? previous;
        try { previous = CoreApplication.GetSystemVariable(name); }
        catch { return null; }

        foreach (var value in candidateValues)
        {
            try
            {
                CoreApplication.SetSystemVariable(name, value);
                return previous;
            }
            catch { }
        }
        return null;
    }

    private static void TryRestoreSystemVariable(string name, object? previous)
    {
        if (previous == null) { return; }
        try { CoreApplication.SetSystemVariable(name, previous); } catch { }
    }

    /// <summary>
    /// Checks the file is a drawing before handing it to the native reader, which faults rather than
    /// reporting when given something that is not one.
    /// </summary>
    private static void ValidateTemplateFile(string templatePath)
    {
        // Falling back to the open drawing here would look like the template simply had no blocks,
        // which is the least useful thing this could do.
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("The CAD template could not be found: " + templatePath, templatePath);
        }

        if (new FileInfo(templatePath).Length == 0)
        {
            throw new InvalidDataException(
                "The CAD template '" + Path.GetFileName(templatePath) + "' is empty. A download that did "
                + "not finish leaves a file of exactly this shape.");
        }

        // Every DWG and DWT AutoCAD can still read starts with an "AC10.." version tag. Checking it
        // turns a native fault into a message that says what is actually wrong, which for a template
        // fetched from SharePoint is usually a partial download or an HTML error page saved under a
        // .dwt name.
        Span<byte> header = stackalloc byte[6];
        using var stream = File.OpenRead(templatePath);
        var read = stream.Read(header);
        if (read < header.Length || !HasDrawingSignature(header))
        {
            throw new InvalidDataException(
                "The file '" + Path.GetFileName(templatePath) + "' is not a DWG or DWT drawing. It starts "
                + "with \"" + DescribeHeader(header[..Math.Max(0, read)]) + "\" rather than a drawing "
                + "version tag, so it is most likely a partial download or an error page saved under a "
                + "drawing name. Download the template again.");
        }
    }

    private static bool HasDrawingSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'A' && header[1] == (byte)'C' && header[2] == (byte)'1' && header[3] == (byte)'0';

    private static string DescribeHeader(ReadOnlySpan<byte> header)
    {
        var text = new System.Text.StringBuilder();
        foreach (var value in header)
        {
            text.Append(value >= 0x20 && value < 0x7f ? ((char)value).ToString() : "\\x" + value.ToString("X2"));
        }
        return text.ToString();
    }

    /// <summary>
    /// Reads a private local copy rather than the file where it sits. A template on a network share,
    /// in a OneDrive or SharePoint synced folder, or already open in AutoCAD, can fault the native
    /// reader outright instead of failing cleanly. A local copy is subject to none of that.
    /// </summary>
    private static string CopyToLocalTempFile(string templatePath)
    {
        var localCopy = Path.Combine(
            Path.GetTempPath(),
            "NGGISCAD_" + Guid.NewGuid().ToString("N") + Path.GetExtension(templatePath));
        File.Copy(templatePath, localCopy, overwrite: true);
        return localCopy;
    }

    private static CadSymbolCatalog ReadTables(Database database)
    {
        using var transaction = database.TransactionManager.StartTransaction();
        var catalog = new CadSymbolCatalog
        {
            Blocks = Sorted(ReadBlockNames(database, transaction)),
            LineTypes = Sorted(ReadLineTypes(database, transaction)),
            Layers = Sorted(ReadLayerNames(database, transaction))
        };
        transaction.Commit();
        return catalog;
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
