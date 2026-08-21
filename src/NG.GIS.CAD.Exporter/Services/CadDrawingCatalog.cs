using System.Globalization;
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

    /// <summary>Paper space viewports, which the strip map index takes its sheet size from.</summary>
    public IReadOnlyList<CadViewport> Viewports { get; init; } = Array.Empty<CadViewport>();

    /// <summary>
    /// What was left out and why, so a template that has been quietly filtered says so rather than
    /// looking like a template that simply has fewer blocks in it than the drafter remembers.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public bool AnythingSkipped => Skipped.Count > 0;

    /// <summary>One line naming what was dropped, for the status area. Empty when nothing was.</summary>
    public string SkippedSummary => Skipped.Count == 0
        ? string.Empty
        : "Some of the template could not be read and was left out: " + string.Join("; ", Skipped) + ".";
}

/// <summary>A paper space viewport in one of the drawing's layouts.</summary>
public sealed class CadViewport
{
    public string LayoutName { get; init; } = string.Empty;
    public int Number { get; init; }

    /// <summary>Width and height in the layout's paper units, not in drawing units.</summary>
    public double Width { get; init; }
    public double Height { get; init; }

    public bool IsMillimetres { get; init; }

    /// <summary>Stable key used to remember the choice in the profile.</summary>
    public string Key => LayoutName + " / viewport " + Number.ToString(CultureInfo.InvariantCulture);

    public string UnitAbbreviation => IsMillimetres ? "mm" : "in";

    public string Display =>
        LayoutName + " — viewport " + Number.ToString(CultureInfo.InvariantCulture)
        + " (" + Width.ToString("0.##", CultureInfo.InvariantCulture) + " × "
        + Height.ToString("0.##", CultureInfo.InvariantCulture) + " " + UnitAbbreviation + ")";
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

    /// <summary>
    /// Reads the four tables, each on its own terms.
    ///
    /// One table failing used to lose all four. A template carrying Civil 3D objects can have a layout
    /// this build cannot open at all, and the whole catalog came back as an error, so a drawing with
    /// perfectly readable blocks and line types offered neither. Each table is read inside its own
    /// guard now: what can be read is returned, and what could not is named in Skipped rather than
    /// thrown.
    /// </summary>
    private static CadSymbolCatalog ReadTables(Database database)
    {
        using var transaction = database.TransactionManager.StartTransaction();

        var skipped = new List<string>();

        var blocks = ReadTable(() => ReadBlockNames(database, transaction, skipped), "blocks", skipped);
        var lineTypes = ReadTable(() => ReadLineTypes(database, transaction, skipped), "line types", skipped);
        var layers = ReadTable(() => ReadLayerNames(database, transaction, skipped), "layers", skipped);
        var viewports = ReadTable(() => ReadViewports(database, transaction, skipped), "viewports", skipped)
                        ?? new List<CadViewport>();

        var catalog = new CadSymbolCatalog
        {
            Blocks = Sorted(blocks ?? new List<string>()),
            LineTypes = Sorted(lineTypes ?? new List<string>()),
            Layers = Sorted(layers ?? new List<string>()),
            Viewports = viewports,
            Skipped = skipped
        };

        transaction.Commit();
        return catalog;
    }

    /// <summary>
    /// Runs one table read, turning a failure into a note rather than an exception.
    ///
    /// SEHException is caught by name as well as Exception because the native side faults rather than
    /// reporting when it meets a class no loaded module implements, and a fault is not an Exception
    /// every runtime configuration will route through the general catch.
    /// </summary>
    private static T? ReadTable<T>(Func<T> read, string what, List<string> skipped) where T : class
    {
        try
        {
            return read();
        }
        catch (SEHException ex)
        {
            skipped.Add("the " + what + " could not be read (" + ex.Message + ")");
            return null;
        }
        catch (Exception ex)
        {
            skipped.Add("the " + what + " could not be read (" + ex.Message + ")");
            return null;
        }
    }

    /// <summary>
    /// Opens one object and hands it back only if it is the type wanted and is something this build
    /// actually implements.
    ///
    /// A drawing can name a class that no loaded module provides: Civil 3D alignments and corridors in
    /// a template opened in plain AutoCAD, or anything from a vertical this installation does not have.
    /// AutoCAD keeps those as proxies. A proxy answers enough questions to be listed and none of the
    /// ones that matter for copying it into another drawing, so anything offered from here has to be a
    /// real object of the type asked for.
    ///
    /// Every failure is a skip. One unreadable record in a table of hundreds is not a reason to have no
    /// table.
    /// </summary>
    private static T? OpenIfUsable<T>(Transaction transaction, ObjectId id) where T : DBObject
    {
        // An erased or invalid id can be walked over without opening it at all, which is cheaper and
        // cannot fault.
        if (id.IsNull || id.IsErased || !id.IsValid) { return null; }

        try
        {
            var opened = transaction.GetObject(id, OpenMode.ForRead, openErased: false, forceOpenOnLockedLayer: false);
            if (opened is ProxyObject or ProxyEntity) { return null; }

            return opened as T;
        }
        catch (SEHException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads every paper space viewport in every layout, so the strip map index can be sized from the
    /// one the drawing already uses rather than from typed-in numbers.
    /// </summary>
    private static List<CadViewport> ReadViewports(Database database, Transaction transaction, List<string> skipped)
    {
        var viewports = new List<CadViewport>();
        var layouts = OpenIfUsable<DBDictionary>(transaction, database.LayoutDictionaryId);
        if (layouts == null) { skipped.Add("the layouts could not be opened"); return viewports; }

        var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(database);
        var unusable = 0;

        foreach (DBDictionaryEntry entry in layouts)
        {
            var layout = OpenIfUsable<Layout>(transaction, entry.Value);
            if (layout == null) { unusable++; continue; }

            // Model space is in this dictionary too, and has no paper to size a sheet from. It used to
            // be filtered by viewport number instead, which stopped working the moment the numbers did.
            try
            {
                if (layout.BlockTableRecordId == modelSpaceId) { continue; }
                viewports.AddRange(ReadLayoutViewports(transaction, layout));
            }
            catch (Exception)
            {
                // One layout this build cannot walk is one layout's worth of viewports missing, not a
                // template without viewports.
                unusable++;
            }
        }

        if (unusable > 0)
        {
            skipped.Add(unusable + (unusable == 1 ? " layout" : " layouts") + " that could not be read");
        }

        return viewports
            .OrderBy(v => v.LayoutName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Number)
            .ToList();
    }

    /// <summary>
    /// The usable viewports on one layout, read from the layout's own paper space rather than from
    /// <c>Layout.GetViewports</c>.
    ///
    /// That method is why the list was empty for a template. It hands back an array the layout keeps of
    /// its viewports, and that array is filled in when the layout is activated and regenerated. The
    /// drawing that is open in AutoCAD has been through that, so reading it worked there and the list
    /// populated from the open drawing as expected. A template opened into a side database has not been
    /// through it and never will be -- nothing activates a layout in a database that is never shown --
    /// so the array came back empty and so did the dropdown, for a template that plainly had viewports
    /// in it.
    ///
    /// Walking the paper space block instead reads the viewport entities themselves, which are in the
    /// file whether or not anything has drawn them.
    /// </summary>
    private static List<CadViewport> ReadLayoutViewports(Transaction transaction, Layout layout)
    {
        var paperSpace = OpenIfUsable<BlockTableRecord>(transaction, layout.BlockTableRecordId);
        if (paperSpace == null) { return new List<CadViewport>(); }

        var isMillimetres = layout.PlotPaperUnits == PlotPaperUnit.Millimeters;

        var found = new List<Viewport>();
        foreach (ObjectId id in paperSpace)
        {
            // Everything else on the sheet is walked past rather than opened as a viewport, and
            // anything this build cannot open at all -- a Civil 3D label or table sitting on the same
            // paper -- is simply not a viewport and is walked past as well.
            var viewport = OpenIfUsable<Viewport>(transaction, id);
            if (viewport == null) { continue; }

            try
            {
                if (viewport.Width > 0 && viewport.Height > 0) { found.Add(viewport); }
            }
            catch (Exception)
            {
                // A viewport that will not report its own size cannot size a sheet.
            }
        }

        // Number 1 is the paper space viewport itself: the whole sheet, not a window onto the model.
        // Sizing a strip map from it would measure the paper rather than the drawing area.
        //
        // The numbers come from the same activation that fills the array above, so in a side database
        // they can all read 0 and the filter would then throw everything away -- trading an empty list
        // for an empty list. When no viewport claims a number above 1, the first entity in paper space
        // is taken as the sheet instead, which is the order AutoCAD writes them in.
        var numbered = found.Where(v => v.Number > 1).ToList();
        var usable = numbered.Count > 0 ? numbered : found.Skip(1).ToList();

        var result = new List<CadViewport>();
        var position = 2;
        foreach (var viewport in usable)
        {
            result.Add(new CadViewport
            {
                LayoutName = layout.LayoutName,
                // Its own number where it has one, otherwise its position, so two viewports on a layout
                // are still told apart in the list and by the key the profile remembers.
                Number = viewport.Number > 1 ? viewport.Number : position,
                Width = viewport.Width,
                Height = viewport.Height,
                IsMillimetres = isMillimetres
            });
            position++;
        }

        return result;
    }

    /// <summary>
    /// The blocks worth offering: named, not a layout, not an xref, and made only of entities this
    /// build can actually copy.
    ///
    /// The entity scan is the point of this. A block listed here is one the user can pick on page 4 and
    /// have written into their drawing, and a block holding a Civil 3D object that came through as a
    /// proxy cannot be written into a plain AutoCAD drawing at all -- the clone fails and takes the
    /// export with it. Offering it and failing later is worse than not offering it: the failure arrives
    /// at the end of a long export, against a choice made several pages ago.
    ///
    /// An xref block is left out for a related reason. It is a reference to another file rather than
    /// geometry, so copying it into the drawing brings across a link that may not resolve there.
    /// </summary>
    private static List<string> ReadBlockNames(Database database, Transaction transaction, List<string> skipped)
    {
        var names = new List<string>();
        var table = OpenIfUsable<BlockTable>(transaction, database.BlockTableId);
        if (table == null) { skipped.Add("the block table could not be opened"); return names; }

        var unusable = 0;

        foreach (ObjectId id in table)
        {
            var record = OpenIfUsable<BlockTableRecord>(transaction, id);
            if (record == null) { unusable++; continue; }

            try
            {
                if (record.IsAnonymous || record.IsLayout) { continue; }
                if (record.IsFromExternalReference || record.IsFromOverlayReference) { unusable++; continue; }
                if (!BlockCanBeCopied(transaction, record)) { unusable++; continue; }

                names.Add(record.Name);
            }
            catch (Exception)
            {
                // A record that cannot even be asked its own name is not one to offer.
                unusable++;
            }
        }

        if (unusable > 0)
        {
            skipped.Add(unusable + (unusable == 1 ? " block that could not" : " blocks that could not")
                + " be copied into a drawing (custom objects from another application, or external references)");
        }

        return names;
    }

    /// <summary>
    /// Whether every entity in a block is something this build implements, so copying the block will
    /// not fault partway through.
    ///
    /// Stops at the first thing it cannot open, because one is enough to answer the question, and gives
    /// up rather than guessing if the walk itself fails.
    /// </summary>
    private static bool BlockCanBeCopied(Transaction transaction, BlockTableRecord record)
    {
        try
        {
            foreach (ObjectId id in record)
            {
                if (OpenIfUsable<Entity>(transaction, id) == null) { return false; }
            }

            return true;
        }
        catch (SEHException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The line types worth offering: the ones whose every segment can be resolved.
    ///
    /// A line type built from shapes or text points at a style, and that style points at an SHX. When
    /// the SHX is not on this machine the style is still in the file and still names it, and a line type
    /// that depends on it draws as a plain line at best. Reading the style is what finds that out, so a
    /// line type whose style will not open is left out rather than offered and then drawn wrong.
    /// </summary>
    private static List<string> ReadLineTypes(Database database, Transaction transaction, List<string> skipped)
    {
        var names = new List<string>();
        var table = OpenIfUsable<LinetypeTable>(transaction, database.LinetypeTableId);
        if (table == null) { skipped.Add("the line type table could not be opened"); return names; }

        var unusable = 0;

        foreach (ObjectId id in table)
        {
            var record = OpenIfUsable<LinetypeTableRecord>(transaction, id);
            if (record == null) { unusable++; continue; }

            try
            {
                if (!LineTypeStylesResolve(transaction, record)) { unusable++; continue; }
                names.Add(record.Name);
            }
            catch (Exception)
            {
                unusable++;
            }
        }

        if (unusable > 0)
        {
            skipped.Add(unusable + (unusable == 1 ? " line type" : " line types")
                + " that depend on a shape or font file this machine does not have");
        }

        return names;
    }

    /// <summary>Whether every style a line type's segments point at can be opened.</summary>
    private static bool LineTypeStylesResolve(Transaction transaction, LinetypeTableRecord record)
    {
        try
        {
            for (var i = 0; i < record.NumDashes; i++)
            {
                var styleId = record.ShapeStyleAt(i);
                if (styleId.IsNull) { continue; }

                if (OpenIfUsable<TextStyleTableRecord>(transaction, styleId) == null) { return false; }
            }

            return true;
        }
        catch (SEHException)
        {
            return false;
        }
        catch (Exception)
        {
            // A line type that will not describe its own dashes is one to leave alone.
            return false;
        }
    }

    private static List<string> ReadLayerNames(Database database, Transaction transaction, List<string> skipped)
    {
        var names = new List<string>();
        var table = OpenIfUsable<LayerTable>(transaction, database.LayerTableId);
        if (table == null) { skipped.Add("the layer table could not be opened"); return names; }

        var unusable = 0;

        foreach (ObjectId id in table)
        {
            var record = OpenIfUsable<LayerTableRecord>(transaction, id);
            if (record == null) { unusable++; continue; }

            try { names.Add(record.Name); }
            catch (Exception) { unusable++; }
        }

        if (unusable > 0)
        {
            skipped.Add(unusable + (unusable == 1 ? " layer" : " layers") + " that could not be read");
        }

        return names;
    }

    private static IReadOnlyList<string> Sorted(List<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
}
