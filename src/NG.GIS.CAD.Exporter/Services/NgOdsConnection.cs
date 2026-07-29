using System.Diagnostics;
using System.Text;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>The parts of an NG_ODS connection the user is asked for.</summary>
public sealed record NgOdsConnectionSettings(string Server, string Database, string UserId, string Password);

/// <summary>
/// Owns the NG_ODS connection string: where it is stored, how it is built from the parts the user
/// supplies, and how to check that it actually connects before it is saved.
/// </summary>
public static class NgOdsConnection
{
    public const string ConnectionStringVariable = "NGGISCAD_ODS_CONN";
    public const string DefaultDatabase = "NG_ODS";

    /// <summary>
    /// The tables the work order query reads. The connection test checks SELECT on each of them,
    /// because signing in successfully says nothing about whether the account can read these.
    /// </summary>
    private static readonly string[] RequiredTables =
    {
        "NG_ODS.MX.WorkOrder",
        "NG_ODS.MX.NG_FUNDPROJ",
        "NG_ODS.MX.WORKTYPE",
        "NG_ODS.MX.NG_PP_WO_TYPE"
    };

    private const string TestScript = """
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
$connStr = $env:NGGISCAD_ODS_CONN
if ([string]::IsNullOrWhiteSpace($connStr)) { throw 'NGGISCAD_ODS_CONN was not provided.' }
$tables = $env:NGGISCAD_ODS_TABLES -split ';' | Where-Object { $_ -ne '' }
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
try {
    $denied = @()
    foreach ($table in $tables) {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT HAS_PERMS_BY_NAME(@name, 'OBJECT', 'SELECT')"
        [void]$cmd.Parameters.AddWithValue('@name', $table)
        $granted = $cmd.ExecuteScalar()
        # NULL means the object is missing or invisible to this login; both are failures here.
        if ($null -eq $granted -or [System.DBNull]::Value.Equals($granted) -or [int]$granted -ne 1) {
            $denied += $table
        }
    }
    if ($denied.Count -gt 0) {
        throw ("Signed in successfully, but this account cannot read: " + ($denied -join ', ') + ". Ask for SELECT permission on those tables.")
    }
    'OK'
}
finally {
    $conn.Close()
}
""";

    /// <summary>
    /// The configured connection string, or null when nothing is set. Process scope is checked first
    /// so a connection entered this session is used immediately; the user and machine scopes are read
    /// straight from the registry, so a value set elsewhere is picked up without restarting AutoCAD.
    /// </summary>
    public static string? TryGetConfiguredConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionStringVariable, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(ConnectionStringVariable, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(ConnectionStringVariable, EnvironmentVariableTarget.Machine);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool IsConfigured => TryGetConfiguredConnectionString() != null;

    /// <summary>
    /// Stores the connection string. Process scope always, so the current AutoCAD session can use it
    /// straight away, and user scope as well when the user asked for it to be remembered.
    /// </summary>
    public static void Save(string connectionString, bool persistForUser)
    {
        Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString, EnvironmentVariableTarget.Process);
        if (persistForUser)
        {
            Environment.SetEnvironmentVariable(ConnectionStringVariable, connectionString, EnvironmentVariableTarget.User);
        }
    }

    public static string Build(NgOdsConnectionSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append("Data Source=").Append(Quote(settings.Server)).Append(';');
        builder.Append("Initial Catalog=").Append(Quote(settings.Database)).Append(';');
        builder.Append("Persist Security Info=True;");
        builder.Append("User ID=").Append(Quote(settings.UserId)).Append(';');
        builder.Append("Password=").Append(Quote(settings.Password)).Append(';');
        builder.Append("Pooling=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;");
        builder.Append("Application Name=\"NG GIS CAD Exporter\";");
        return builder.ToString();
    }

    /// <summary>
    /// Reads server, database and user out of an existing connection string so the prompt can be
    /// prefilled. The password is deliberately not returned; the user retypes it.
    /// </summary>
    public static NgOdsConnectionSettings Parse(string? connectionString)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (connectionString ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) { continue; }
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }
            values[key] = value;
        }

        values.TryGetValue("Data Source", out var server);
        values.TryGetValue("Initial Catalog", out var database);
        values.TryGetValue("User ID", out var userId);

        return new NgOdsConnectionSettings(
            server ?? string.Empty,
            string.IsNullOrWhiteSpace(database) ? DefaultDatabase : database,
            userId ?? string.Empty,
            string.Empty);
    }

    /// <summary>
    /// Opens the connection and runs SELECT 1. Returns null when the connection works, or the error
    /// text when it does not.
    /// </summary>
    public static async Task<string?> TestAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            var extraEnvironment = new Dictionary<string, string>
            {
                ["NGGISCAD_ODS_TABLES"] = string.Join(";", RequiredTables)
            };
            await RunPowerShellAsync(TestScript, connectionString, "NGGisCadExporter_ConnectionTest_", cancellationToken, extraEnvironment).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return SummarizeError(ex.Message);
        }
    }

    /// <summary>
    /// Whether re-entering the connection details could plausibly fix this error. A permission error
    /// is about what the account is granted, not who it is, so prompting for the password again would
    /// only ask the user to retype something that is already correct.
    /// </summary>
    public static bool LooksLikeCredentialProblem(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return false; }

        if (message.Contains("permission was denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot read:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return message.Contains("Login failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not associated with a trusted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not configured", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the real message out of the PowerShell wrapper. A SQL error arrives as
    /// Exception calling "ExecuteReader" with "0" argument(s): "&lt;the part that matters&gt;",
    /// followed by a stack of script positions nobody needs to read.
    /// </summary>
    public static string SummarizeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return "Unknown error."; }

        var text = message.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            "argument\\(s\\):\\s*\"(?<inner>.*?)\"\\s*(\\r?\\n|$)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (match.Success) { return match.Groups["inner"].Value.Trim(); }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) { return trimmed; }
        }
        return text;
    }

    /// <summary>
    /// Runs a PowerShell script with the connection string supplied through the environment rather
    /// than the command line, so it never appears in the process arguments.
    /// </summary>
    public static async Task<string> RunPowerShellAsync(string script, string connectionString, string tempFilePrefix, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), tempFilePrefix + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.Environment[ConnectionStringVariable] = connectionString;
            if (extraEnvironment != null)
            {
                foreach (var pair in extraEnvironment) { psi.Environment[pair.Key] = pair.Value; }
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the PowerShell process for NG_ODS.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelling only stops the wait. Without this the query keeps running to completion
                // in a process nobody is listening to, holding its SQL connection open.
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } } catch { }
                throw;
            }
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The NG_ODS command failed." : error.Trim());
            }
            return output;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Quotes a connection string value when it contains a character that would otherwise end the
    /// key or the value.
    /// </summary>
    private static string Quote(string value)
    {
        value ??= string.Empty;
        var needsQuoting = value.Contains(';') || value.Contains('"') || value.Contains('\'') || value.Contains('=')
            || value != value.Trim();
        if (!needsQuoting) { return value; }

        if (!value.Contains('"')) { return "\"" + value + "\""; }
        if (!value.Contains('\'')) { return "'" + value + "'"; }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
