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
    private Task? _ngOdsLoadInFlight;
    private bool _ngOdsLoadFailed;
    private bool _ngOdsConnectionDeclined;
    private bool _syncingNgOdsWorkOrderSelection;
    private List<NgOdsWorkOrderItem> _ngOdsWorkOrders = new();
    private const int NgOdsDropdownDisplayLimit = 1000;

    private async void NgOdsWorkOrderCombos_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureNgOdsWorkOrdersLoadedAsync(userInitiated: false);
    }

    /// <summary>
    /// Makes sure a working NG_ODS connection is configured, prompting for one when it is missing or
    /// when <paramref name="forcePrompt"/> says the configured one has just failed. Returns false when
    /// the user cancels, which latches <see cref="_ngOdsConnectionDeclined"/> so nothing prompts again
    /// until the user explicitly opens a work order dropdown.
    /// </summary>
    private bool EnsureNgOdsConnection(bool forcePrompt)
    {
        if (!forcePrompt && NgOdsConnection.IsConfigured) { return true; }

        var dialog = new NgOdsConnectionWindow();
        if (IsLoaded) { dialog.Owner = this; }

        if (dialog.ShowDialog() == true)
        {
            _ngOdsConnectionDeclined = false;
            return true;
        }

        _ngOdsConnectionDeclined = true;
        SetNgOdsStatus("NG_ODS connection was not configured, so work order lookup is unavailable. "
            + "Open a work order dropdown to enter it.");
        return false;
    }

    private async Task LoadNgOdsWorkOrdersOnceAsync(bool userInitiated)
    {
        if (_ngOdsWorkOrders.Count > 0) { return; }
        _ngOdsWorkOrderLookupCts?.Cancel();
        _ngOdsWorkOrderLookupCts = new CancellationTokenSource();
        var token = _ngOdsWorkOrderLookupCts.Token;
        try
        {
            if (WorkOrderSelectionComboBox != null) { WorkOrderSelectionComboBox.IsEnabled = false; }
            if (WorkOrderNameComboBox != null) { WorkOrderNameComboBox.IsEnabled = false; }
            if (!EnsureNgOdsConnection(forcePrompt: false)) { return; }

            // A prefetch started at command entry is usually already running, and may already be
            // finished, so collect that rather than starting a second identical query.
            var prefetched = NgOdsWorkOrderLookup.TakePrefetched();
            SetNgOdsStatus(prefetched != null
                ? "Collecting the NG_ODS work order list started at startup..."
                : "Loading full NG_ODS work order list once for local filtering...");

            IReadOnlyList<NgOdsWorkOrderItem> items;
            try
            {
                items = prefetched != null
                    ? await prefetched
                    : await NgOdsWorkOrderLookup.LoadAllAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception firstAttempt)
            {
                if (token.IsCancellationRequested) { return; }
                var reason = NgOdsConnection.SummarizeError(FlattenNgOdsException(firstAttempt));
                _ngOdsLoadFailed = true;

                // Re-entering the connection only helps when the credentials are what is wrong. A
                // permission error means the account is correct but is not granted SELECT, so asking
                // for the password again would just make the user retype something already right.
                if (!NgOdsConnection.LooksLikeCredentialProblem(reason))
                {
                    SetNgOdsStatus("NG_ODS work order load failed: " + reason
                        + " This is not a sign-in problem, so re-entering the password will not help. "
                        + "The SQL account needs SELECT on the work order tables.");
                    return;
                }

                SetNgOdsStatus("NG_ODS work order load failed: " + reason);

                // Only offer to re-enter the connection when the user asked for this load. A failure
                // during the startup load is reported and left alone, so an unreachable database does
                // not throw a credentials dialog at every AutoCAD start.
                if (!userInitiated) { return; }
                if (!EnsureNgOdsConnection(forcePrompt: true)) { return; }

                SetNgOdsStatus("Retrying the NG_ODS work order load...");
                items = await NgOdsWorkOrderLookup.LoadAllAsync(token);
                _ngOdsLoadFailed = false;
            }

            if (token.IsCancellationRequested) { return; }
            _ngOdsWorkOrders = items.ToList();
            _ngOdsLoadFailed = false;
            BindNgOdsDropdowns(_ngOdsWorkOrders.Take(NgOdsDropdownDisplayLimit).ToList(), null, null);
            var shown = Math.Min(NgOdsDropdownDisplayLimit, _ngOdsWorkOrders.Count);
            SetNgOdsStatus($"Loaded {_ngOdsWorkOrders.Count:N0} work orders from NG_ODS. Showing first {shown:N0}; type to filter locally.");
        }
        catch (Exception ex)
        {
            _ngOdsLoadFailed = true;
            SetNgOdsStatus("NG_ODS work order load failed: " + NgOdsConnection.SummarizeError(FlattenNgOdsException(ex))
                + " Open a work order dropdown to try again.");
        }
        finally
        {
            if (WorkOrderSelectionComboBox != null) { WorkOrderSelectionComboBox.IsEnabled = true; }
            if (WorkOrderNameComboBox != null) { WorkOrderNameComboBox.IsEnabled = true; }
        }
    }

    private async void NgOdsWorkOrderNumberCombo_DropDownOpened(object sender, EventArgs e)
    {
        await EnsureNgOdsWorkOrdersLoadedAsync(userInitiated: true);
        ApplyNgOdsLocalFilter(WorkOrderSelectionComboBox?.Text, null, "wonum dropdown");
    }

    private async void NgOdsWorkOrderNameCombo_DropDownOpened(object sender, EventArgs e)
    {
        await EnsureNgOdsWorkOrdersLoadedAsync(userInitiated: true);
        ApplyNgOdsLocalFilter(null, WorkOrderNameComboBox?.Text, "name dropdown");
    }

    private async void NgOdsWorkOrderNumberCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab || e.Key == Key.Enter) { return; }
        await EnsureNgOdsWorkOrdersLoadedAsync(userInitiated: false);
        ApplyNgOdsLocalFilter(WorkOrderSelectionComboBox?.Text, null, "wonum local filter");
    }

    private async void NgOdsWorkOrderNameCombo_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab || e.Key == Key.Enter) { return; }
        await EnsureNgOdsWorkOrdersLoadedAsync(userInitiated: false);
        ApplyNgOdsLocalFilter(null, WorkOrderNameComboBox?.Text, "name local filter");
    }

    /// <summary>
    /// Single entry point for loading the work order list. At most one load ever runs: callers that
    /// arrive while one is in flight await that same task instead of starting another.
    ///
    /// <paramref name="userInitiated"/> separates a deliberate gesture, opening a dropdown, from
    /// incidental ones like typing. Only a deliberate gesture retries after a failure or a declined
    /// connection prompt. Without that distinction every keystroke would restart a full table load
    /// against a database that is not answering.
    /// </summary>
    private async Task EnsureNgOdsWorkOrdersLoadedAsync(bool userInitiated)
    {
        if (_ngOdsWorkOrders.Count > 0) { return; }

        var inFlight = _ngOdsLoadInFlight;
        if (inFlight != null) { await inFlight; return; }

        if (!userInitiated && (_ngOdsLoadFailed || _ngOdsConnectionDeclined)) { return; }
        if (userInitiated) { _ngOdsLoadFailed = false; _ngOdsConnectionDeclined = false; }

        var task = LoadNgOdsWorkOrdersOnceAsync(userInitiated);
        _ngOdsLoadInFlight = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(_ngOdsLoadInFlight, task)) { _ngOdsLoadInFlight = null; }
        }
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
