using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using NG.GIS.CAD.Exporter.Services;
using NG.GIS.CAD.Exporter.ViewModels;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Local CAD template (.dwt) picker. The UI lives in the MethodPage section of
/// ExporterWindow.xaml, so it is shown on page 1 only and hidden with that page.
/// </summary>
public partial class ExporterWindow
{
    /// <summary>
    /// The template currently chosen, read from the view model rather than held here as well.
    ///
    /// It used to be kept in a field beside the view model's copy, which meant the box on page 1 only
    /// ever showed what this window had put there this session. A template restored from the profile
    /// arrived in the view model and was invisible here, so a saved choice looked unsaved.
    /// </summary>
    private string? SelectedDwtPath => (DataContext as ExporterViewModel)?.TemplatePath;

    private void PickLocalDwtTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "AutoCAD template (*.dwt)|*.dwt",
            Title = "Select CAD template (.dwt)"
        };
        if (dialog.ShowDialog() != true) { return; }

        SetLocalDwtStatus("Selected template: " + dialog.FileName);
        PublishTemplatePath(dialog.FileName);
    }

    private void OpenLocalDwtTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SelectedDwtPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetLocalDwtStatus("Select a .dwt template first.");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetLocalDwtStatus("Opened template: " + path);
        }
        catch (Exception ex)
        {
            SetLocalDwtStatus("Template open failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void SetLocalDwtStatus(string message) => LocalDwtStatusText.Text = message;

    // ---- SharePoint templates -------------------------------------------------------------

    private SharePointDwtTemplateService? _sharePoint;

    /// <summary>
    /// Built from the profile on first use. Returns null when the profile has not loaded yet, which
    /// is reported rather than treated as an error.
    /// </summary>
    private SharePointDwtTemplateService? GetSharePointService()
    {
        if (_sharePoint != null) { return _sharePoint; }
        if (DataContext is not ExporterViewModel vm) { return null; }

        _sharePoint = new SharePointDwtTemplateService(vm.SharePointTemplateSettings);
        return _sharePoint;
    }

    /// <summary>
    /// Opens the SharePoint browser, the counterpart of the local Pick .dwt dialog. Everything about
    /// choosing a template lives in that window: signing in, finding a site, picking a library and
    /// walking its folders. A template chosen there lands in the same box the local picker fills, so
    /// the rest of the window sees one selected template wherever it came from.
    /// </summary>
    private void PickSharePointTemplate_Click(object sender, RoutedEventArgs e)
    {
        var service = GetSharePointService();
        if (service == null)
        {
            SetSharePointStatus("The profile has not loaded yet. Try again in a moment.");
            return;
        }

        var browser = new SharePointBrowserWindow(service);
        if (IsLoaded) { browser.Owner = this; }

        if (browser.ShowDialog() != true || string.IsNullOrWhiteSpace(browser.SelectedTemplatePath))
        {
            SetSharePointStatus(string.Empty);
            return;
        }

        SetLocalDwtStatus("Selected template: " + browser.SelectedTemplatePath);
        PublishTemplatePath(browser.SelectedTemplatePath);
        SetSharePointStatus("Downloaded from SharePoint as " + (service.SignedInAs ?? "the signed-in account") + ".");
    }

    /// <summary>
    /// Hands the chosen template to the view model, so page 4 can offer its blocks and line types as
    /// an alternative to the ones in the open drawing, and writes it into the profile so the choice is
    /// still there next session.
    ///
    /// The box on page 1 is bound to the view model, so it follows from this rather than being set
    /// alongside it -- assigning its Text directly would replace that binding with a literal and the
    /// box would stop tracking the template from then on.
    /// </summary>
    private void PublishTemplatePath(string path)
    {
        if (DataContext is not ExporterViewModel vm) { return; }
        vm.TemplatePath = path;
        _ = vm.SaveTemplatePathAsync();
    }

    private void SetSharePointStatus(string message) => SharePointStatusText.Text = message;
}
