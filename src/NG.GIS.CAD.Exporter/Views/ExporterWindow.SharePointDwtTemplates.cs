using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

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
}
