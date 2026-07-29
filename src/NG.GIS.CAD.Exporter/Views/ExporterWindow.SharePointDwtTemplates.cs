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
    /// Opening the section tries to restore an existing session silently. It never signs in, so
    /// expanding the panel cannot put a browser window in front of the user.
    /// </summary>
    private async void SharePointTemplates_Expanded(object sender, RoutedEventArgs e)
    {
        var service = GetSharePointService();
        if (service == null) { SetSharePointStatus("Waiting for the profile to load."); return; }

        if (DataContext is ExporterViewModel vm && string.IsNullOrEmpty(SharePointFolderUrlBox.Text))
        {
            SharePointFolderUrlBox.Text = vm.SharePointTemplateSettings.FolderUrl ?? string.Empty;
        }

        if (service.IsSignedIn) { return; }

        SetSharePointStatus("Checking for a saved SharePoint session...");
        var restored = await service.TryRestoreSessionAsync();
        SetSharePointStatus(restored
            ? "Signed in as " + service.SignedInAs + " from a saved session."
            : "Not signed in. Use Sign in to SharePoint.");

        if (restored) { await LoadSharePointTemplatesAsync(service); }
    }

    private async void SharePointSignIn_Click(object sender, RoutedEventArgs e)
    {
        var service = GetSharePointService();
        if (service == null) { SetSharePointStatus("Waiting for the profile to load."); return; }

        SetSharePointBusy(true);
        try
        {
            SetSharePointStatus("A browser window has opened for sign-in. Complete it there.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await service.SignInInteractiveAsync(timeout.Token);

            // Normally the session is saved and this is empty. It is only set when the cache file
            // could not be used, which the user should know about rather than discover next launch.
            SetSharePointStatus(string.IsNullOrEmpty(service.TokenCacheWarning)
                ? "Signed in as " + service.SignedInAs + ". This session will be remembered."
                : "Signed in as " + service.SignedInAs + ". " + service.TokenCacheWarning);

            await LoadSharePointTemplatesAsync(service);
        }
        catch (OperationCanceledException)
        {
            SetSharePointStatus("Sign-in timed out after 5 minutes.");
        }
        catch (Exception ex)
        {
            SetSharePointStatus("Sign-in failed: " + ex.Message);
        }
        finally { SetSharePointBusy(false); }
    }

    /// <summary>
    /// Stores the pasted folder URL in the profile so it survives a restart. The service reads the
    /// same settings instance, so the next Refresh uses the new folder without rebuilding anything.
    /// </summary>
    private async void SharePointFolderUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExporterViewModel vm) { return; }

        var url = SharePointFolderUrlBox.Text?.Trim() ?? string.Empty;
        if (string.Equals(url, vm.SharePointTemplateSettings.FolderUrl, StringComparison.Ordinal)) { return; }

        vm.SharePointTemplateSettings.FolderUrl = url;
        await vm.SaveProfileAsync();
        SetSharePointStatus(string.IsNullOrEmpty(url)
            ? "Folder URL cleared. The drive id and path from the profile will be used."
            : "Folder URL saved. Use Refresh list to read it.");
    }

    private async void SharePointRefresh_Click(object sender, RoutedEventArgs e)
    {
        var service = GetSharePointService();
        if (service == null) { SetSharePointStatus("Waiting for the profile to load."); return; }
        await LoadSharePointTemplatesAsync(service);
    }

    private async void SharePointSignOut_Click(object sender, RoutedEventArgs e)
    {
        var service = GetSharePointService();
        if (service == null) { return; }

        try
        {
            await service.SignOutAsync();
            SharePointTemplateList.ItemsSource = null;
            SetSharePointStatus("Signed out. The saved session has been removed from this machine.");
        }
        catch (Exception ex) { SetSharePointStatus("Sign-out failed: " + ex.Message); }
    }

    private async void SharePointUseSelected_Click(object sender, RoutedEventArgs e)
    {
        var service = GetSharePointService();
        if (service == null) { return; }

        if (SharePointTemplateList.SelectedItem is not SharePointDwtTemplateItem selected)
        {
            SetSharePointStatus("Select a template from the list first.");
            return;
        }

        SetSharePointBusy(true);
        try
        {
            SetSharePointStatus("Downloading " + selected.Name + "...");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var path = await service.DownloadTemplateAsync(selected, timeout.Token);

            // Feed the downloaded copy into the same slot the local picker fills, so the rest of the
            // window sees one selected template regardless of where it came from.
            _selectedDwtPath = path;
            LocalDwtPathTextBox.Text = path;
            SetLocalDwtStatus("Selected template: " + path);
            SetSharePointStatus("Downloaded " + selected.Name + ".");
        }
        catch (Exception ex) { SetSharePointStatus("Download failed: " + ex.Message); }
        finally { SetSharePointBusy(false); }
    }

    private async Task LoadSharePointTemplatesAsync(SharePointDwtTemplateService service)
    {
        SetSharePointBusy(true);
        try
        {
            SetSharePointStatus("Loading templates from SharePoint...");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var templates = await service.ListTemplatesAsync(timeout.Token);

            SharePointTemplateList.ItemsSource = templates;
            SetSharePointStatus(templates.Count == 0
                ? "No .dwt files were found in the configured SharePoint folder."
                : templates.Count + " template(s) found. Select one and choose Download and use.");
        }
        catch (Exception ex)
        {
            SharePointTemplateList.ItemsSource = null;
            SetSharePointStatus(ex.Message);
        }
        finally { SetSharePointBusy(false); }
    }

    private void SetSharePointBusy(bool busy)
    {
        SharePointSignInButton.IsEnabled = !busy;
        SharePointRefreshButton.IsEnabled = !busy;
        SharePointSignOutButton.IsEnabled = !busy;
        SharePointUseButton.IsEnabled = !busy;
    }

    private void SetSharePointStatus(string message) => SharePointStatusText.Text = message;
}
