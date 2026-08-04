using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            var schema = await ProposedMainFeatureService.GetLayerSchemaAsync(token);
            if (DataContext is ExporterViewModel vm)
            {
                vm.LoadProposedMainSchema(schema);
                vm.SyncProposedMainSegmentRows(_manualProposedPipelineSegmentGeometries.Count);
            }
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

        // Rows and segments are kept level here as well as when the drawing changes, because this is
        // the last moment before the two have to agree and a mismatch would send the wrong attributes
        // with the wrong pipe.
        vm.SyncProposedMainSegmentRows(segments.Count);

        var missing = vm.MissingRequiredProposedMainAttributes;
        if (missing.Count > 0)
        {
            vm.ProposedMainAttributeStatus = "Fill in the attribute table before going on: "
                + string.Join("; ", missing) + ".";
            vm.Status = "The proposed main attribute table is incomplete: " + string.Join("; ", missing) + ".";
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

    /// <summary>
    /// The height the table was last given, so closing and opening it again does not throw away a
    /// height the user dragged to. Null until it has been open once.
    /// </summary>
    private GridLength? _proposedMainAttributeRowHeight;

    /// <summary>
    /// Gives the table's row a share of the pane while it is open and none of it while it is closed.
    ///
    /// A share rather than its content's height, because that is what makes the splitter above it mean
    /// anything: two rows that both want a share can have the boundary between them moved, where a row
    /// sized to its content cannot be resized at all.
    ///
    /// Closed, it goes back to Auto. Left holding a share, a closed table would keep a third of the
    /// pane to show a header in, which is the opposite of what closing it is for.
    /// </summary>
    private void ProposedMainAttributeSection_ExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (ProposedMainAttributeRow == null || ProposedMainAttributeSection == null) { return; }

        if (ProposedMainAttributeSection.IsExpanded)
        {
            // Whatever it was last time, so a drag survives the table being closed and opened again.
            ProposedMainAttributeRow.Height = _proposedMainAttributeRowHeight ?? new GridLength(1, GridUnitType.Star);
            ProposedMainAttributeRow.MinHeight = 120;

            // Nothing dragged yet, so it opens at the height its rows want rather than at a share.
            if (_proposedMainAttributeRowHeight == null) { ResizeProposedMainAttributeTableToRows(); }
            return;
        }

        // Remembered before it is given up, and only when it was a real share: an Auto height read
        // back here would be the closed state remembering itself.
        if (ProposedMainAttributeRow.Height.GridUnitType != GridUnitType.Auto)
        {
            _proposedMainAttributeRowHeight = ProposedMainAttributeRow.Height;
        }

        ProposedMainAttributeRow.Height = GridLength.Auto;
        ProposedMainAttributeRow.MinHeight = 0;
    }

    /// <summary>
    /// Whether any subtype, or the field itself, gives this field a list to choose from.
    ///
    /// Asked across every subtype rather than only the one currently selected, because the column is
    /// built once and has to be a dropdown for the row that will need it. A column that started as a
    /// text box would stay one after the subtype was chosen.
    /// </summary>
    private static bool FieldCanOfferChoices(ProposedMainLayerSchema schema, ProposedMainField field)
    {
        if (field.HasCodedValues) { return true; }

        if (schema.HasSubtypes
            && string.Equals(field.Name, schema.SubtypeFieldName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return schema.Subtypes.Any(s => s.Domains.TryGetValue(field.Name, out var values) && values.Count > 0);
    }

    /// <summary>
    /// Picks out the segment whose row was selected, so a row in the table and a line on the map can be
    /// told to be the same thing. Selecting nothing clears the highlight rather than leaving the last
    /// one lit, which would be pointing at a row that is no longer chosen.
    /// </summary>
    private void ProposedMainAttributeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        var selected = ProposedMainAttributeGrid?.SelectedItem as ProposedMainSegmentAttributesViewModel;
        HighlightManualProposedPipelineSegment(selected == null ? -1 : vm.ProposedMainSegmentRows.IndexOf(selected));
    }

    /// <summary>
    /// Sizes the table to the rows it has, up to a point.
    ///
    /// Opened at a fixed height it was wrong in both directions: two segments left most of it empty
    /// while the map went short, and a dozen showed four with the rest behind a scrollbar. The height
    /// is worked out from the rows instead, so the usual case of a few segments needs no dragging.
    ///
    /// Capped, because the map is the reason the page exists. Past the cap the grid scrolls, which is
    /// what the vertical scrollbar is for, and the splitter is still there for anyone who wants to see
    /// more rows than the cap allows.
    /// </summary>
    private void ResizeProposedMainAttributeTableToRows()
    {
        if (ProposedMainAttributeRow == null || ProposedMainAttributeSection == null) { return; }
        if (!ProposedMainAttributeSection.IsExpanded) { return; }
        if (DataContext is not ExporterViewModel vm) { return; }

        var rows = vm.ProposedMainSegmentRows.Count;
        if (rows == 0) { return; }

        // Header, the rows themselves, and the status line and tick box that sit with them. Rounded up
        // rather than measured, because a table that is a few pixels short of its last row is the
        // thing this is meant to avoid.
        var height = ProposedMainTableChrome + ProposedMainTableHeaderHeight + (rows * ProposedMainTableRowHeight);
        var capped = Math.Min(height, ProposedMainTableMaxHeight);

        ProposedMainAttributeRow.Height = new GridLength(capped);
        _proposedMainAttributeRowHeight = ProposedMainAttributeRow.Height;
    }

    /// <summary>Room for the status line, the upload tick box, the expander header and the margins.</summary>
    private const double ProposedMainTableChrome = 96;
    private const double ProposedMainTableHeaderHeight = 30;
    private const double ProposedMainTableRowHeight = 26;

    /// <summary>
    /// As tall as the table is allowed to open on its own. Past this it scrolls: the map is the reason
    /// the page exists and a table opening over most of it would be the tail wagging the dog.
    /// </summary>
    private const double ProposedMainTableMaxHeight = 320;

    /// <summary>
    /// Brings the attribute table in line with what is drawn. Called wherever the segment list changes,
    /// so a row appears with the segment rather than when something else happens to refresh the page.
    /// </summary>
    private void SyncProposedMainAttributeRows()
    {
        if (DataContext is ExporterViewModel vm)
        {
            vm.SyncProposedMainSegmentRows(_manualProposedPipelineSegmentGeometries.Count);
        }

        ResizeProposedMainAttributeTableToRows();
    }

    /// <summary>
    /// Builds the table's columns from the layer's fields.
    ///
    /// In code because the columns are not known until the service has answered: which fields exist is
    /// GIS's to decide, so they cannot be written into the XAML without being a copy that goes stale.
    /// Each cell binds through the row's indexer, which is what lets a column exist for a field this
    /// code has never heard of.
    /// </summary>
    private void RebuildProposedMainAttributeColumns()
    {
        if (ProposedMainAttributeGrid == null) { return; }
        if (DataContext is not ExporterViewModel vm) { return; }

        ProposedMainAttributeGrid.Columns.Clear();

        // Which segment, and what it still needs. Both read only: they describe the row rather than
        // being part of it, and the second is the one that says why the page will not move on.
        ProposedMainAttributeGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Segment",
            Binding = new Binding(nameof(ProposedMainSegmentAttributesViewModel.SegmentDisplay)),
            IsReadOnly = true,
            Width = 90
        });

        var schema = vm.ProposedMainSchema;

        foreach (var field in schema.Fields)
        {
            // A field name with brackets or a dot in it would be read as part of the binding path
            // rather than as a name. None of them should have one, and a column bound to nonsense is
            // worse than a column that is not there.
            if (field.Name.IndexOfAny(new[] { '[', ']', '.', '/' }) >= 0) { continue; }

            var path = "[" + field.Name + "]";
            var header = field.Required ? field.Display + " *" : field.Display;

            // A dropdown wherever anything could ever fill it: the field's own domain, a subtype's
            // narrower one, or the list of subtypes for the field that chooses between them. Asking
            // the field alone was the mistake -- on a layer with subtypes most fields carry no domain
            // of their own and are constrained per subtype instead, so almost everything came out as
            // a free text box and the values GIS would accept were nowhere on screen.
            if (FieldCanOfferChoices(schema, field))
            {
                var column = new DataGridComboBoxColumn
                {
                    Header = header,
                    SelectedValuePath = nameof(ProposedMainCodedValue.Code),
                    DisplayMemberPath = nameof(ProposedMainCodedValue.Name),
                    SelectedValueBinding = new Binding(path) { Mode = BindingMode.TwoWay },
                    MinWidth = 140
                };

                // The list comes from the row rather than the column, because two rows of different
                // subtypes are allowed different values in the same column. A style setter is how a
                // per row ItemsSource is reached on this kind of column.
                var choices = new Binding("Choices[" + field.Name + "]");
                var style = new Style(typeof(ComboBox));
                style.Setters.Add(new Setter(ItemsControl.ItemsSourceProperty, choices));
                column.ElementStyle = style;
                column.EditingElementStyle = style;

                ProposedMainAttributeGrid.Columns.Add(column);
                continue;
            }

            ProposedMainAttributeGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                MinWidth = 120
            });
        }

        ProposedMainAttributeGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Status",
            Binding = new Binding(nameof(ProposedMainSegmentAttributesViewModel.MissingDisplay)),
            IsReadOnly = true,
            MinWidth = 160
        });
    }
}
