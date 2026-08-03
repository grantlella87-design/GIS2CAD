using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Answers one question about the work order chosen on page 1 -- does the proposed main already exist in
/// GIS -- and picks the export method that fits the answer.
///
/// The two methods are the same question asked twice. Work Order driven export reads a route GIS already
/// holds; manual proposed pipeline segments is for drawing one it does not. The user could only tell
/// those apart by choosing a method, going to page 2, and seeing whether anything arrived, so the choice
/// was being made before the fact that decides it was known. Asking the layer at selection time moves
/// that fact to where the choice is.
/// </summary>
public partial class ExporterWindow
{
    private CancellationTokenSource? _proposedMainAvailabilityCts;

    /// <summary>
    /// The last work order this resolved, and what it found. Keyed so that leaving the combo -- which
    /// happens on every focus change, not only on a new choice -- re-reports from memory instead of
    /// asking the service the same question again.
    /// </summary>
    private string? _proposedMainAvailabilityWorkOrderId;
    private bool _proposedMainAvailabilityResolved;
    private bool _proposedMainAvailabilityInFlight;
    private int _proposedMainAvailabilityCount;

    /// <summary>
    /// Set while this file is the one ticking a radio, so the resulting Checked is not mistaken for the
    /// user overriding the choice it just made.
    /// </summary>
    private bool _applyingProposedMainAvailabilityMethod;

    private static readonly Brush ProposedMainFoundBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
    private static readonly Brush ProposedMainMissingBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x00));
    private static readonly Brush ProposedMainUnknownBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    /// <summary>
    /// Looks the work order up in the proposed main layer and sets the export method to match.
    ///
    /// Only a definite answer moves the radio. A missing sign-in or a service that will not answer says
    /// nothing about whether the route exists, and switching on one of those would send the user off to
    /// draw a main by hand over a route GIS already has -- work that is wasted and then conflicts. In
    /// that case the method is left where it was and the display says why, which is recoverable.
    /// </summary>
    private async Task CrossReferenceProposedMainForWorkOrderAsync(string workOrderId, bool force)
    {
        workOrderId = workOrderId?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(workOrderId))
        {
            _proposedMainAvailabilityCts?.Cancel();
            ForgetProposedMainAvailability();
            SetProposedMainAvailability("Choose a work order and its proposed main will be looked up in GIS.", ProposedMainUnknownBrush);
            return;
        }

        // This work order is either answered already or being answered right now, so re-asking would put
        // a second query on the wire for a question already in hand. Leaving the combo raises LostFocus
        // whether or not anything changed, so without this a few focus changes become a few queries.
        //
        // In flight counts as well as resolved: selecting a work order and then tabbing out of the box is
        // two events about one choice, and the second used to cancel the first and start it again.
        if (!force
            && (_proposedMainAvailabilityResolved || _proposedMainAvailabilityInFlight)
            && string.Equals(_proposedMainAvailabilityWorkOrderId, workOrderId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _proposedMainAvailabilityCts?.Cancel();
        var cts = new CancellationTokenSource();
        _proposedMainAvailabilityCts = cts;
        var token = cts.Token;

        _proposedMainAvailabilityWorkOrderId = workOrderId;
        _proposedMainAvailabilityResolved = false;
        _proposedMainAvailabilityInFlight = true;

        var accessToken = GetCurrentArcGisAccessTokenForProposedMain();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            // Forgotten rather than remembered as an answer, so signing in and coming back re-asks. A
            // work order held here with nothing resolved against it would make every later attempt take
            // the cache path above and never reach the service again.
            ForgetProposedMainAvailability();
            SetProposedMainAvailability(
                "Cannot check GIS for a proposed main on work order " + workOrderId
                + ": no ArcGIS sign-in. Run NGGIS sign-in, then pick the work order again. "
                + "The export method has been left as it is.",
                ProposedMainUnknownBrush);
            return;
        }

        SetProposedMainAvailability("Checking GIS for a proposed main on work order " + workOrderId + "...", ProposedMainUnknownBrush);

        try
        {
            var count = await ProposedMainFeatureService.CountByWorkOrderAsync(workOrderId, accessToken, token);

            // A later selection has already started its own lookup, so this answer is about a work order
            // the user has moved on from. Applying it would set the method from the wrong one.
            if (token.IsCancellationRequested) { return; }

            _proposedMainAvailabilityResolved = true;
            _proposedMainAvailabilityInFlight = false;
            _proposedMainAvailabilityCount = count;

            if (count > 0)
            {
                SelectWorkOrderDrivenExportForAvailability();
                SetProposedMainAvailability(
                    "Proposed main found in GIS for work order " + workOrderId + ": "
                    + count.ToString("N0") + (count == 1 ? " segment." : " segments.")
                    + " Work Order driven export selected, so page 2 will bring that route in.",
                    ProposedMainFoundBrush);
            }
            else
            {
                SelectManualProposedPipelineForAvailability();
                SetProposedMainAvailability(
                    "No proposed main in GIS for work order " + workOrderId + ". "
                    + "Manual proposed pipeline segments selected, so the route can be drawn on page 2.",
                    ProposedMainMissingBrush);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection, which owns the state now. Clearing the in-flight flag here
            // would clear the replacement's.
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) { return; }

            // Nothing was learned, so nothing is remembered. A failure held as though it were an answer
            // would make the cache path swallow every retry, and the user would be stuck with a stale
            // message and no way to ask again short of restarting.
            ForgetProposedMainAvailability();
            SetProposedMainAvailability(
                "Could not check GIS for a proposed main on work order " + workOrderId + ": "
                + FlattenProposedMainException(ex)
                + " The export method has been left as it is, since a failed check does not mean the "
                + "route is missing.",
                ProposedMainUnknownBrush);
        }
    }

    /// <summary>
    /// Drops what is held about the current work order, so the next attempt asks the service again
    /// rather than taking the cache path. For outcomes that are not answers: no sign-in, or a failure.
    /// </summary>
    private void ForgetProposedMainAvailability()
    {
        _proposedMainAvailabilityWorkOrderId = null;
        _proposedMainAvailabilityResolved = false;
        _proposedMainAvailabilityInFlight = false;
        _proposedMainAvailabilityCount = 0;
    }

    /// <summary>
    /// Ticks a radio without the cross-reference running again from the method change. Checking a radio
    /// raises Checked, which republishes the method and resets page 2 -- all wanted -- but the work order
    /// has not changed, so re-asking the layer would be a second query for the same answer.
    /// </summary>
    private void SelectWorkOrderDrivenExportForAvailability()
    {
        if (WorkOrderDrivenExportRadio == null || WorkOrderDrivenExportRadio.IsChecked == true) { return; }
        _applyingProposedMainAvailabilityMethod = true;
        try { WorkOrderDrivenExportRadio.IsChecked = true; }
        finally { _applyingProposedMainAvailabilityMethod = false; }
    }

    private void SelectManualProposedPipelineForAvailability()
    {
        if (ManualProposedPipelineRadio == null || ManualProposedPipelineRadio.IsChecked == true) { return; }
        _applyingProposedMainAvailabilityMethod = true;
        try { ManualProposedPipelineRadio.IsChecked = true; }
        finally { _applyingProposedMainAvailabilityMethod = false; }
    }

    /// <summary>
    /// Says something when the method the user has just picked disagrees with what GIS holds.
    ///
    /// The choice is theirs and is not undone -- there are good reasons to draw over an existing route,
    /// or to export a view that has nothing to do with the work order. What is not wanted is finding out
    /// on page 2, from an empty map, that the route was never going to arrive. This is the only place
    /// that fact is already known, so it is the place to say it.
    /// </summary>
    private void ReportMethodAgainstProposedMainAvailability()
    {
        if (_applyingProposedMainAvailabilityMethod) { return; }
        if (!_proposedMainAvailabilityResolved || string.IsNullOrWhiteSpace(_proposedMainAvailabilityWorkOrderId)) { return; }

        var workOrderId = _proposedMainAvailabilityWorkOrderId;
        var exists = _proposedMainAvailabilityCount > 0;

        if (_displayMode == ExportDisplayMode.WorkOrder && !exists)
        {
            SetProposedMainAvailability(
                "Work Order driven export is selected, but GIS holds no proposed main for work order "
                + workOrderId + ", so page 2 will have no route to bring in. Manual proposed pipeline "
                + "segments is the one that lets you draw it.",
                ProposedMainMissingBrush);
            return;
        }

        if (_displayMode == ExportDisplayMode.ManualProposedPipeline && exists)
        {
            SetProposedMainAvailability(
                "Manual proposed pipeline segments is selected, so the route will be drawn by hand even "
                + "though GIS already holds " + _proposedMainAvailabilityCount.ToString("N0")
                + (_proposedMainAvailabilityCount == 1 ? " segment for work order " : " segments for work order ")
                + workOrderId + ". Work Order driven export would bring that one in instead.",
                ProposedMainMissingBrush);
            return;
        }

        if (_displayMode == ExportDisplayMode.VisibleMap)
        {
            SetProposedMainAvailability(
                "Visible map viewport export is selected, so what is on screen decides the extent and the "
                + "work order is not consulted. GIS "
                + (exists
                    ? "does hold a proposed main for work order " + workOrderId + ", which this method will not bring in."
                    : "holds no proposed main for work order " + workOrderId + "."),
                ProposedMainUnknownBrush);
            return;
        }

        // Method and GIS agree. Restate the agreement rather than leaving whatever was said last, which
        // may have been a warning about the method they have just moved away from.
        SetProposedMainAvailability(
            exists
                ? "Proposed main found in GIS for work order " + workOrderId + ": "
                  + _proposedMainAvailabilityCount.ToString("N0")
                  + (_proposedMainAvailabilityCount == 1 ? " segment." : " segments.")
                  + " Work Order driven export will bring that route in on page 2."
                : "No proposed main in GIS for work order " + workOrderId
                  + ". Manual proposed pipeline segments lets the route be drawn on page 2.",
            exists ? ProposedMainFoundBrush : ProposedMainMissingBrush);
    }

    private void SetProposedMainAvailability(string message, Brush foreground)
    {
        if (ProposedMainAvailabilityText == null) { return; }
        ProposedMainAvailabilityText.Text = message;
        ProposedMainAvailabilityText.Foreground = foreground;
    }
}
