using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private bool _localDwtSectionInstalled;
    private TextBox? _localDwtPathTextBox;
    private TextBlock? _localDwtStatus;
    private string? _selectedDwtPath;

    private void InstallSharePointDwtTemplateSection()
    {
        if (_localDwtSectionInstalled) return;
        _localDwtSectionInstalled = true;
        Loaded += (_, __) => AddLocalDwtSectionToPage1Only();
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, __) => Dispatcher.BeginInvoke(new Action(AddLocalDwtSectionToPage1Only))), true);
    }

    private void AddLocalDwtSectionToPage1Only()
    {
        try
        {
            if (FindName("LocalDwtSectionRoot") is FrameworkElement) return;
            if (!IsPage1VisibleForDwt()) return;
            var panel = FindPage1PanelForDwt();
            if (panel == null) return;
            var root = new StackPanel { Name = "LocalDwtSectionRoot", Margin = new Thickness(0, 6, 0, 8) };
            root.Children.Add(new TextBlock { Text = "CAD template (.dwt)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var pick = new Button { Content = "Pick .dwt", Width = 80, Height = 24, Margin = new Thickness(0, 0, 5, 0) };
            pick.Click += (_, __) => PickLocalOrSyncedDwt();
            _localDwtPathTextBox = new TextBox { Width = 440, Height = 24, IsReadOnly = true, Margin = new Thickness(0, 0, 5, 0) };
            var open = new Button { Content = "Open", Width = 70, Height = 24 };
            open.Click += (_, __) => OpenSelectedDwt();
            row.Children.Add(pick);
            row.Children.Add(_localDwtPathTextBox);
            row.Children.Add(open);
            root.Children.Add(row);
            _localDwtStatus = new TextBlock { Text = "Select a local or synced SharePoint .dwt. No Graph sign-in is used.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            root.Children.Add(_localDwtStatus);
            var insertAt = Math.Min(panel.Children.Count, 7);
            panel.Children.Insert(insertAt, root);
        }
        catch { }
    }

    private bool IsPage1VisibleForDwt()
    {
        var text = string.Join(" ", FindVisualChildren<TextBlock>(this).Select(t => t.Text).Concat(FindVisualChildren<RadioButton>(this).Select(r => r.Content?.ToString() ?? string.Empty)));
        return text.Contains("1. Export Method") || text.Contains("Export method") || text.Contains("Work order lookup from NG_ODS");
    }

    private Panel? FindPage1PanelForDwt()
    {
        var panels = FindVisualChildren<Panel>(this).Where(p => p.Children.Count > 0).ToList();
        return panels.FirstOrDefault(p => FindPanelTextForDwt(p).Contains("Work order lookup from NG_ODS"))
            ?? panels.FirstOrDefault(p => FindPanelTextForDwt(p).Contains("Export method"))
            ?? panels.OrderByDescending(p => p.Children.Count).FirstOrDefault();
    }

    private static string FindPanelTextForDwt(DependencyObject parent)
    {
        return string.Join(" ", FindVisualChildren<TextBlock>(parent).Select(t => t.Text).Concat(FindVisualChildren<RadioButton>(parent).Select(r => r.Content?.ToString() ?? string.Empty)).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private void PickLocalOrSyncedDwt()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "AutoCAD template (*.dwt)|*.dwt",
            Title = "Select CAD template (.dwt)"
        };
        if (dialog.ShowDialog() == true)
        {
            _selectedDwtPath = dialog.FileName;
            if (_localDwtPathTextBox != null) _localDwtPathTextBox.Text = dialog.FileName;
            SetLocalDwtStatus("Selected template: " + dialog.FileName);
        }
    }

    private void OpenSelectedDwt()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_selectedDwtPath) || !File.Exists(_selectedDwtPath))
            {
                SetLocalDwtStatus("Select a .dwt template first.");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_selectedDwtPath) { UseShellExecute = true });
            SetLocalDwtStatus("Opened template: " + _selectedDwtPath);
        }
        catch (Exception ex)
        {
            SetLocalDwtStatus("Template open failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void SetLocalDwtStatus(string message)
    {
        if (_localDwtStatus != null) _localDwtStatus.Text = message;
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