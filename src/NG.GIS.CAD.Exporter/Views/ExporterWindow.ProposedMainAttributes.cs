using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using NG.GIS.CAD.Exporter.Services;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// The parts of a hand drawn proposed main that come from the GIS layer rather than from the drawing:
/// the symbol it is shown in, the attributes it has to carry, and writing it back.
/// </summary>
public partial class ExporterWindow
{
    private ProposedMainRestSymbol? _proposedMainLayerSymbol;
    private bool _proposedMainSchemaLoaded;
    private bool _proposedMainUploaded;

    /// <summary>
    /// Fetches the layer's own symbol and field list once, so a drawn main looks like the layer it is
    /// going into and asks for the attributes that layer wants.
    ///
    /// Quiet about failure. Neither of these stops anyone drawing: without the symbol the segments are
    /// drawn in the fallback colour, and without the fields the table is empty and the upload is what
    /// reports the problem. Interrupting the drawing with a message about a service call the user did
    /// not make would be the wrong moment.
    /// </summary>
    private async Task EnsureProposedMainLayerDetailsAsync()
    {
        if (_proposedMainSchemaLoaded) { return; }

        var token = GetCurrentArcGisAccessTokenForProposedMain();
        if (string.IsNullOrWhiteSpace(token)) { return; }

        _proposedMainSchemaLoaded = true;

        try
        {
            _proposedMainLayerSymbol = await ProposedMainFeatureService.GetLayerSymbolAsync(token);
            RefreshManualProposedPipelineSegmentOverlay();
        }
        catch
        {
            // Drawn in the fallback symbol, which is visible and is not wrong, only generic.
        }

        try
        {
            var fields = await ProposedMainFeatureService.GetEditableFieldsAsync(token);
            if (DataContext is ExporterViewModel vm) { vm.LoadProposedMainAttributes(fields); }
        }
        catch (Exception ex)
        {
            if (DataContext is ExporterViewModel vm)
            {
                vm.ProposedMainAttributeStatus = "The proposed main layer's fields could not be read: "
                    + FlattenProposedMainException(ex)
                    + " The attributes cannot be filled in here until that succeeds.";
            }

            // Not latched, so opening the page again retries rather than leaving an empty table for
            // the rest of the session.
            _proposedMainSchemaLoaded = false;
        }
    }

    /// <summary>
    /// The symbol hand drawn segments are shown in: the layer's own, so what is being added looks like
    /// what it is being added to. Falls back to the built in one until the layer has answered.
    /// </summary>
    private SimpleLineSymbol BuildManualProposedPipelineSymbol()
    {
        var symbol = _proposedMainLayerSymbol;
        if (symbol == null) { return new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.Red, 4.0); }

        return new SimpleLineSymbol(
            ConvertRestLineStyleToRuntimeStyle(symbol.Style),
            System.Drawing.Color.FromArgb(ClampByte(symbol.A), ClampByte(symbol.R), ClampByte(symbol.G), ClampByte(symbol.B)),
            Math.Max(2.0, symbol.Width));
    }

    /// <summary>
    /// Writes the drawn segments to the GIS layer, and says whether page 3 should be allowed.
    ///
    /// Returns false only when the user should stay on page 2: a required attribute is empty, or the
    /// upload was attempted and refused. A drawing with no segments, or a user who has turned the
    /// upload off, is not a failure and passes straight through.
    ///
    /// Uploading once is enough. Coming back to page 2 and going forward again would otherwise add the
    /// same main a second time, which is a duplicate in a live layer that somebody has to go and find.
    /// </summary>
    private async Task<bool> TryUploadManualProposedMainAsync()
    {
        if (DataContext is not ExporterViewModel vm) { return true; }
        if (_displayMode != ExportDisplayMode.ManualProposedPipeline) { return true; }
        if (!vm.UploadProposedMainToGis) { return true; }
        if (_proposedMainUploaded) { return true; }

        var segments = _manualProposedPipelineSegmentGeometries
            .Where(g => g != null && !g.IsEmpty)
            .ToList();
        if (segments.Count == 0) { return true; }

        var missing = vm.MissingRequiredProposedMainAttributes;
        if (missing.Count > 0)
        {
            vm.ProposedMainAttributeStatus = "Fill in " + string.Join(", ", missing)
                + " before going on. GIS will not accept the main without "
                + (missing.Count == 1 ? "it." : "them.");
            vm.Status = "The proposed main is missing " + missing.Count + " required attribute(s): "
                + string.Join(", ", missing) + ".";
            return false;
        }

        var token = GetCurrentArcGisAccessTokenForProposedMain();
        if (string.IsNullOrWhiteSpace(token))
        {
            vm.Status = "The proposed main was not uploaded to GIS: no ArcGIS sign-in. Run NGGIS "
                + "sign-in and come back to this page, or untick the upload to carry on without it.";
            return false;
        }

        try
        {
            vm.Status = "Adding " + segments.Count + " proposed main segment(s) to GIS...";
            var result = await ProposedMainFeatureService.AddFeaturesAsync(
                segments, vm.BuildProposedMainAttributeValues(), token);

            if (!result.Succeeded)
            {
                vm.Status = "GIS refused the proposed main, so nothing was added: "
                    + (result.Error ?? "the service did not say why")
                    + ". The drawing is untouched and the segments are still here.";
                return false;
            }

            // Latched only on success, so a refusal can be corrected and tried again.
            _proposedMainUploaded = true;
            vm.Status = "Added " + result.Added + " proposed main segment(s) to GIS.";
            return true;
        }
        catch (Exception ex)
        {
            vm.Status = "The proposed main could not be added to GIS: " + FlattenProposedMainException(ex)
                + " Nothing was added.";
            return false;
        }
    }

    /// <summary>
    /// Lets the segments be uploaded again, after they have changed enough to be a different main.
    /// Called when the drawing is cleared or the export method changes.
    /// </summary>
    private void ForgetProposedMainUpload() => _proposedMainUploaded = false;
}
