using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Opening, closing and resizing the four sections down the left of page 2.
///
/// Each owns a grid row. An open section should take the free space in the pane, and several open
/// sections should share it, which is what a starred row does. A closed one should be its header and
/// nothing else, which is what an Auto row does. Neither height suits both states, so the row is
/// switched as the section opens and closes.
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
    /// <summary>Height a section keeps for itself while open, so it cannot be squeezed to nothing.</summary>
    private const double ModeReadoutOpenMinHeight = 60;
    private const double MapLayersOpenMinHeight = 80;
    private const double DataSourcesOpenMinHeight = 80;
    private const double StripMapOpenMinHeight = 80;

    /// <summary>
    /// What each row was last set to while its section was open, so a drag survives the section being
    /// closed and opened again rather than snapping back to an even share.
    /// </summary>
    private readonly Dictionary<RowDefinition, GridLength> _leftPaneSectionHeights = new();

    private void LeftPaneSection_ExpansionChanged(object sender, RoutedEventArgs e) => ApplyLeftPaneSectionHeights();

    /// <summary>
    /// Puts each section's row into the shape its current state calls for, and shows or hides the drag
    /// bars to match. Safe to call before the window has finished loading: the named parts are simply
    /// not there yet, and the XAML already starts each row in the shape its section starts in.
    /// </summary>
    private void ApplyLeftPaneSectionHeights()
    {
        ApplySectionRowHeight(ModeReadoutRow, ModeReadoutExpander, ModeReadoutOpenMinHeight);
        ApplySectionRowHeight(MapLayersRow, MapLayersExpander, MapLayersOpenMinHeight);
        ApplySectionRowHeight(DataSourcesRow, DataSourcesExpander, DataSourcesOpenMinHeight);
        ApplySectionRowHeight(StripMapRow, StripMapExpander, StripMapOpenMinHeight);

        ShowSplitterBetween(ModeReadoutSplitter, ModeReadoutExpander, MapLayersExpander);
        ShowSplitterBetween(MapLayersSplitter, MapLayersExpander, DataSourcesExpander);
        ShowSplitterBetween(DataSourcesSplitter, DataSourcesExpander, StripMapExpander);
    }

    private void ApplySectionRowHeight(RowDefinition? row, Expander? section, double openMinHeight)
    {
        if (row == null || section == null) { return; }

        if (section.IsExpanded)
        {
            row.Height = _leftPaneSectionHeights.TryGetValue(row, out var dragged)
                ? dragged
                : new GridLength(1, GridUnitType.Star);
            row.MinHeight = openMinHeight;
            return;
        }

        // Remembered before it is given up, and only when it was a real height: an Auto height read back
        // here would be the closed state remembering itself.
        if (row.Height.GridUnitType != GridUnitType.Auto) { _leftPaneSectionHeights[row] = row.Height; }

        // The minimum goes with it. Left behind, it would hold a closed section open by most of the
        // height it was supposed to give back, which is the opposite of closing it.
        row.Height = GridLength.Auto;
        row.MinHeight = 0;
    }

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
