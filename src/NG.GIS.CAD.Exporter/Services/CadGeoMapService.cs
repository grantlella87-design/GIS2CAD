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
    /// <summary>Aerial. The point is comparing exported features against real ground, not a drawn map.</summary>
    private const short AerialMode = 1;

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
    public string TurnOn(int outWkid, double anchorX, double anchorY)
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

        // Short first and then int, the same way this codebase already sets PROXYNOTICE and XREFNOTIFY.
        // Which of the two a system variable accepts is not something the managed API tells you, and
        // the wrong one throws, so both are tried rather than guessed at.
        Exception? failure = null;
        foreach (object value in new object[] { AerialMode, (int)AerialMode })
        {
            try
            {
                CoreApplication.SetSystemVariable("GEOMAPMODE", value);
                return ("Geographic map turned on (aerial). " + locationNote).TrimEnd();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        // Most likely no Autodesk sign-in, which is what the online map service needs. The export
        // itself has already been written by this point, so this is worth reporting and no more.
        return ("The geographic map could not be turned on, so the export is there without it: "
                + (failure?.Message ?? "the setting was refused.") + " " + locationNote).TrimEnd();
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
            geoData.PostToDb();

            // Each part on its own, so a failure names the part rather than the whole. Not all of these
            // managed setters are implemented in every release -- one of them answers eNotImplementedYet
            // -- and a single try around the lot reports that without saying which, which is the
            // difference between knowing what to do next and guessing again.
            //
            // They are also independent. A setter that is missing does not stop the others from landing,
            // and a location carrying most of what it should is worth more than none at all.
            var failed = new List<string>();

            void Attempt(string part, Action set)
            {
                try { set(); }
                catch (Exception ex) { failed.Add(part + " (" + ex.Message + ")"); }
            }

            // The coordinate system is offered every way of naming it in turn, stopping at the first that
            // is accepted. One string was refused with eNotImplementedYet, which reads as the setter not
            // existing but is also what some of these APIs answer for input they cannot use. Offering a
            // different form is the only way to tell those apart, and each refusal is recorded with the
            // form that drew it so the answer is in the message rather than in another guess.
            var coordinateSystemFailed = true;
            foreach (var candidate in CoordinateSystemCandidates(wkText, outWkid))
            {
                var before = failed.Count;
                Attempt("coordinate system as " + DescribeCandidate(candidate), () => geoData.CoordinateSystem = candidate);
                if (failed.Count == before) { coordinateSystemFailed = false; break; }
            }

            // The rest are set even when every form of the coordinate system was refused, rather than
            // giving up here. They were all accepted last time, and confirming that stays true is what
            // says the refusal is about this one setter rather than about the location as a whole.

            // Without this the location stays CoordinateTypeUnknown, which is what a location that has
            // not really been set looks like. A state plane projection is a grid system.
            Attempt("coordinate type", () => geoData.TypeOfCoordinates = TypeOfCoordinates.CoordinateTypeGrid);

            // The same point twice. The drawing is in the system just named, so the point on the ground
            // and the point in the drawing are the same numbers, and the transform is an identity.
            Attempt("design point", () => geoData.DesignPoint = new Point3d(anchorX, anchorY, 0.0));
            Attempt("reference point", () => geoData.ReferencePoint = new Point3d(anchorX, anchorY, 0.0));
            Attempt("transformation matrix", () => geoData.UpdateTransformationMatrix());

            if (coordinateSystemFailed)
            {
                // Taken back off, so the next export tries again rather than finding a location that
                // exists and describes nothing.
                try { geoData.EraseFromDb(); } catch { }
                throw new InvalidOperationException(
                    "this release refused the coordinate system, so the location would have named no "
                    + "projection. What was tried, and what each said: " + string.Join("; ", failed));
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
                    using (system)
                    {
                        if (system.EPSGcode == outWkid) { Add(system.ID); break; }
                    }
                }
            }
            catch
            {
                // No library to walk, or it refused. The definition below still stands.
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
