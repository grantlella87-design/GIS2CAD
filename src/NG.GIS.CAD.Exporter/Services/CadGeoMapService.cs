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

            try
            {
                geoData.CoordinateSystem = ResolveCoordinateSystem(wkText, outWkid);

                // Without this the location stays CoordinateTypeUnknown, which is what a location that
                // has not really been set looks like, and AutoCAD treats it accordingly. A state plane
                // projection is a grid system, so that is what it is declared as.
                geoData.TypeOfCoordinates = TypeOfCoordinates.CoordinateTypeGrid;

                // The same point twice. The drawing is in the system just named, so the point on the
                // ground and the point in the drawing are the same numbers, and the transform is an
                // identity.
                geoData.DesignPoint = new Point3d(anchorX, anchorY, 0.0);
                geoData.ReferencePoint = new Point3d(anchorX, anchorY, 0.0);

                geoData.UpdateTransformationMatrix();
            }
            catch
            {
                // Attaching first means a failure now leaves a location on the drawing that says almost
                // nothing. Worse, it would answer yes to "does this drawing have a location", so every
                // export after it would skip setting one and never recover. Taken back off instead.
                try { geoData.EraseFromDb(); } catch { }
                throw;
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
    /// What to hand AutoCAD as the coordinate system.
    ///
    /// AutoCAD keeps its own library of coordinate systems, each with an ID of its own, and that ID is
    /// what a geographic location is normally set from. It will take a full definition instead, which is
    /// what the well known text is, but going through the library first means the drawing ends up naming
    /// the same system AutoCAD would have named had someone picked it from the dialog.
    ///
    /// Tried by well known text and then by EPSG code, since the library indexes both, and falling back
    /// to the well known text itself when neither is recognised. Every step is a way of saying the same
    /// projection, so a fallback narrows nothing.
    /// </summary>
    private static string ResolveCoordinateSystem(string wkText, int outWkid)
    {
        foreach (var candidate in new[] { wkText, "EPSG:" + outWkid.ToString(CultureInfo.InvariantCulture) })
        {
            try
            {
                using var system = GeoCoordinateSystem.Create(candidate);
                if (!string.IsNullOrWhiteSpace(system?.ID)) { return system.ID; }
            }
            catch
            {
                // Not a form this library recognises. The next candidate, or the raw definition below.
            }
        }

        return wkText;
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
