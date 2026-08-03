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
            // The boxes stay usable while this runs. They used to be disabled for the duration, which
            // meant a user who already knew the work order number had to watch a list of thousands load
            // before they could type six digits into it. The list is a convenience for finding a number
            // that is not known; it is not a precondition for entering one.
            //
            // Nothing here depends on them being locked. The text is read when it is needed rather than
            // held, and the load only ever adds rows to filter against.
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

            // Anything typed while this was loading is now worth filtering by. Showing the first rows of
            // the whole list instead would throw away what the user had already told us and leave them
            // to retype it, which is the same wait they were just spared.
            var typedNumber = WorkOrderSelectionComboBox?.Text?.Trim();
            var typedName = WorkOrderNameComboBox?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(typedNumber) || !string.IsNullOrWhiteSpace(typedName))
            {
                ApplyNgOdsLocalFilter(typedNumber, typedName, "typed while loading");
                return;
            }

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
    /// Takes the work order on Enter, so a number that is known can be typed and committed without
    /// reaching for the list.
    ///
    /// On PreviewKeyDown rather than KeyUp. Enter is a key other things want: it closes a dropdown and
    /// it is what a default button would take, so being the first to see it is what makes this
    /// dependable rather than a race. Nothing is handled unless a work order is actually taken, so
    /// Enter in an empty box still does whatever it did before.
    /// </summary>
    private void NgOdsWorkOrderCombo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) { return; }
        if (sender is not ComboBox combo) { return; }

        var chosen = ResolveTypedNgOdsWorkOrder(combo);
        if (chosen == null) { return; }

        combo.IsDropDownOpen = false;
        ApplyNgOdsWorkOrderSelection(chosen, "Enter");
        e.Handled = true;
    }

    /// <summary>
    /// The work order Enter should take, or null when the typing matches nothing.
    ///
    /// A row highlighted in the open list wins, because it is what the user is looking at. Otherwise
    /// the text decides: an exact number or name first, so typing one in full takes that one and not
    /// something merely beginning the same way, and failing that the top of the filtered list -- which
    /// is the row shown directly under the caret, so Enter takes what the dropdown is offering.
    /// </summary>
    private NgOdsWorkOrderItem? ResolveTypedNgOdsWorkOrder(ComboBox combo)
    {
        if (combo.IsDropDownOpen && combo.SelectedItem is NgOdsWorkOrderItem highlighted) { return highlighted; }

        var isNumberBox = ReferenceEquals(combo, WorkOrderSelectionComboBox);
        var typed = combo.Text?.Trim();
        if (string.IsNullOrWhiteSpace(typed)) { return null; }

        var exact = _ngOdsWorkOrders.FirstOrDefault(x => string.Equals(
            isNumberBox ? x.WorkOrderNumber : x.WorkOrderName, typed, StringComparison.OrdinalIgnoreCase));
        if (exact != null) { return exact; }

        // The same test the list is filtered by, so the first row here is the first row on screen.
        return isNumberBox
            ? _ngOdsWorkOrders.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.WorkOrderNumber)
                && x.WorkOrderNumber.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            : _ngOdsWorkOrders.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.WorkOrderName)
                && x.WorkOrderName.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0);
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
        BindOneNgOdsDropdown(WorkOrderSelectionComboBox, items, wonumText);
        BindOneNgOdsDropdown(WorkOrderNameComboBox, items, nameText);
    }

    /// <summary>
    /// Puts the filtered rows on one dropdown and opens it, without disturbing what is being typed.
    ///
    /// Changing ItemsSource clears the selection, and clearing the selection empties the editable box,
    /// so the text has to be written back afterwards. Writing it back is what moves the caret to the
    /// end -- which on its own is enough to make typing into the middle of a number impossible, since
    /// every keystroke throws the caret to the end before the next one arrives. The caret is therefore
    /// read before the rebuild and put back after it, and after the dropdown opens, so that nothing
    /// which follows can move it again.
    ///
    /// The text is only written when the rebuild actually changed it. A set that changes nothing still
    /// resets the caret, so the cheapest fix for most keystrokes is not to write at all.
    /// </summary>
    private static void BindOneNgOdsDropdown(ComboBox? combo, List<NgOdsWorkOrderItem> items, string? text)
    {
        if (combo == null) { return; }

        var editor = combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox;
        var caret = editor?.SelectionStart ?? -1;
        var selectionLength = editor?.SelectionLength ?? 0;

        var desired = text ?? combo.Text ?? string.Empty;

        combo.ItemsSource = items;
        if (!string.Equals(combo.Text, desired, StringComparison.Ordinal)) { combo.Text = desired; }

        // Only for the box being typed in. The two dropdowns are rebuilt together so that picking in
        // one narrows the other, and popping open the one nobody is looking at would drop a list over
        // whatever is beneath it.
        if (combo.IsKeyboardFocusWithin)
        {
            var shouldBeOpen = items.Count > 0 && !string.IsNullOrEmpty(desired);
            if (combo.IsDropDownOpen != shouldBeOpen) { combo.IsDropDownOpen = shouldBeOpen; }
        }

        if (editor == null || caret < 0) { return; }

        var length = editor.Text?.Length ?? 0;
        editor.SelectionStart = Math.Min(caret, length);
        editor.SelectionLength = Math.Min(selectionLength, Math.Max(0, length - editor.SelectionStart));
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

    /// <summary>
    /// Picks up a work order number that was typed rather than chosen from the list, once the user has
    /// finished typing it. Cached by number, so leaving the box without having changed anything reports
    /// the answer already held instead of asking again.
    /// </summary>
    private void NgOdsWorkOrderCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        _ = CrossReferenceProposedMainForWorkOrderAsync(GetSelectedNgOdsWorkOrderNumberForProposedMain(), force: false);
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
            SetNgOdsStatus($"Selected {item.WorkOrderNumber} from NG_ODS by {selectedFrom}. Checking GIS for an existing proposed main...");
        }
        finally
        {
            _syncingNgOdsWorkOrderSelection = false;
        }

        // Outside the sync guard, because that flag is about the two combo boxes echoing each other and
        // this is neither of them. The method is no longer forced to Work Order driven here: which of
        // the two fits depends on whether GIS already holds the route, so the lookup sets it.
        _ = CrossReferenceProposedMainForWorkOrderAsync(item.WorkOrderNumber, force: false);
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
