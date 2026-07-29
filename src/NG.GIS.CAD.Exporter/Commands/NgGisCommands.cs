using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using NG.GIS.CAD.Exporter.Services;
using NG.GIS.CAD.Exporter.ViewModels;
using NG.GIS.CAD.Exporter.Views;
using NG.GIS.CAD.Exporter.Auth;

namespace NG.GIS.CAD.Exporter.Commands;

public sealed class NgGisCommands
{
    [CommandMethod("NGGIS")]
    public void ShowExporter()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var editor = document.Editor;

        // Signs in to the ArcGIS portal and primes the shared token cache before the window opens.
        ArcGisPortalOAuth.LoginAndGetToken(editor);

        try
        {
            var services = AppServices.CreateDefault();
            var viewModel = new ExporterViewModel(services);
            var window = new ExporterWindow { DataContext = viewModel };
            Application.ShowModelessWindow(window);
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nNGGIS failed: {ex.Message}");
        }
    }
}
