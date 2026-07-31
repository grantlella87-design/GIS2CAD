using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CoreApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>
/// Turns AutoCAD's geographic map on in the open drawing, so the features that were just exported can
/// be seen against the ground rather than on blank paper.
///
/// This is AutoCAD's own map, the one GEOMAP switches on, and it is nothing to do with what the export
/// fetched: no file is written and the drawing gains no reference. It is a view of the drawing, so it
/// costs nothing to leave on and nothing to turn off.
/// </summary>
public sealed class CadGeoMapService
{
    /// <summary>Aerial. The point is comparing exported features against real ground, not a drawn map.</summary>
    private const short AerialMode = 1;

    /// <summary>
    /// Switches the geographic map on, and says what happened.
    ///
    /// Returns a message rather than throwing or reporting success blindly. The map depends on things
    /// this code does not control -- a geographic location on the drawing, and an Autodesk sign-in for
    /// the imagery -- so the honest outcomes are "on", "cannot be on, and here is why", and nothing
    /// else. Reporting it as on when it is not would send someone looking for a placement error that
    /// the absent map is the whole reason they cannot see.
    /// </summary>
    public string TurnOn()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return "No drawing is open, so the geographic map was not turned on.";
        }

        // GEOMAP does nothing without a geographic location: the map has no way to know where the
        // drawing sits on the earth. Setting one here is deliberately not attempted, because it means
        // choosing a coordinate system on the user's behalf and a wrong choice puts the imagery
        // somewhere plausible and wrong, which is worse than no imagery at all.
        if (!HasGeographicLocation(document))
        {
            return "The geographic map needs a geographic location on the drawing, which this one does "
                   + "not have, so it was left off. Run GEOGRAPHICLOCATION once on the drawing or the "
                   + "template to set one, and the map will come on with every export after that.";
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
                return "Geographic map turned on (aerial).";
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        // Most likely no Autodesk sign-in, which is what the online map service needs. The export
        // itself has already been written by this point, so this is worth reporting and no more.
        return "The geographic map could not be turned on, so the export is there without it: "
               + (failure?.Message ?? "the setting was refused.");
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
            var geoDataId = document.Database.GeoDataObject;
            return !geoDataId.IsNull;
        }
        catch
        {
            // An older drawing, or one where the property is not available, is treated as having none.
            // Being wrong in this direction leaves the map off and says why, which is recoverable.
            return false;
        }
    }
}
