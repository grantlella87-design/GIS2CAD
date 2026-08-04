using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NG.GIS.CAD.Exporter.Services;

public sealed record NgOdsWorkOrderItem(
    string NgJurisdiction,
    string NgOpCo,
    string NgOpCoDescription,
    string NgFundProj,
    string NgFundingProjectDescription,
    string WorkOrderNumber,
    string WorkOrderName,
    string WoClass,
    string WoClassDescription,
    string WorkType,
    string WTypeDesc,
    string Status,
    string StatusDescription,
    string NgPpWoType,
    string NgPpWoTypeDescription,
    string NgServTerritory)
{
    public string WorkOrderNumberDisplay => WorkOrderNumber ?? string.Empty;
    public string WorkOrderNameDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorkOrderName)) { return WorkOrderNumber ?? string.Empty; }
            if (string.IsNullOrWhiteSpace(WorkOrderNumber)) { return WorkOrderName; }
            return WorkOrderName + " | " + WorkOrderNumber;
        }
    }
    public string DetailDisplay => string.Join(" | ", new[]
    {
        NgJurisdiction ?? string.Empty,
        string.Join(" ", new[] { NgOpCo, NgOpCoDescription }).Trim(),
        string.Join(" ", new[] { NgFundProj, NgFundingProjectDescription }).Trim(),
        string.Join(" - ", new[] { WorkOrderNumber, WorkOrderName }).Trim(' ', '-'),
        string.Join(" ", new[] { WoClass, WoClassDescription }).Trim(),
        string.Join(" ", new[] { WorkType, WTypeDesc }).Trim(),
        string.Join(" ", new[] { Status, StatusDescription }).Trim(),
        string.Join(" ", new[] { NgPpWoType, NgPpWoTypeDescription }).Trim(),
        NgServTerritory ?? string.Empty
    });
    public override string ToString() => DetailDisplay;
}

/// <summary>
/// The work order's service address, for placing a job on the map when GIS has no proposed main to
/// place it by.
/// </summary>
public sealed record NgOdsWorkOrderAddress(
    string LongitudeX,
    string LatitudeY,
    string FormattedAddress,
    string City,
    string StateProvince,
    string PostalCode)
{
    /// <summary>
    /// The coordinates as numbers, or null when the row carried none. Parsed invariantly: these come
    /// back as text from the query, and a comma decimal separator would turn a longitude into nothing
    /// and put the job on Null Island.
    /// </summary>
    public (double Longitude, double Latitude)? Coordinates
    {
        get
        {
            if (!double.TryParse(LongitudeX, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)) { return null; }
            if (!double.TryParse(LatitudeY, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)) { return null; }

            // Both zero is the default a row gets when nobody filled it in rather than a place, and it
            // is in the Gulf of Guinea. Out of range values are equally not somewhere to fly to.
            if (Math.Abs(longitude) < 1e-9 && Math.Abs(latitude) < 1e-9) { return null; }
            if (Math.Abs(longitude) > 180 || Math.Abs(latitude) > 90) { return null; }

            return (longitude, latitude);
        }
    }

    /// <summary>The address as one line, for geocoding and for saying where the map went.</summary>
    public string SearchText => string.Join(", ", new[] { FormattedAddress, City, StateProvince, PostalCode }
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool HasAnything => Coordinates != null || !string.IsNullOrWhiteSpace(SearchText);
}

public static class NgOdsWorkOrderLookup
{
    private static Task<IReadOnlyList<NgOdsWorkOrderItem>>? _prefetched;
    private static readonly object PrefetchGate = new();

    /// <summary>
    /// Starts the work order query immediately, before the window exists. The query runs in its own
    /// process, so it proceeds alongside portal sign-in, the AutoCAD drawing reads, window
    /// construction and profile loading instead of queueing behind them. Does nothing when no
    /// connection is configured, since that path needs a prompt and therefore a window.
    /// </summary>
    public static void StartPrefetch()
    {
        if (!NgOdsConnection.IsConfigured) { return; }

        lock (PrefetchGate)
        {
            if (_prefetched != null) { return; }

            var task = LoadAllAsync(CancellationToken.None);

            // Observe a failure here so a prefetch nobody collects cannot resurface later as an
            // unobserved task exception. The real error is still reported when the task is awaited.
            _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _prefetched = task;
        }
    }

    /// <summary>
    /// Hands over the prefetched query if one was started, and clears it, so a later reload runs a
    /// fresh query rather than replaying a stale result or a stale failure.
    /// </summary>
    public static Task<IReadOnlyList<NgOdsWorkOrderItem>>? TakePrefetched()
    {
        lock (PrefetchGate)
        {
            var task = _prefetched;
            _prefetched = null;
            return task;
        }
    }

    private static string ResolveConnectionString()
    {
        return NgOdsConnection.TryGetConfiguredConnectionString()
            ?? throw new InvalidOperationException(
                "The NG_ODS connection is not configured. Enter the database connection details when prompted, " +
                "or set the " + NgOdsConnection.ConnectionStringVariable + " environment variable.");
    }

    private const string LookupScript = """
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
$connStr = $env:NGGISCAD_ODS_CONN
if ([string]::IsNullOrWhiteSpace($connStr)) { throw 'NGGISCAD_ODS_CONN was not provided.' }
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
try {
    $sql = @"
WITH fp_dedup AS
(
    SELECT
        fp.[ng_fpnum],
        MAX(fp.[ng_fpdesc]) AS [ng_fpdesc]
    FROM [NG_ODS].[MX].[NG_FUNDPROJ] fp
    GROUP BY
        fp.[ng_fpnum]
),
wt_dedup AS
(
    SELECT
        wt.[worktype],
        MAX(wt.[wtypedesc]) AS [wtypedesc]
    FROM [NG_ODS].[MX].[WORKTYPE] wt
    GROUP BY
        wt.[worktype]
),
pp_dedup AS
(
    SELECT
        pp.[ng_pp_work_order_type],
        MAX(pp.[NG_PP_WO_TYPE_DESCRIPTION]) AS [NG_PP_WO_TYPE_DESCRIPTION]
    FROM [NG_ODS].[MX].[NG_PP_WO_TYPE] pp
    GROUP BY
        pp.[ng_pp_work_order_type]
)
SELECT
    CASE
        WHEN wo.[ng_opco] IN ('5330', '5340') THEN 'MA'
        ELSE wo.[ng_jurisdiction]
    END AS [NgJurisdiction],
    ISNULL(CAST(wo.[ng_opco] AS NVARCHAR(100)), '') AS [NgOpCo],
    ISNULL(CAST(wo.[ng_opco_description] AS NVARCHAR(4000)), '') AS [NgOpCoDescription],
    ISNULL(CAST(wo.[ng_fundproj] AS NVARCHAR(100)), '') AS [NgFundProj],
    ISNULL(CAST(fp.[ng_fpdesc] AS NVARCHAR(4000)), '') AS [NgFundingProjectDescription],
    ISNULL(CAST(wo.[wonum] AS NVARCHAR(100)), '') AS [WorkOrderNumber],
    ISNULL(CAST(wo.[description] AS NVARCHAR(4000)), '') AS [WorkOrderName],
    ISNULL(CAST(wo.[woclass] AS NVARCHAR(100)), '') AS [WoClass],
    ISNULL(CAST(wo.[woclass_description] AS NVARCHAR(4000)), '') AS [WoClassDescription],
    ISNULL(CAST(wo.[worktype] AS NVARCHAR(100)), '') AS [WorkType],
    ISNULL(CAST(wt.[wtypedesc] AS NVARCHAR(4000)), '') AS [WTypeDesc],
    ISNULL(CAST(wo.[status] AS NVARCHAR(100)), '') AS [Status],
    ISNULL(CAST(wo.[status_description] AS NVARCHAR(4000)), '') AS [StatusDescription],
    ISNULL(CAST(wo.[NG_PPWOTYPE] AS NVARCHAR(100)), '') AS [NgPpWoType],
    ISNULL(CAST(pp.[NG_PP_WO_TYPE_DESCRIPTION] AS NVARCHAR(4000)), '') AS [NgPpWoTypeDescription],
    ISNULL(CAST(wo.[ng_servterritory] AS NVARCHAR(100)), '') AS [NgServTerritory]
FROM [NG_ODS].[MX].[WorkOrder] wo
LEFT JOIN fp_dedup fp
    ON fp.[ng_fpnum] = wo.[ng_fundproj]
LEFT JOIN wt_dedup wt
    ON wt.[worktype] = wo.[worktype]
LEFT JOIN pp_dedup pp
    ON pp.[ng_pp_work_order_type] = wo.[NG_PPWOTYPE]
WHERE
    wo.[ng_opco] IN ('5330', '5340')
    AND wo.[NG_PPWOTYPE] BETWEEN 400 AND 499
    AND wo.[ng_fundproj] IS NOT NULL
    AND wo.[ng_fundproj] <> ''
    AND wo.[wonum] IS NOT NULL
    AND wo.[wonum] <> ''
    AND (
        wo.[status] IS NULL
        OR wo.[status] <> 'CAN'
    )
    AND (
        wt.[wtypedesc] IS NULL
        OR wt.[wtypedesc] NOT IN (
            'Preventive Maintenance',
            'Inspection',
            'Corrective Maintenance'
        )
    )
ORDER BY
    wo.[wonum] DESC
OPTION (RECOMPILE)
"@
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 0
    $cmd.CommandText = $sql
    $reader = $cmd.ExecuteReader()
    $items = New-Object System.Collections.Generic.List[object]
    while ($reader.Read()) {
        $items.Add([pscustomobject]@{
            NgJurisdiction = [string]$reader['NgJurisdiction']
            NgOpCo = [string]$reader['NgOpCo']
            NgOpCoDescription = [string]$reader['NgOpCoDescription']
            NgFundProj = [string]$reader['NgFundProj']
            NgFundingProjectDescription = [string]$reader['NgFundingProjectDescription']
            WorkOrderNumber = [string]$reader['WorkOrderNumber']
            WorkOrderName = [string]$reader['WorkOrderName']
            WoClass = [string]$reader['WoClass']
            WoClassDescription = [string]$reader['WoClassDescription']
            WorkType = [string]$reader['WorkType']
            WTypeDesc = [string]$reader['WTypeDesc']
            Status = [string]$reader['Status']
            StatusDescription = [string]$reader['StatusDescription']
            NgPpWoType = [string]$reader['NgPpWoType']
            NgPpWoTypeDescription = [string]$reader['NgPpWoTypeDescription']
            NgServTerritory = [string]$reader['NgServTerritory']
        })
    }
    $reader.Close()
    $items | ConvertTo-Json -Depth 4 -Compress
}
finally {
    $conn.Close()
}
""";

    public static async Task<IReadOnlyList<NgOdsWorkOrderItem>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = ResolveConnectionString();
        var output = await NgOdsConnection
            .RunPowerShellAsync(LookupScript, connectionString, "NGGisCadExporter_WorkOrderFullLoad_", cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output)) { return Array.Empty<NgOdsWorkOrderItem>(); }
        output = output.Trim();

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (output.StartsWith("[", StringComparison.Ordinal))
        {
            return JsonSerializer.Deserialize<List<NgOdsWorkOrderItem>>(output, options) ?? new List<NgOdsWorkOrderItem>();
        }
        var single = JsonSerializer.Deserialize<NgOdsWorkOrderItem>(output, options);
        return single == null ? Array.Empty<NgOdsWorkOrderItem>() : new[] { single };
    }

    /// <summary>
    /// The service address held against one work order, or null when there is none.
    ///
    /// Its own query rather than a join onto the list above. The list is thousands of rows loaded once
    /// and filtered locally, and an address is wanted for exactly one of them at a time; carrying six
    /// more columns on every row to answer a question asked about one would be paying for the whole
    /// table to serve a single lookup.
    /// </summary>
    public static async Task<NgOdsWorkOrderAddress?> LoadAddressAsync(
        string workOrderNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workOrderNumber)) { return null; }

        var connectionString = ResolveConnectionString();

        // Passed as an environment variable and bound as a parameter inside the script, so a work
        // order number never becomes part of the SQL text. It arrives from a dropdown today, but a
        // value that is pasted or typed is one that can carry a quote.
        var environment = new Dictionary<string, string> { ["NGGISCAD_WONUM"] = workOrderNumber };

        var output = await NgOdsConnection
            .RunPowerShellAsync(AddressScript, connectionString, "NGGisCadExporter_WorkOrderAddress_", cancellationToken, environment)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output)) { return null; }
        output = output.Trim();
        if (output.StartsWith("[", StringComparison.Ordinal)) { output = output.Trim('[', ']').Trim(); }
        if (string.IsNullOrWhiteSpace(output)) { return null; }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<NgOdsWorkOrderAddress>(output, options);
    }

    private const string AddressScript = """
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
$connStr = $env:NGGISCAD_ODS_CONN
if ([string]::IsNullOrWhiteSpace($connStr)) { throw 'NGGISCAD_ODS_CONN was not provided.' }
$wonum = $env:NGGISCAD_WONUM
if ([string]::IsNullOrWhiteSpace($wonum)) { throw 'NGGISCAD_WONUM was not provided.' }
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
try {
    $sql = @"
SELECT TOP (1)
    ISNULL(CAST(wosa.[LONGITUDEX] AS NVARCHAR(100)), '') AS [LongitudeX],
    ISNULL(CAST(wosa.[LATITUDEY] AS NVARCHAR(100)), '') AS [LatitudeY],
    ISNULL(CAST(wosa.[FORMATTEDADDRESS] AS NVARCHAR(4000)), '') AS [FormattedAddress],
    ISNULL(CAST(wosa.[CITY] AS NVARCHAR(4000)), '') AS [City],
    ISNULL(CAST(wosa.[STATEPROVINCE] AS NVARCHAR(100)), '') AS [StateProvince],
    ISNULL(CAST(wosa.[POSTALCODE] AS NVARCHAR(100)), '') AS [PostalCode]
FROM [NG_ODS].[MX].[WOSERVICEADDRESS] wosa
WHERE
    wosa.[wonum] = @wonum
"@
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 0
    $cmd.CommandText = $sql
    $null = $cmd.Parameters.AddWithValue('@wonum', $wonum)
    $reader = $cmd.ExecuteReader()
    $items = New-Object System.Collections.Generic.List[object]
    while ($reader.Read()) {
        $items.Add([pscustomobject]@{
            LongitudeX = [string]$reader['LongitudeX']
            LatitudeY = [string]$reader['LatitudeY']
            FormattedAddress = [string]$reader['FormattedAddress']
            City = [string]$reader['City']
            StateProvince = [string]$reader['StateProvince']
            PostalCode = [string]$reader['PostalCode']
        })
    }
    $reader.Close()
    $items | ConvertTo-Json -Depth 4 -Compress
}
finally {
    $conn.Close()
}
""";
}
