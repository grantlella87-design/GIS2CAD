using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private bool _sharePointDwtSectionInstalled;
    private ListBox? _sharePointDwtList;
    private TextBlock? _sharePointDwtStatus;
    private SharePointDwtTemplateService? _sharePointDwtService;

    private void InstallSharePointDwtTemplateSection()
    {
        if (_sharePointDwtSectionInstalled) return;
        _sharePointDwtSectionInstalled = true;
        Loaded += (_, __) => AddSharePointDwtSectionToExtentPane();
    }

    private void AddSharePointDwtSectionToExtentPane()
    {
        try
        {
            var proofBox = FindName("WorkOrderGeometryTextBox") as TextBox;
            var panel = proofBox?.Parent as Panel ?? FindFirstVisualChild<StackPanel>(this) as Panel;
            if (panel == null) return;
            if (panel.Children.OfType<FrameworkElement>().Any(e => string.Equals(e.Name, "SharePointDwtSectionRoot", StringComparison.OrdinalIgnoreCase))) return;
            var root = new StackPanel { Name = "SharePointDwtSectionRoot", Margin = new Thickness(0, 12, 0, 0) };
            root.Children.Add(new TextBlock { Text = "SharePoint CAD templates (.dwt)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var browse = new Button { Content = "Browse .dwt", Width = 105, Margin = new Thickness(0, 0, 6, 0) };
            browse.Click += async (_, __) => await BrowseSharePointDwtTemplatesAsync();
            var open = new Button { Content = "Download + Open", Width = 130, Margin = new Thickness(0, 0, 6, 0) };
            open.Click += async (_, __) => await DownloadAndOpenSelectedDwtAsync();
            row.Children.Add(browse);
            row.Children.Add(open);
            root.Children.Add(row);
            _sharePointDwtList = new ListBox { Height = 90, Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(_sharePointDwtList);
            _sharePointDwtStatus = new TextBlock { Text = "Sign in and browse SharePoint for .dwt files.", TextWrapping = TextWrapping.Wrap };
            root.Children.Add(_sharePointDwtStatus);
            panel.Children.Add(root);
        }
        catch { }
    }

    private async Task BrowseSharePointDwtTemplatesAsync()
    {
        try
        {
            SetSharePointDwtStatus("Connecting to Microsoft Graph and loading .dwt templates...");
            _sharePointDwtService ??= new SharePointDwtTemplateService();
            var items = await _sharePointDwtService.ListDefaultTemplatesAsync();
            if (_sharePointDwtList != null)
            {
                _sharePointDwtList.ItemsSource = items;
                if (items.Count > 0) _sharePointDwtList.SelectedIndex = 0;
            }
            SetSharePointDwtStatus(items.Count == 0 ? "No .dwt files found in the default SharePoint template folder." : "Loaded " + items.Count + " .dwt template(s) from SharePoint.");
        }
        catch (Exception ex)
        {
            SetSharePointDwtStatus("SharePoint .dwt browse failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private async Task DownloadAndOpenSelectedDwtAsync()
    {
        try
        {
            if (_sharePointDwtList?.SelectedItem is not SharePointDwtTemplateItem item)
            {
                SetSharePointDwtStatus("Select a .dwt template first.");
                return;
            }
            _sharePointDwtService ??= new SharePointDwtTemplateService();
            SetSharePointDwtStatus("Downloading " + item.Name + "...");
            var path = await _sharePointDwtService.DownloadTemplateAsync(item);
            SharePointDwtTemplateService.OpenTemplatePath(path);
            SetSharePointDwtStatus("Downloaded and opened template: " + path);
        }
        catch (Exception ex)
        {
            SetSharePointDwtStatus("SharePoint .dwt download/open failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void SetSharePointDwtStatus(string message)
    {
        if (_sharePointDwtStatus != null) _sharePointDwtStatus.Text = message;
    }

    private static T? FindFirstVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindFirstVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}