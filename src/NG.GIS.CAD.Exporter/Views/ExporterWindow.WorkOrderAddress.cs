using System;
using System.Threading.Tasks;
using NG.GIS.CAD.Exporter.Services;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Putting the map where the work order is when GIS has no proposed main to put it there.
///
/// A job with no route yet is the case the manual drawing exists for, and until now it left the map
/// wherever it happened to be: eastern Massachusetts, or the last work order looked at. Drawing a main
/// then began with finding the street, which the work order already knows.
/// </summary>
public partial class ExporterWindow
{
    /// <summary>The work order the map was last moved to, so it is not moved again for the same one.</summary>
    private string? _workOrderAddressZoomedTo;

    /// <summary>
    /// Moves the map to the work order's service address.
    ///
    /// The one line address the query builds is what the map is placed by and what goes in the Address
    /// box, so the two always agree. Placing by stored coordinates while showing an address built from
    /// other columns could send the map somewhere the box did not name, and there would be nothing on
    /// screen to say which of the two was wrong.
    ///
    /// Quiet when there is nothing to go on. A work order with no address is not an error worth
    /// interrupting the drawing for; the map simply stays where it was, which is where it would have
    /// been anyway.
    /// </summary>
    private async Task ZoomToWorkOrderAddressAsync(string workOrderNumber, string reason)
    {
        if (string.IsNullOrWhiteSpace(workOrderNumber)) { return; }
        if (_mapView == null) { return; }

        // Once per work order. This runs whenever the page finds no proposed main, which happens on
        // every visit to page 2, and moving the map out from under someone who has panned away from
        // it would undo their own work each time they came back.
        if (string.Equals(_workOrderAddressZoomedTo, workOrderNumber, StringComparison.OrdinalIgnoreCase)) { return; }
        _workOrderAddressZoomedTo = workOrderNumber;

        try
        {
            var address = await NgOdsWorkOrderLookup.LoadAddressAsync(workOrderNumber);
            if (address == null || !address.HasAnything)
            {
                SetNgOdsStatus(reason + " NG_ODS has no service address for work order " + workOrderNumber
                    + " either, so the map was left where it is.");

                // Forgotten, so a later attempt tries again rather than being refused by the guard
                // above on the strength of a lookup that found nothing.
                _workOrderAddressZoomedTo = null;
                return;
            }

            await GeocodeToWorkOrderAddressAsync(address, reason, workOrderNumber);
        }
        catch (Exception ex)
        {
            SetNgOdsStatus(reason + " The work order's service address could not be read: "
                + NgOdsConnection.SummarizeError(ex.Message) + " The map was left where it is.");
            _workOrderAddressZoomedTo = null;
        }
    }

    /// <summary>
    /// One inch to eighty feet, near enough. Close enough to see which street the job is on and to
    /// start drawing on it, without being so close that a wrong address looks like the right one.
    /// </summary>
    private const double WorkOrderAddressScale = 960;

    /// <summary>
    /// Finds the address on the map, using the same geocoder the address box on this page uses. The
    /// same route the user's own typed address takes, so a job placed automatically lands where it
    /// would have landed had they typed the address in themselves.
    /// </summary>
    private async Task GeocodeToWorkOrderAddressAsync(NgOdsWorkOrderAddress address, string reason, string workOrderNumber)
    {
        if (_locatorTask == null || string.IsNullOrWhiteSpace(address.SearchText))
        {
            SetNgOdsStatus(reason + " The work order's address could not be placed on the map, so it was "
                + "left where it is.");
            _workOrderAddressZoomedTo = null;
            return;
        }

        var results = await _locatorTask.GeocodeAsync(address.SearchText);
        if (results == null || results.Count == 0 || results[0].DisplayLocation == null)
        {
            SetNgOdsStatus(reason + " \"" + address.SearchText + "\" is the address on work order "
                + workOrderNumber + ", and the geocoder did not recognise it, so the map was left where it is.");
            _workOrderAddressZoomedTo = null;
            return;
        }

        await _mapView!.SetViewpointCenterAsync(results[0].DisplayLocation!, WorkOrderAddressScale);
        ShowWorkOrderAddressAboveMap(address.SearchText);
        SetNgOdsStatus(reason + " The map has been moved to the work order's service address: "
            + address.SearchText + ".");
    }

    /// <summary>
    /// Puts the address the map was moved to into the Address box above it.
    ///
    /// The map jumping to a street on its own is disconcerting without something saying which street it
    /// went to. The status line says so as well, but that scrolls past; the box stays, and it is the
    /// same box the address would have been typed into to get there by hand.
    ///
    /// It is also then live: Go re-centres on it, so having been moved somewhere the user can get back
    /// to it after panning away without having to type it out.
    /// </summary>
    private void ShowWorkOrderAddressAboveMap(string? address)
    {
        if (AddressSearchTextBox == null || string.IsNullOrWhiteSpace(address)) { return; }

        AddressSearchTextBox.Text = address;
    }

    /// <summary>Lets the map be moved again, for a work order whose main has since been looked up afresh.</summary>
    private void ForgetWorkOrderAddressZoom() => _workOrderAddressZoomedTo = null;
}
