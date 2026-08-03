using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
// Aliased because Autodesk.AutoCAD.Geometry and the ArcGIS runtime both have a lot of geometry names,
// and this file is one of the few places both are in scope at once.
using EsriSpatialReference = Esri.ArcGISRuntime.Geometry.SpatialReference;
using CoreApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>
/// Sets the drawing's geographic location and turns AutoCAD's geographic map on, so the features that
/// were just exported can be seen against the ground rather than on blank paper.
///
/// This is AutoCAD's own map, the one GEOMAP switches on. Nothing is downloaded into the drawing and no
/// file is referenced: it is a view, so it costs nothing to leave on and nothing to turn off.
/// </summary>
public sealed class CadGeoMapService
{
    /// <summary>
    /// The GEOMAP options that show imagery, preferred first.
    ///
    /// GEOMAPMODE is not how this is done. It is documented read-only, which is what eInvalidInput was
    /// saying all along: the refusal was about writing the variable at all, not about the value, which is
    /// why every map type was refused identically. The command is the only way in.
    ///
    /// GEOMAP takes four options and only four -- Aerial, Road, Hybrid, None. There is no Esri keyword to
    /// try, whatever service is behind the imagery: AcGeoMapType names an Esri member, but that enum is
    /// the API's vocabulary rather than the command's. Aerial is the plain satellite view and is what is
    /// wanted here; Hybrid is the same imagery with roads drawn over it, so it is worth falling back to
    /// and Road, being vector only, is not.
    /// </summary>
    private static readonly (string Name, string Keyword)[] ImageryKeywords =
    {
        ("aerial", "_Aerial"),
        ("hybrid", "_Hybrid")
    };

    /// <summary>
    /// AutoCAD's own name for the systems this work uses, which is what a geographic location holds.
    ///
    /// Read off the coordinate system list AutoCAD shows when a location is set by hand, so these are
    /// the same names the dialog would have written. Only NAD83, because that is what the profile and
    /// the services speak in, and because an EPSG code does not name one system on its own: 2805 is both
    /// MAHP and HARN/NE.MA in that list, and choosing between them is a question about datum rather than
    /// something the code answers.
    ///
    /// Anything not listed goes to the library search instead, so this is a shortcut rather than a
    /// limit. Setting a location this way is the same thing the dialog does, and the same thing
    /// Map 3D and Civil 3D assign a coordinate system with.
    /// </summary>
    private static readonly Dictionary<int, string> AutoCadCoordinateSystemIds = new()
    {
        [2249] = "MA83F",   // NAD83 Massachusetts Mainland, US survey feet
        [26986] = "MA83",   // NAD83 Massachusetts Mainland, metres
        [3438] = "RI83F",   // NAD83 Rhode Island, US survey feet
        [32130] = "RI83"    // NAD83 Rhode Island, metres
    };

    /// <summary>
    /// Gives the drawing a geographic location if it has none, then switches the map on.
    ///
    /// The location has to exist first: without one the map has no way to know where the drawing sits on
    /// the earth, and GEOMAPMODE does nothing.
    ///
    /// Setting it is safe here for one specific reason. The features were written in
    /// <paramref name="outWkid"/>, so the drawing's coordinates already are that system's coordinates,
    /// which makes the design point and the reference point the same point and the transformation an
    /// identity. Nothing is being guessed at or converted: the drawing is simply being told the system
    /// it was already drawn in.
    ///
    /// An existing location is never replaced. One that is already there was either set deliberately or
    /// came with the template, and either way it describes the whole drawing rather than this export.
    /// </summary>
    /// <param name="anchorX">A point in drawing coordinates to anchor the location to, X.</param>
    /// <param name="anchorY">The same point, Y. The centre of what was exported is a good choice.</param>
    public async Task<string> TurnOnAsync(int outWkid, double anchorX, double anchorY)
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return "No drawing is open, so the geographic map was not turned on.";
        }

        string locationNote;
        try
        {
            locationNote = EnsureGeographicLocation(document, outWkid, anchorX, anchorY);
        }
        catch (Exception ex)
        {
            locationNote = "The drawing's geographic location could not be set: " + ex.Message + ".";
        }

        // Asked of the drawing rather than inferred from whether the attach threw. A location that was
        // already there is as good as one just set, and one that failed to attach leaves the map with
        // nothing to work from however the attempt ended.
        if (!HasGeographicLocation(document))
        {
            return (locationNote + " The map was left off, because it has no way to know where the "
                    + "drawing sits on the earth without one.").TrimStart();
        }

        var refusals = new List<string>();

        foreach (var (name, keyword) in ImageryKeywords)
        {
            try
            {
                // Run in a command context rather than sent to the command line. A keyword this release
                // does not know comes back as an exception here, where the same keyword typed at the
                // prompt would leave AutoCAD waiting on an answer nobody is there to give.
                await Application.DocumentManager.ExecuteInCommandContextAsync(_ =>
                {
                    document.Editor.Command("._GEOMAP", keyword);
                    return Task.CompletedTask;
                }, null);

                if (IsMapOn())
                {
                    return ("Geographic map turned on (" + name + "). " + locationNote).TrimEnd();
                }

                refusals.Add(keyword + " (accepted, but the map stayed off)");
            }
            catch (Exception ex)
            {
                refusals.Add(keyword + " (" + ex.Message + ")");
            }
        }

        return ("The geographic map could not be turned on, so the export is there without it. What was "
                + "tried, and what each said: " + string.Join("; ", refusals)
                + ". GEOMAP needs an Autodesk sign-in as well as a geographic location, so if the "
                + "drawing has one and this still refuses, signing in to AutoCAD is the thing to check. "
                + locationNote).TrimEnd();
    }

    /// <summary>
    /// Whether a map is showing. GEOMAPMODE is read-only, so it cannot turn the map on, but it still
    /// reports what the map is doing, which is how the command's result is checked.
    /// </summary>
    private static bool IsMapOn()
    {
        try
        {
            var mode = CoreApplication.GetSystemVariable("GEOMAPMODE");
            return mode != null && Convert.ToInt32(mode, CultureInfo.InvariantCulture) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attaches a geographic location describing the system the features were written in, unless the
    /// drawing already carries one.
    ///
    /// Returns what to tell the user, empty when there was already a location and nothing was done.
    /// </summary>
    private static string EnsureGeographicLocation(Document document, int outWkid, double anchorX, double anchorY)
    {
        if (HasGeographicLocation(document)) { return string.Empty; }

        // AutoCAD is given the system's well known text. The runtime is the same source the export used
        // to ask the services for this projection, so the drawing is described by the definition its
        // coordinates actually came from rather than by a name looked up somewhere else.
        var wkText = EsriSpatialReference.Create(outWkid)?.WkText;
        if (string.IsNullOrWhiteSpace(wkText))
        {
            return "The drawing has no geographic location and one could not be built, because "
                   + SpatialReferenceNames.Describe(outWkid) + " has no well known text to describe it "
                   + "to AutoCAD. Run GEOGRAPHICLOCATION once to set one and the map will come on after "
                   + "every export.";
        }

        using var documentLock = document.LockDocument();
        var database = document.Database;

        // The working database is pointed at this document for the duration. PostToDb takes no arguments
        // and has to find a database for itself, and from a modeless window there is nothing making the
        // working one the document's.
        var previousWorkingDatabase = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = database;

            // Model space by name rather than through a transaction on the block table. PostToDb
            // attaches the object itself, so opening a transaction around it would only add something
            // else to go wrong between creating the location and it being attached.
            var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(database);

            using var geoData = new GeoLocationData();

            // Attached before it is configured, which is the opposite of the obvious order and is the
            // point. Most of what a geographic location holds is written through the database it belongs
            // to, so setting those on an object that has not been attached yet has no database to write
            // to, which is what eNoDatabase was saying. The block table record id is the exception: it is
            // what PostToDb reads to know where it is going, so it has to come first.
            geoData.BlockTableRecordId = modelSpaceId;

            var failed = new List<string>();
            var candidates = CoordinateSystemCandidates(wkText, outWkid);

            // Tried before attaching as well as after. The order was settled the other way round for the
            // points, which need the database and answered eNoDatabase without it -- but the coordinate
            // system is a name rather than something written through the database, and it is the one
            // part still being refused after attaching. Both moments are cheap, and only one of them has
            // to work.
            var coordinateSystemSet = TrySetCoordinateSystem(geoData, candidates, "before attaching", failed);

            geoData.PostToDb();

            if (!coordinateSystemSet)
            {
                coordinateSystemSet = TrySetCoordinateSystem(geoData, candidates, "after attaching", failed);
            }

            // Each part on its own, so a failure names the part rather than the whole. Not all of these
            // managed setters are implemented in every release -- one of them answers eNotImplementedYet
            // -- and a single try around the lot reports that without saying which, which is the
            // difference between knowing what to do next and guessing again.
            //
            // They are also independent. A setter that is missing does not stop the others from landing,
            // and a location carrying most of what it should is worth more than none at all.
            void Attempt(string part, Action set)
            {
                try { set(); }
                catch (Exception ex) { failed.Add(part + " (" + ex.Message + ")"); }
            }

            // The rest are set even when every form of the coordinate system was refused, rather than
            // giving up here. They were all accepted last time, and confirming that stays true is what
            // says the refusal is about this one setter rather than about the location as a whole.

            // Without this the location stays CoordinateTypeUnknown, which is what a location that has
            // not really been set looks like. A state plane projection is a grid system.
            Attempt("coordinate type", () => geoData.TypeOfCoordinates = TypeOfCoordinates.CoordinateTypeGrid);

            // The same point twice, and deliberately so. These two are what tie drawing space to the
            // projection: the design point is in drawing coordinates and the reference point is in the
            // coordinates of the projected system named above -- not in longitude and latitude, which is
            // the easy thing to assume and is wrong. Autodesk's own ObjectARX sample says it outright,
            // that the "reference point is in the coordinates of the projected coordinate system and not
            // in geodetic", and it has to be: the coordinate system already describes projected against
            // geodetic, so this pair has nothing left to describe except drawing against projected.
            //
            // The features were written in that same projection, so both are the same numbers here and
            // the transform between them is an identity. Converting a longitude and latitude for this
            // would introduce an error rather than remove one.
            Attempt("design point", () => geoData.DesignPoint = new Point3d(anchorX, anchorY, 0.0));
            Attempt("reference point", () => geoData.ReferencePoint = new Point3d(anchorX, anchorY, 0.0));

            // North is left alone, not overlooked. ObjectARX has setNorthDirectionVector, but the managed
            // GeoLocationData.NorthDirection is get-only, so from .NET it is not ours to set -- the same
            // shape of thing as GEOMAPMODE, and worth writing down so it is not tried again. The default
            // is drawing +Y, which is what a drawing sitting in its projection rather than rotated to it
            // wants anyway, so there is nothing to correct.

            Attempt("transformation matrix", () => geoData.UpdateTransformationMatrix());

            if (!coordinateSystemSet)
            {
                // Taken back off, so the next export tries again rather than finding a location that
                // exists and describes nothing.
                try { geoData.EraseFromDb(); } catch { }
                throw new InvalidOperationException(
                    "this release refused the coordinate system, so the location would have named no "
                    + "projection. " + DescribeCoordinateSystemRefusal(failed));
            }

            if (failed.Count > 0)
            {
                return "Geographic location set to " + SpatialReferenceNames.Describe(outWkid)
                       + ", though this release did not accept all of it: " + string.Join("; ", failed)
                       + ". The map may still place correctly; if it does not, that list is why.";
            }
        }
        finally
        {
            // Only when there was one. Assigning null here would leave AutoCAD worse off than the
            // failure this is unwinding from.
            if (previousWorkingDatabase != null)
            {
                try { HostApplicationServices.WorkingDatabase = previousWorkingDatabase; } catch { }
            }
        }

        return "Geographic location set to " + SpatialReferenceNames.Describe(outWkid) + ".";
    }

    /// <summary>
    /// Offers each way of naming the projection in turn, stopping at the first the release accepts.
    /// Records every refusal with the form and the moment that drew it, so a message says which of them
    /// was being tried rather than that something failed.
    /// </summary>
    private static bool TrySetCoordinateSystem(
        GeoLocationData geoData, IReadOnlyList<string> candidates, string when, List<string> failed)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                geoData.CoordinateSystem = candidate;
                return true;
            }
            catch (Exception ex)
            {
                failed.Add("coordinate system as " + DescribeCandidate(candidate) + " " + when
                           + " (" + ex.Message + ")");
            }
        }

        return false;
    }

    /// <summary>
    /// Says what a refused coordinate system means, and what can be done about it.
    ///
    /// Every name was refused, and refused differently: a short identifier crashes inside the native
    /// side, a full definition comes back as not implemented. Different answers to different strings
    /// mean the setter is reachable and it is the lookup behind it that is not, so the library of
    /// coordinate systems is asked directly. If that will not answer either, no string was ever going to
    /// work and the code cannot make one.
    ///
    /// The way out is real and is worth saying: setting a location once by hand loads that library and
    /// leaves the drawing with a location, and a location already there is never replaced, so every
    /// export after it goes straight to turning the map on.
    /// </summary>
    private static string DescribeCoordinateSystemRefusal(List<string> failed)
    {
        var tried = "What was tried, and what each said: " + string.Join("; ", failed) + ".";

        return CoordinateSystemLibraryAnswers()
            ? tried + " The coordinate system library does answer in this drawing, so it is the name "
              + "rather than the library that is being refused."
            : tried + " The coordinate system library does not answer at all here, which is why no name "
              + "for the projection was accepted and why the errors differ by string rather than "
              + "agreeing. Run GEOGRAPHICLOCATION once and set the location by hand: that loads the "
              + "library and leaves the drawing with a location, and an existing location is never "
              + "replaced, so exports after it will go straight to turning the map on.";
    }

    /// <summary>
    /// Whether this drawing's coordinate system library can be read at all. One entry is enough: the
    /// question is whether it is there, not what is in it.
    /// </summary>
    private static bool CoordinateSystemLibraryAnswers()
    {
        try
        {
            foreach (var system in GeoCoordinateSystem.CreateAll())
            {
                using (system) { return true; }
            }
        }
        catch
        {
            // No library to read, which is the answer.
        }

        return false;
    }

    /// <summary>
    /// Names a candidate for the status line without printing a whole well known text at the user, which
    /// runs to hundreds of characters and would bury the part worth reading.
    /// </summary>
    private static string DescribeCandidate(string candidate) =>
        candidate.Length <= 24 ? "\"" + candidate + "\"" : "its full definition";

    /// <summary>
    /// Every way of naming this projection that is worth offering AutoCAD, best first.
    ///
    /// The setter answered eNotImplementedYet to one string, which reads as the method not existing but
    /// is also what some of these APIs return for input they cannot use. Those are worth telling apart,
    /// and the only way to tell them apart is to offer a different string and see whether the answer
    /// changes. If every form is refused identically then the setter really is missing, and no amount of
    /// rephrasing will help.
    ///
    /// Best first means AutoCAD's own library ID, since that is what a geographic location set from the
    /// dialog would hold. The library is asked by definition, then by EPSG code, then by walking it for
    /// a system carrying that code, which is the one route that does not depend on the library parsing
    /// anything. The definition itself is last, as the form that needs no library at all.
    /// </summary>
    private static IReadOnlyList<string> CoordinateSystemCandidates(string wkText, int outWkid)
    {
        var candidates = new List<string>();

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !candidates.Contains(value)) { candidates.Add(value); }
        }

        var epsg = "EPSG:" + outWkid.ToString(CultureInfo.InvariantCulture);

        // The ID first, where it is known outright. An EPSG code does not name one system in this
        // library: 2805 is both MAHP and HARN/NE.MA, and picking between them is a question about datum
        // rather than something a code answers. For the systems this work uses the answer is settled, so
        // it is written down rather than searched for.
        if (AutoCadCoordinateSystemIds.TryGetValue(outWkid, out var knownId)) { Add(knownId); }

        foreach (var lookup in new[] { wkText, epsg })
        {
            try
            {
                using var system = GeoCoordinateSystem.Create(lookup);
                Add(system?.ID);
            }
            catch
            {
                // Not a form this library parses. The next lookup, or the walk below.
            }
        }

        // Walking the library asks it nothing except what it already holds, so it still finds the system
        // when the parsing routes have failed. Only worth the walk when they have.
        if (candidates.Count == 0)
        {
            try
            {
                foreach (var system in GeoCoordinateSystem.CreateAll())
                {
                    // Each entry in its own guard. The library runs to thousands of systems and this one
                    // throws on some of them, so a single guard around the whole walk lost every entry
                    // after the first bad one -- which is how a library that demonstrably holds 2249 was
                    // walked without finding it.
                    try
                    {
                        using (system)
                        {
                            if (system.EPSGcode == outWkid) { Add(system.ID); break; }
                        }
                    }
                    catch
                    {
                        // This entry cannot say what it is. The next one still can.
                    }
                }
            }
            catch
            {
                // No library to walk at all, which the forms below do not need.
            }
        }

        Add(epsg);
        Add(wkText);
        return candidates;
    }

    /// <summary>
    /// Whether the drawing carries a geographic location.
    ///
    /// Read through the document lock, because this runs from a modeless window and the native side
    /// throws without one, the same as everywhere else this code touches the drawing.
    /// </summary>
    private static bool HasGeographicLocation(Document document)
    {
        try
        {
            using var documentLock = document.LockDocument();
            return !document.Database.GeoDataObject.IsNull;
        }
        catch
        {
            // An older drawing, or one where the property is not available, is treated as having none.
            // Being wrong in this direction attempts a location that then fails and says so, which is
            // recoverable; being wrong the other way would silently leave the map off.
            return false;
        }
    }
}
