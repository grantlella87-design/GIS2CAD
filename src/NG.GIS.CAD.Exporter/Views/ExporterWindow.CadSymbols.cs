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
