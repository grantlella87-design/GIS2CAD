using System;
using System.Windows;
using NG.GIS.CAD.Exporter.ViewModels;

// System.Windows and Autodesk.AutoCAD.ApplicationServices both define Application, and this file
// needs types from both namespaces, so the AutoCAD one is named explicitly.
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Page 4 helpers: re-reading the CAD symbol lists, and opening a block for editing.
/// </summary>
public partial class ExporterWindow
{
    private void ReloadCadCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExporterViewModel vm) { vm.ReloadCadCatalog(); }
    }

    /// <summary>
    /// Opens AutoCAD's own colour dialog, so the index tab, true colour tab and colour books are the
    /// ones the user already knows, rather than a lesser picker built here.
    ///
    /// The RGB is recorded whichever tab was used, so the panel always has a swatch to show.
    /// </summary>
    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExporterViewModel vm || vm.SelectedTransform == null)
        {
            return;
        }

        try
        {
            var dialog = new Autodesk.AutoCAD.Windows.ColorDialog();
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) { return; }

            var color = dialog.Color;

            // The dialog also offers ByLayer and ByBlock. Treating those as a true colour, which is
            // what anything that is not ByAci used to become, recorded a colour the user did not pick.
            if (color.IsByLayer || color.IsByBlock)
            {
                vm.SelectedTransform.ApplyByLayerColor();
            }
            else
            {
                vm.SelectedTransform.ApplyPickedColor(color.IsByAci, color.ColorIndex, ResolvePickedRgbHex(color));
            }

            vm.Status = "Colour set to " + vm.SelectedTransform.ColorDescription + " for " + vm.SelectedTransform.LayerName + ".";
        }
        catch (Exception ex)
        {
            vm.Status = "Could not open the colour dialog: " + ex.Message;
        }
    }

    /// <summary>
    /// Reads the picked colour back as "#RRGGBB".
    ///
    /// Red, Green and Blue only carry a value for a true colour. For an index they are all zero, the
    /// index being the whole of the colour, which is why an ACI pick used to leave a black swatch.
    /// ColorValue resolves an index through the ACI table, so it answers for both kinds.
    /// </summary>
    private static string ResolvePickedRgbHex(Autodesk.AutoCAD.Colors.Color color)
    {
        try
        {
            var resolved = color.ColorValue;
            return FormatRgbHex(resolved.R, resolved.G, resolved.B);
        }
        catch
        {
            return FormatRgbHex(color.Red, color.Green, color.Blue);
        }
    }

    private static string FormatRgbHex(byte red, byte green, byte blue) =>
        "#" + red.ToString("X2") + green.ToString("X2") + blue.ToString("X2");

    /// <summary>
    /// Opens the selected block in AutoCAD's own block editor rather than reimplementing one. The
    /// window is modeless, so BEDIT runs against the open drawing while this stays on screen.
    ///
    /// A block only exists to edit if it is in the drawing. When the name came from a template it may
    /// not be, which is said plainly instead of failing inside AutoCAD.
    /// </summary>
    private void EditBlock_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        var blockName = vm.SelectedTransform?.BlockName;
        if (string.IsNullOrWhiteSpace(blockName))
        {
            vm.Status = "Choose a block before opening the block editor.";
            return;
        }

        try
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                vm.Status = "No drawing is open, so the block editor cannot be started.";
                return;
            }

            if (!vm.BlockExistsInOpenDrawing(blockName))
            {
                vm.Status = "Block '" + blockName + "' is not in the open drawing, so the block editor cannot open it. "
                    + "Insert it from the template first, or start a drawing from that template.";
                return;
            }

            // Quoting the name keeps blocks with spaces in one argument.
            document.SendStringToExecute("._BEDIT \"" + blockName + "\"\n", true, false, true);
            vm.Status = "Opening '" + blockName + "' in the AutoCAD block editor.";
        }
        catch (Exception ex)
        {
            vm.Status = "Could not start the block editor: " + ex.Message;
        }
    }
}
