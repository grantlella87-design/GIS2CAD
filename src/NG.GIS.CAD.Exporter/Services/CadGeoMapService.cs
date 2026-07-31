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
            return "The drawing's geographic location could not be set, so the map was left off: "
                   + ex.Message;
        }

        if (locationNote.Length > 0 && !HasGeographicLocation(document))
        {
            return locationNote;
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

        // Model space by name rather than through a transaction on the block table. PostToDb attaches
        // the object itself, so opening a transaction around it would only add something else to go
        // wrong between creating the location and it being attached.
        var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(database);

        using var geoData = new GeoLocationData();
        geoData.BlockTableRecordId = modelSpaceId;
        geoData.CoordinateSystem = wkText;

        // The same point twice. The drawing is in the system just named, so the point on the ground and
        // the point in the drawing are the same numbers, and the transformation is an identity.
        geoData.DesignPoint = new Point3d(anchorX, anchorY, 0.0);
        geoData.ReferencePoint = new Point3d(anchorX, anchorY, 0.0);

        geoData.PostToDb();
        geoData.UpdateTransformationMatrix();

        return "Geographic location set to " + SpatialReferenceNames.Describe(outWkid) + ".";
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
