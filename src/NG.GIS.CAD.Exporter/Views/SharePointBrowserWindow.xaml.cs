using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Browses SharePoint for a CAD template: sites, then the libraries in a site, then folders within a
/// library. The equivalent of the local "Pick .dwt" file dialog, so nobody has to know a drive id or
/// paste a URL.
///
/// Sign-in stays manual here too. Opening this window restores an existing session silently; only the
/// sign-in button can open a browser.
/// </summary>
public partial class SharePointBrowserWindow : Window
{
    private readonly SharePointDwtTemplateService _service;

    /// <summary>Folder ids from the library root down to where the user currently is.</summary>
    private readonly List<SharePointItem> _breadcrumb = new();

    private SharePointDrive? _drive;

    /// <summary>Local path of the downloaded template once the dialog is accepted.</summary>
    public string? SelectedTemplatePath { get; private set; }

    public SharePointBrowserWindow(SharePointDwtTemplateService service)
    {
        _service = service;
        InitializeComponent();
        Loaded += async (_, _) => await RestoreSessionAsync();
    }

    private async Task RestoreSessionAsync()
    {
        SetBusy(true);
        try
        {
            SetStatus("Checking for a saved SharePoint session...");
            var restored = await _service.TryRestoreSessionAsync();
            UpdateAccount();

            if (!restored)
            {
                SetStatus("Not signed in. Use Sign in to continue.");
                return;
            }
            await LoadFollowedSitesAsync();
        }
        finally { SetBusy(false); }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            SetStatus("A browser window has opened for sign-in. Complete it there.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _service.SignInInteractiveAsync(timeout.Token);
            UpdateAccount();
            await LoadFollowedSitesAsync();
        }
        catch (OperationCanceledException) { SetStatus("Sign-in timed out after 5 minutes."); }
        catch (Exception ex) { SetStatus("Sign-in failed: " + ex.Message); }
        finally { SetBusy(false); }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _service.SignOutAsync();
            SiteList.ItemsSource = null;
            DriveList.ItemsSource = null;
            ItemList.ItemsSource = null;
            _breadcrumb.Clear();
            _drive = null;
            PathText.Text = string.Empty;
            UpdateAccount();
            SetStatus("Signed out. The saved session has been removed from this machine.");
        }
        catch (Exception ex) { SetStatus("Sign-out failed: " + ex.Message); }
    }

    private async void FollowedSites_Click(object sender, RoutedEventArgs e) => await LoadFollowedSitesAsync();

    private async void SearchSites_Click(object sender, RoutedEventArgs e) => await SearchSitesAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { await SearchSitesAsync(); }
    }

    private async Task LoadFollowedSitesAsync()
    {
        SetBusy(true);
        try
        {
            SetStatus("Loading the sites you follow...");
            var sites = await _service.ListFollowedSitesAsync(CancellationToken.None);
            SiteList.ItemsSource = sites;
            SetStatus(sites.Count == 0
                ? "You do not follow any sites. Search for one by name instead."
                : sites.Count + " followed site(s). Pick one to see its libraries.");
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { SetBusy(false); }
    }

    private async Task SearchSitesAsync()
    {
        SetBusy(true);
        try
        {
            var query = SearchBox.Text?.Trim() ?? string.Empty;
            SetStatus("Searching sites for \"" + query + "\"...");
            var sites = await _service.SearchSitesAsync(query, CancellationToken.None);
            SiteList.ItemsSource = sites;
            SetStatus(sites.Count == 0
                ? "No sites matched. Try part of the site name."
                : sites.Count + " site(s) found. Pick one to see its libraries.");
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { SetBusy(false); }
    }

    private async void SiteList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SiteList.SelectedItem is not SharePointSite site) { return; }

        DriveList.ItemsSource = null;
        ItemList.ItemsSource = null;
        _breadcrumb.Clear();
        _drive = null;
        PathText.Text = string.Empty;

        SetBusy(true);
        try
        {
            SetStatus("Loading libraries in " + site.Name + "...");
            var drives = await _service.ListDrivesAsync(site.Id, CancellationToken.None);
            DriveList.ItemsSource = drives;
            SetStatus(drives.Count == 0
                ? "That site has no document libraries this account can read."
                : drives.Count + " librar(y/ies). Pick one to browse its folders.");
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { SetBusy(false); }
    }

    private async void DriveList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DriveList.SelectedItem is not SharePointDrive drive) { return; }

        _drive = drive;
        _breadcrumb.Clear();
        await LoadCurrentFolderAsync();
    }

    private async void ItemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is not SharePointItem item) { return; }

        if (item.IsFolder)
        {
            _breadcrumb.Add(item);
            await LoadCurrentFolderAsync();
            return;
        }
        await UseSelectedAsync();
    }

    private void ItemList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ItemList.SelectedItem is SharePointItem { IsFolder: false } file)
        {
            SetStatus("Selected " + file.Name + ". Choose Use selected template, or double-click it.");
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_breadcrumb.Count == 0) { return; }
        _breadcrumb.RemoveAt(_breadcrumb.Count - 1);
        await LoadCurrentFolderAsync();
    }

    private async Task LoadCurrentFolderAsync()
    {
        if (_drive == null) { return; }

        SetBusy(true);
        try
        {
            var itemId = _breadcrumb.Count == 0 ? null : _breadcrumb[^1].Id;
            var items = await _service.ListChildrenAsync(_drive.Id, itemId, CancellationToken.None);

            ItemList.ItemsSource = items;
            PathText.Text = _drive.Name + "/" + string.Join("/", _breadcrumb.Select(b => b.Name));
            UpButton.IsEnabled = _breadcrumb.Count > 0;

            SetStatus(items.Count == 0
                ? "This folder holds no subfolders and no .dwt files."
                : "Double-click a folder to open it, or pick a .dwt file.");
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { SetBusy(false); }
    }

    private async void Use_Click(object sender, RoutedEventArgs e) => await UseSelectedAsync();

    private async Task UseSelectedAsync()
    {
        if (ItemList.SelectedItem is not SharePointItem item || item.IsFolder)
        {
            SetStatus("Pick a .dwt file first.");
            return;
        }

        SetBusy(true);
        try
        {
            SetStatus("Downloading " + item.Name + "...");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            SelectedTemplatePath = await _service.DownloadTemplateAsync(item.ToTemplate(), timeout.Token);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetStatus("Download failed: " + ex.Message);
        }
        finally { SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdateAccount()
    {
        AccountText.Text = _service.IsSignedIn ? "Signed in as " + _service.SignedInAs : "Not signed in.";
        SignOutButton.IsEnabled = _service.IsSignedIn;
    }

    private void SetBusy(bool busy)
    {
        SignInButton.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        FollowedButton.IsEnabled = !busy;
        UseButton.IsEnabled = !busy;
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
