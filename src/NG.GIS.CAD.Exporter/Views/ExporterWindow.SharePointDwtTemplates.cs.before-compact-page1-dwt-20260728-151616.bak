using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        Loaded += (_, __) => AddSharePointDwtSectionToFirstPagePane();
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => Dispatcher.BeginInvoke(new Action(AddSharePointDwtSectionToFirstPagePane))), true);
    }

    private void AddSharePointDwtSectionToFirstPagePane()
    {
        try
        {
            if (FindName("SharePointDwtSectionRoot") is FrameworkElement) return;
            var targetPanel = FindBestFirstPagePanel();
            if (targetPanel == null) return;
            var root = BuildSharePointDwtSection();
            var insertAt = Math.Min(2, targetPanel.Children.Count);
            targetPanel.Children.Insert(insertAt, root);
        }
        catch { }
    }

    private FrameworkElement BuildSharePointDwtSection()
    {
        var root = new Border
        {
            Name = "SharePointDwtSectionRoot",
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 10)
        };
        var panel = new StackPanel();
        root.Child = panel;
        panel.Children.Add(new TextBlock { Text = "SharePoint CAD templates (.dwt)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        var browse = new Button { Content = "Browse .dwt", Width = 105, Margin = new Thickness(0, 0, 6, 0) };
        browse.Click += async (_, __) => await BrowseSharePointDwtTemplatesAsync();
        var open = new Button { Content = "Download + Open", Width = 130 };
        open.Click += async (_, __) => await DownloadAndOpenSelectedDwtAsync();
        row.Children.Add(browse);
        row.Children.Add(open);
        panel.Children.Add(row);
        _sharePointDwtList = new ListBox { Height = 85, Margin = new Thickness(0, 0, 0, 5) };
        panel.Children.Add(_sharePointDwtList);
        _sharePointDwtStatus = new TextBlock { Text = "Browse SharePoint templates once, then use the selected .dwt across any import method.", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_sharePointDwtStatus);
        return root;
    }

    private Panel? FindBestFirstPagePanel()
    {
        var panels = FindVisualChildren<Panel>(this).Where(p => p.Children.Count > 0).ToList();
        var pageOnePanel = panels.FirstOrDefault(p => FindVisualText(p).Contains("1.") || FindVisualText(p).Contains("Work Order") || FindVisualText(p).Contains("Manual proposed"));
        if (pageOnePanel != null) return pageOnePanel;
        var proofBox = FindName("WorkOrderGeometryTextBox") as TextBox;
        if (proofBox?.Parent is Panel proofPanel) return proofPanel;
        return panels.FirstOrDefault();
    }

    private static string FindVisualText(DependencyObject parent)
    {
        return string.Join(" ", FindVisualChildren<TextBlock>(parent).Select(t => t.Text).Concat(FindVisualChildren<Button>(parent).Select(b => b.Content?.ToString() ?? string.Empty)).Where(s => !string.IsNullOrWhiteSpace(s)));
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

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}