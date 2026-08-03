using System.Windows;
using System.Windows.Controls;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Opening and closing the sections down the left of page 2.
///
/// The mode readout and the map layers each own a grid row. An open section should take the free space
/// in the pane, and two open sections should share it, which is what a starred row does. A closed one
/// should be its header and nothing else, which is what an Auto row does. Neither height suits both
/// states, so the row is switched as the section opens and closes.
///
/// Done here rather than with a style trigger on the row. A row height set directly outranks one that
/// comes from a style, so a single splitter drag -- or anything else that assigns a height -- would
/// leave the trigger unable to close the section again. Assigning it from here is subject to no such
/// ordering.
/// </summary>
public partial class ExporterWindow
{
    /// <summary>Height a section keeps for itself while open, so it cannot be squeezed to nothing.</summary>
    private const double ModeReadoutOpenMinHeight = 60;
    private const double MapLayersOpenMinHeight = 80;

    private void LeftPaneSection_ExpansionChanged(object sender, RoutedEventArgs e) => ApplyLeftPaneSectionHeights();

    /// <summary>
    /// Puts each section's row into the shape its current state calls for. Safe to call before the
    /// window has finished loading: the named parts are simply not there yet, and the XAML already
    /// starts both rows in the open shape both sections start in.
    /// </summary>
    private void ApplyLeftPaneSectionHeights()
    {
        ApplySectionRowHeight(ModeReadoutRow, ModeReadoutExpander, ModeReadoutOpenMinHeight);
        ApplySectionRowHeight(MapLayersRow, MapLayersExpander, MapLayersOpenMinHeight);
    }

    private static void ApplySectionRowHeight(RowDefinition? row, Expander? section, double openMinHeight)
    {
        if (row == null || section == null) { return; }

        if (section.IsExpanded)
        {
            row.Height = new GridLength(1, GridUnitType.Star);
            row.MinHeight = openMinHeight;
            return;
        }

        // The minimum goes with it. Left behind, it would hold a closed section open by most of the
        // height it was supposed to give back, which is the opposite of closing it.
        row.Height = GridLength.Auto;
        row.MinHeight = 0;
    }
}
