using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Opening, closing and resizing the four sections down the left of page 2.
///
/// Every section is sized to what is in it. None of them takes a share of the free space, which is what
/// used to put a gap between one section and the next: a section given a share it had no content to
/// fill left the rest of that share empty, and the section below it began after the gap rather than
/// after the contents. Sized to their contents they butt against one another, and whatever is left over
/// is left over at the bottom, below all four, where it is not between anything.
///
/// Nothing can be pushed off the pane either. An open section is capped at its share of the height the
/// pane actually has, so four open sections are four visible sections, each scrolling inside its own cap
/// rather than growing until the ones below it are off the bottom. The cap is a ceiling and not a
/// height: a section shorter than its share still ends where its contents end, so the guarantee costs
/// none of the closeness.
///
/// Between each pair is a drag bar. Splitters and collapsing sections do not get on: a splitter writes
/// a pixel height straight onto the row, and a height written directly outranks one that comes from a
/// style, so a section that had been dragged could no longer close itself. That is why the bars were
/// taken out once before. They are back because the heights are wanted, and the argument is settled
/// rather than avoided: the collapse writes the row height from here, so it always wins over a drag,
/// and the dragged height is remembered and handed back when the section opens again.
///
/// A bar is hidden unless both of the sections it sits between are open. Dragging against a closed
/// section can only make a gap where its contents are not.
/// </summary>
public partial class ExporterWindow
{
    /// <summary>
    /// The least a section is capped at, however many are open. Below this a section is a header and a
    /// sliver, which is worse than the pane running short.
    /// </summary>
    private const double LeftPaneSectionMinShare = 90;

    /// <summary>Roughly what a closed section takes: its header and the margin above it.</summary>
    private const double LeftPaneClosedSectionHeight = 30;

    /// <summary>The height of one drag bar, matching the XAML, so the shares add up to what is there.</summary>
    private const double LeftPaneSplitterHeight = 7;

    /// <summary>
    /// What each row was last set to while its section was open, so a drag survives the section being
    /// closed and opened again rather than snapping back.
    /// </summary>
    private readonly Dictionary<RowDefinition, GridLength> _leftPaneSectionHeights = new();

    private void LeftPaneSection_ExpansionChanged(object sender, RoutedEventArgs e) => ApplyLeftPaneSectionHeights();

    /// <summary>The pane changed size, so the shares are worked out again against the height it has now.</summary>
    private void LeftPaneSections_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLeftPaneSectionHeights();

    /// <summary>
    /// Puts each section's row into the shape its current state calls for, caps the open ones so all
    /// four stay on the pane, and shows or hides the drag bars to match. Safe to call before the window
    /// has finished loading: the named parts are simply not there yet.
    /// </summary>
    private void ApplyLeftPaneSectionHeights()
    {
        ApplySectionRowHeight(ModeReadoutRow, ModeReadoutExpander);
        ApplySectionRowHeight(MapLayersRow, MapLayersExpander);
        ApplySectionRowHeight(DataSourcesRow, DataSourcesExpander);
        ApplySectionRowHeight(StripMapRow, StripMapExpander);

        CapLeftPaneSectionsToPane();

        ShowSplitterBetween(ModeReadoutSplitter, ModeReadoutExpander, MapLayersExpander);
        ShowSplitterBetween(MapLayersSplitter, MapLayersExpander, DataSourcesExpander);
        ShowSplitterBetween(DataSourcesSplitter, DataSourcesExpander, StripMapExpander);
    }

    private void ApplySectionRowHeight(RowDefinition? row, Expander? section)
    {
        if (row == null || section == null) { return; }

        if (section.IsExpanded)
        {
            // Auto, not a share. A dragged height beats it, because the user has said what they want
            // this one to be.
            row.Height = _leftPaneSectionHeights.TryGetValue(row, out var dragged) ? dragged : GridLength.Auto;
            row.MinHeight = 0;
            return;
        }

        // Remembered before it is given up, and only when it was a real height: an Auto height read back
        // here would be the closed state remembering itself.
        if (row.Height.GridUnitType != GridUnitType.Auto) { _leftPaneSectionHeights[row] = row.Height; }

        row.Height = GridLength.Auto;
        row.MinHeight = 0;
    }

    /// <summary>
    /// Caps each open section so the whole set fits the pane, and so the last of them ends at the
    /// bottom rather than below it.
    ///
    /// Everything is measured rather than assumed. The mode buttons above the sections, the headers of
    /// the closed ones and the bars between the open ones are all read from what is on screen; the
    /// arithmetic was estimating them before, and it estimated the mode buttons at nothing, which is
    /// most of why a section could end up pushed off the bottom in the first place.
    ///
    /// Two passes, because an even share is not the right answer when some sections do not want theirs.
    /// A section content shorter than its share keeps its own height and gives the rest back, and what
    /// is given back is shared out again among the ones that were capped. That is what stops a short
    /// section from being handed a third of the pane it cannot fill while a long one is cut off.
    /// </summary>
    private void CapLeftPaneSectionsToPane()
    {
        if (LeftPaneSections == null) { return; }

        var sections = new[] { ModeReadoutExpander, MapLayersExpander, DataSourcesExpander, StripMapExpander };
        var splitters = new[] { ModeReadoutSplitter, MapLayersSplitter, DataSourcesSplitter };

        var openSections = new List<Expander>();
        var taken = LeftPaneModeHeaders?.ActualHeight ?? 0;

        foreach (var section in sections)
        {
            if (section == null || section.Visibility != Visibility.Visible) { continue; }

            if (section.IsExpanded) { openSections.Add(section); }
            else { taken += MeasuredClosedHeight(section); }
        }

        foreach (var splitter in splitters)
        {
            if (splitter is { Visibility: Visibility.Visible }) { taken += LeftPaneSplitterHeight; }
        }

        if (openSections.Count == 0) { return; }

        var available = LeftPaneSections.ActualHeight - taken;
        if (available <= 0) { return; }

        // First pass: an even share, never below the floor, so no section is reduced to a header and a
        // sliver however many are open.
        var share = Math.Max(LeftPaneSectionMinShare, available / openSections.Count);

        // Second pass: hand back what the short ones do not want. Their own height stands in for what
        // they want, which is what they are drawn at now, and this runs again on the next layout pass,
        // so it settles rather than having to be right first time.
        var spare = 0.0;
        var wanting = 0;
        foreach (var section in openSections)
        {
            var height = section.ActualHeight;
            if (height > 0 && height < share - 1) { spare += share - height; }
            else { wanting++; }
        }

        var generous = wanting > 0 ? share + (spare / wanting) : share;

        foreach (var section in sections)
        {
            if (section == null) { continue; }

            var cap = !section.IsExpanded
                ? double.PositiveInfinity
                : section.ActualHeight > 0 && section.ActualHeight < share - 1 ? share : generous;

            // Only when it has actually moved. Writing the same number back would be another layout
            // pass, which would arrive here again, which is a loop rather than a layout.
            if (double.IsInfinity(cap) && double.IsInfinity(section.MaxHeight)) { continue; }
            if (Math.Abs(section.MaxHeight - cap) < 0.5) { continue; }

            section.MaxHeight = cap;
        }
    }

    /// <summary>
    /// What a closed section takes up. Its own height once it has been drawn, and the estimate only
    /// until then, because a section that has never been laid out reports nothing.
    /// </summary>
    private static double MeasuredClosedHeight(Expander section) =>
        section.ActualHeight > 1 ? section.ActualHeight : LeftPaneClosedSectionHeight;

    private static void ShowSplitterBetween(GridSplitter? splitter, Expander? above, Expander? below)
    {
        if (splitter == null) { return; }

        // Visible as well as open. The strip map index is hidden outright for the visible map viewport
        // method, and a bar for dragging against a section that is not on the page is worse than none.
        var draggable = above is { IsExpanded: true, Visibility: Visibility.Visible }
                        && below is { IsExpanded: true, Visibility: Visibility.Visible };
        splitter.Visibility = draggable ? Visibility.Visible : Visibility.Collapsed;
    }
}
