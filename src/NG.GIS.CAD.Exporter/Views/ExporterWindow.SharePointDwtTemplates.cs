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
    private string? _selectedDwtPath;

    private void PickLocalDwtTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "AutoCAD template (*.dwt)|*.dwt",
            Title = "Select CAD template (.dwt)"
        };
        if (dialog.ShowDialog() != true) { return; }

        _selectedDwtPath = dialog.FileName;
        LocalDwtPathTextBox.Text = dialog.FileName;
        SetLocalDwtStatus("Selected template: " + dialog.FileName);
    }

    private void OpenLocalDwtTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_selectedDwtPath) || !File.Exists(_selectedDwtPath))
            {
                SetLocalDwtStatus("Select a .dwt template first.");
                return;
            }

            Process.Start(new ProcessStartInfo(_selectedDwtPath) { UseShellExecute = true });
            SetLocalDwtStatus("Opened template: " + _selectedDwtPath);
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

        _selectedDwtPath = browser.SelectedTemplatePath;
        LocalDwtPathTextBox.Text = browser.SelectedTemplatePath;
        SetLocalDwtStatus("Selected template: " + browser.SelectedTemplatePath);
        SetSharePointStatus("Downloaded from SharePoint as " + (service.SignedInAs ?? "the signed-in account") + ".");
    }

    private void SetSharePointStatus(string message) => SharePointStatusText.Text = message;
}
