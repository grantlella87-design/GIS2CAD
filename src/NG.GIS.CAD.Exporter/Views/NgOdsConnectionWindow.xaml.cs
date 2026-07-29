using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.Views;

/// <summary>
/// Prompts for the NG_ODS database connection when none is configured, or when the configured one
/// stops working. The connection is tested before it is accepted, so the dialog cannot save a
/// connection string that does not connect.
/// </summary>
public partial class NgOdsConnectionWindow : Window
{
    public NgOdsConnectionWindow()
    {
        InitializeComponent();

        // Prefill from whatever is configured so the user only retypes what actually changed.
        var existing = NgOdsConnection.Parse(NgOdsConnection.TryGetConfiguredConnectionString());
        ServerTextBox.Text = existing.Server;
        DatabaseTextBox.Text = string.IsNullOrWhiteSpace(existing.Database) ? NgOdsConnection.DefaultDatabase : existing.Database;
        UserIdTextBox.Text = existing.UserId;

        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ServerTextBox.Text)) { ServerTextBox.Focus(); }
            else { PasswordBoxInput.Focus(); }
        };
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        await RunTestAsync(saveOnSuccess: false);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunTestAsync(saveOnSuccess: true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async Task RunTestAsync(bool saveOnSuccess)
    {
        var settings = new NgOdsConnectionSettings(
            ServerTextBox.Text?.Trim() ?? string.Empty,
            DatabaseTextBox.Text?.Trim() ?? string.Empty,
            UserIdTextBox.Text?.Trim() ?? string.Empty,
            PasswordBoxInput.Password ?? string.Empty);

        if (string.IsNullOrWhiteSpace(settings.Server) || string.IsNullOrWhiteSpace(settings.Database)
            || string.IsNullOrWhiteSpace(settings.UserId) || string.IsNullOrWhiteSpace(settings.Password))
        {
            SetStatus("Enter the server, database, user ID and password.", isError: true);
            return;
        }

        SetBusy(true);
        SetStatus("Connecting to " + settings.Server + "...", isError: false);
        try
        {
            var connectionString = NgOdsConnection.Build(settings);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var error = await NgOdsConnection.TestAsync(connectionString, timeout.Token);

            if (error != null)
            {
                SetStatus("Connection failed: " + Summarize(error), isError: true);
                return;
            }

            if (!saveOnSuccess)
            {
                SetStatus("Connection succeeded.", isError: false);
                return;
            }

            NgOdsConnection.Save(connectionString, RememberCheckBox.IsChecked == true);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Connection timed out after 60 seconds.", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus("Connection failed: " + ex.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        TestButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.Media.Brushes.DimGray;
    }

    /// <summary>PowerShell wraps SQL errors in several lines; the first one carries the reason.</summary>
    private static string Summarize(string error)
    {
        foreach (var line in error.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) { return trimmed; }
        }
        return error.Trim();
    }
}
