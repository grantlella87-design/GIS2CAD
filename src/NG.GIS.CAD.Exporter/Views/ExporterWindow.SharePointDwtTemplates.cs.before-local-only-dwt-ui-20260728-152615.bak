using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private bool _sharePointDwtSectionInstalled;
    private ComboBox? _sharePointDwtCombo;
    private TextBlock? _sharePointDwtStatus;
    private SharePointDwtTemplateService? _sharePointDwtService;
    private string? _selectedDwtPath;

    private void InstallSharePointDwtTemplateSection()
    {
        if (_sharePointDwtSectionInstalled) return;
        _sharePointDwtSectionInstalled = true;
        Loaded += (_, __) => AddCompactDwtSectionToPage1Only();
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => Dispatcher.BeginInvoke(new Action(AddCompactDwtSectionToPage1Only))), true);
    }

    private void AddCompactDwtSectionToPage1Only()
    {
        try
        {
            if (FindName("SharePointDwtSectionRoot") is FrameworkElement) return;
            if (!IsPage1Visible()) return;
            var panel = FindPage1NgOdsPanel();
            if (panel == null) return;
            var root = new StackPanel { Name = "SharePointDwtSectionRoot", Margin = new Thickness(0, 8, 0, 8) };
            root.Children.Add(new TextBlock { Text = "CAD template (.dwt)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var browseGraph = new Button { Content = "SharePoint", Width = 86, Margin = new Thickness(0, 0, 5, 0) };
            browseGraph.Click += async (_, __) => await BrowseSharePointDwtTemplatesAsync();
            var browseLocal = new Button { Content = "Pick local/synced .dwt", Width = 145, Margin = new Thickness(0, 0, 5, 0) };
            browseLocal.Click += (_, __) => PickLocalDwt();
            _sharePointDwtCombo = new ComboBox { Width = 260, Height = 24, Margin = new Thickness(0, 0, 5, 0) };
            var open = new Button { Content = "Download/Open", Width = 110 };
            open.Click += async (_, __) => await DownloadAndOpenSelectedDwtAsync();
            row.Children.Add(browseGraph);
            row.Children.Add(browseLocal);
            row.Children.Add(_sharePointDwtCombo);
            row.Children.Add(open);
            root.Children.Add(row);
            _sharePointDwtStatus = new TextBlock { Text = "Optional universal template for all export methods.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            root.Children.Add(_sharePointDwtStatus);
            var insertAt = Math.Min(panel.Children.Count, 7);
            panel.Children.Insert(insertAt, root);
        }
        catch { }
    }

    private bool IsPage1Visible()
    {
        var text = string.Join(" ", FindVisualChildren<TextBlock>(this).Select(t => t.Text).Concat(FindVisualChildren<RadioButton>(this).Select(r => r.Content?.ToString() ?? string.Empty)));
        return text.Contains("1. Export Method") || text.Contains("Export method") || text.Contains("Work order lookup from NG_ODS");
    }

    private Panel? FindPage1NgOdsPanel()
    {
        var panels = FindVisualChildren<Panel>(this).Where(p => p.Children.Count > 0).ToList();
        return panels.FirstOrDefault(p => FindPanelText(p).Contains("Work order lookup from NG_ODS"))
            ?? panels.FirstOrDefault(p => FindPanelText(p).Contains("Export method"))
            ?? panels.OrderByDescending(p => p.Children.Count).FirstOrDefault();
    }

    private static string FindPanelText(DependencyObject parent)
    {
        return string.Join(" ", FindVisualChildren<TextBlock>(parent).Select(t => t.Text).Concat(FindVisualChildren<RadioButton>(parent).Select(r => r.Content?.ToString() ?? string.Empty)).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private async Task BrowseSharePointDwtTemplatesAsync()
    {
        try
        {
            SetSharePointDwtStatus("Loading SharePoint .dwt templates...");
            _sharePointDwtService ??= new SharePointDwtTemplateService();
            var items = await _sharePointDwtService.ListDefaultTemplatesAsync();
            if (_sharePointDwtCombo != null)
            {
                _sharePointDwtCombo.ItemsSource = items;
                if (items.Count > 0) _sharePointDwtCombo.SelectedIndex = 0;
            }
            SetSharePointDwtStatus(items.Count == 0 ? "No .dwt files found in default SharePoint folder." : "Loaded " + items.Count + " SharePoint .dwt template(s).");
        }
        catch (Exception ex)
        {
            SetSharePointDwtStatus("SharePoint browse failed. Use local/synced picker. " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void PickLocalDwt()
    {
        var dialog = new OpenFileDialog { Filter = "AutoCAD template (*.dwt)|*.dwt", Title = "Select CAD template (.dwt)" };
        if (dialog.ShowDialog() == true)
        {
            _selectedDwtPath = dialog.FileName;
            if (_sharePointDwtCombo != null)
            {
                _sharePointDwtCombo.ItemsSource = new[] { dialog.FileName };
                _sharePointDwtCombo.SelectedIndex = 0;
            }
            SetSharePointDwtStatus("Selected local/synced template: " + dialog.FileName);
        }
    }

    private async Task DownloadAndOpenSelectedDwtAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_selectedDwtPath))
            {
                SharePointDwtTemplateService.OpenTemplatePath(_selectedDwtPath);
                SetSharePointDwtStatus("Opened template: " + _selectedDwtPath);
                return;
            }
            if (_sharePointDwtCombo?.SelectedItem is not SharePointDwtTemplateItem item)
            {
                SetSharePointDwtStatus("Select a .dwt template first.");
                return;
            }
            _sharePointDwtService ??= new SharePointDwtTemplateService();
            SetSharePointDwtStatus("Downloading " + item.Name + "...");
            var path = await _sharePointDwtService.DownloadTemplateAsync(item);
            SharePointDwtTemplateService.OpenTemplatePath(path);
            SetSharePointDwtStatus("Downloaded/opened template: " + path);
        }
        catch (Exception ex)
        {
            SetSharePointDwtStatus("Template open failed: " + ex.GetType().Name + ": " + ex.Message);
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