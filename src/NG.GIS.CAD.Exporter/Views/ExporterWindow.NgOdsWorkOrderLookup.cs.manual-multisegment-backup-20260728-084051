using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.Views;

public partial class ExporterWindow
{
    private CancellationTokenSource? _ngOdsWorkOrderLookupCts;
    private Task? _ngOdsStartupLoadTask;
    private bool _syncingNgOdsWorkOrderSelection;
    private List<NgOdsWorkOrderItem> _ngOdsWorkOrders = new();
    private const int NgOdsDropdownDisplayLimit = 1000;

    private async void NgOdsWorkOrderCombos_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ngOdsStartupLoadTask == null)
        {
            _ngOdsStartupLoadTask = LoadNgOdsWorkOrdersOnceAsync();
        }
        await _ngOdsStartupLoadTask;
    }

    private async Task LoadNgOdsWorkOrdersOnceAsync()
    {
        if (_ngOdsWorkOrders.Count > 0) { return; }
        _ngOdsWorkOrderLookupCts?.Cancel();
        _ngOdsWorkOrderLookupCts = new CancellationTokenSource();
        var token = _ngOdsWorkOrderLookupCts.Token;
        try
        {
            if (WorkOrderSelectionComboBox != null) { WorkOrderSelectionComboBox.IsEnabled = false; }
            if (WorkOrderNameComboBox != null) { WorkOrderNameComboBox.IsEnabled = false; }
            SetNgOdsStatus("Loading full NG_ODS work order list once for local filtering...");
            var items = await NgOdsWorkOrderLookup.LoadAllAsync(token);
            if (token.IsCancellationRequested) { return; }
            _ngOdsWorkOrders = items.ToList();
            BindNgOdsDropdowns(_ngOdsWorkOrders.Take(NgOdsDropdownDisplayLimit).ToList(), null, null);
            var shown = Math.Min(NgOdsDropdownDisplayLimit, _ngOdsWorkOrders.Count);
            SetNgOdsStatus($"Loaded {_ngOdsWorkOrders.Count:N0} work orders from NG_ODS. Showing first {shown:N0}; type to filter locally.");
        }
        catch (Exception ex)
        {
            SetNgOdsStatus("NG_ODS work order full startup load failed: " + FlattenNgOdsException(ex));
        }
        finally
        {
            if (WorkOrderSelectionComboBox != null) { WorkOrderSelectionComboBox.IsEnabled = true; }
            if (WorkOrderNameComboBox != null) { WorkOrderNameComboBox.IsEnabled = true; }
        }
    }

    private async void NgOdsWorkOrderNumberCombo_DropDownOpened(object sender, EventArgs e)
    {
        await EnsureNgOdsWorkOrdersLoadedAsync();
        ApplyNgOdsLocalFilter(WorkOrderSelectionComboBox?.Text, null, "wonum dropdown");
    }

    private async void NgOdsWorkOrderNameCombo_DropDownOpened(object sender, EventArgs e)
    {
        await EnsureNgOdsWorkOrdersLoadedAsync();
        ApplyNgOdsLocalFilter(null, WorkOrderNameComboBox?.Text, "name dropdown");
    }

    private async void NgOdsWorkOrderNumberCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab || e.Key == Key.Enter) { return; }
        await EnsureNgOdsWorkOrdersLoadedAsync();
        ApplyNgOdsLocalFilter(WorkOrderSelectionComboBox?.Text, null, "wonum local filter");
    }

    private async void NgOdsWorkOrderNameCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab || e.Key == Key.Enter) { return; }
        await EnsureNgOdsWorkOrdersLoadedAsync();
        ApplyNgOdsLocalFilter(null, WorkOrderNameComboBox?.Text, "name local filter");
    }

    private async Task EnsureNgOdsWorkOrdersLoadedAsync()
    {
        if (_ngOdsWorkOrders.Count > 0) { return; }
        if (_ngOdsStartupLoadTask == null) { _ngOdsStartupLoadTask = LoadNgOdsWorkOrdersOnceAsync(); }
        await _ngOdsStartupLoadTask;
    }

    private void ApplyNgOdsLocalFilter(string? wonumText, string? nameText, string source)
    {
        var wonum = wonumText?.Trim();
        var name = nameText?.Trim();
        IEnumerable<NgOdsWorkOrderItem> query = _ngOdsWorkOrders;
        if (!string.IsNullOrWhiteSpace(wonum))
        {
            query = query.Where(x => !string.IsNullOrWhiteSpace(x.WorkOrderNumber) && x.WorkOrderNumber.StartsWith(wonum, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => !string.IsNullOrWhiteSpace(x.WorkOrderName) && x.WorkOrderName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        var filtered = query.Take(NgOdsDropdownDisplayLimit).ToList();
        BindNgOdsDropdowns(filtered, wonumText, nameText);
        SetNgOdsStatus($"Showing {filtered.Count:N0} locally filtered work orders from {_ngOdsWorkOrders.Count:N0} loaded rows ({source}).");
    }

    private void BindNgOdsDropdowns(List<NgOdsWorkOrderItem> items, string? wonumText, string? nameText)
    {
        if (WorkOrderSelectionComboBox != null)
        {
            var existingText = wonumText ?? WorkOrderSelectionComboBox.Text;
            WorkOrderSelectionComboBox.ItemsSource = items;
            WorkOrderSelectionComboBox.Text = existingText ?? string.Empty;
        }
        if (WorkOrderNameComboBox != null)
        {
            var existingText = nameText ?? WorkOrderNameComboBox.Text;
            WorkOrderNameComboBox.ItemsSource = items;
            WorkOrderNameComboBox.Text = existingText ?? string.Empty;
        }
    }

    private void NgOdsWorkOrderNumberCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNgOdsWorkOrderSelection) { return; }
        if (WorkOrderSelectionComboBox?.SelectedItem is not NgOdsWorkOrderItem item) { return; }
        ApplyNgOdsWorkOrderSelection(item, "wonum");
    }

    private void NgOdsWorkOrderNameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNgOdsWorkOrderSelection) { return; }
        if (WorkOrderNameComboBox?.SelectedItem is not NgOdsWorkOrderItem item) { return; }
        ApplyNgOdsWorkOrderSelection(item, "name");
    }

    private void NgOdsWorkOrderCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        if (WorkOrderDrivenExportRadio != null) { WorkOrderDrivenExportRadio.IsChecked = true; }
    }

    private void ApplyNgOdsWorkOrderSelection(NgOdsWorkOrderItem item, string selectedFrom)
    {
        _syncingNgOdsWorkOrderSelection = true;
        try
        {
            if (WorkOrderSelectionComboBox != null)
            {
                WorkOrderSelectionComboBox.SelectedItem = item;
                WorkOrderSelectionComboBox.Text = item.WorkOrderNumber;
            }
            if (WorkOrderNameComboBox != null)
            {
                WorkOrderNameComboBox.SelectedItem = item;
                WorkOrderNameComboBox.Text = item.WorkOrderName;
            }
            if (WorkOrderDrivenExportRadio != null) { WorkOrderDrivenExportRadio.IsChecked = true; }
            SetNgOdsStatus($"Selected {item.WorkOrderNumber} from NG_ODS by {selectedFrom}. Work Order driven export selected.");
        }
        finally
        {
            _syncingNgOdsWorkOrderSelection = false;
        }
    }

    private void SetNgOdsStatus(string message)
    {
        if (DataContext is ViewModels.ExporterViewModel vm)
        {
            vm.Status = message;
        }
    }

    private static string FlattenNgOdsException(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            messages.Add(current.GetType().Name + ": " + current.Message);
        }
        return string.Join(" -> ", messages);
    }
}
