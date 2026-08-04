using System;
using System.Collections.Generic;
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
    string FullAddress,
    string Address,
    string City,
    string State,
    string PostalCode)
{
    /// <summary>
    /// The address as one line, which is what the map is placed by and what goes in the Address box.
    ///
    /// Built by the query rather than joined up here. The parts are carried alongside it for anything
    /// that wants them separately, but the one line is the query's answer and not a second opinion
    /// assembled from the pieces, which could differ from what the query said.
    /// </summary>
    public string SearchText => FullAddress;

    public bool HasAnything => !string.IsNullOrWhiteSpace(FullAddress);
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
FOR JSON PATH
OPTION (RECOMPILE)
"@
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 0
    $cmd.CommandText = $sql

    # SQL Server writes the JSON, and this only joins it up. What was here before built a
    # PowerShell object per row -- sixteen property inserts each -- and then walked the whole
    # collection through ConvertTo-Json, and on a table of this size that work dwarfed the query
    # it was formatting. The query was never the slow part.
    #
    # FOR JSON returns one column split across as many rows as it needs, so the pieces are
    # concatenated in order. A StringBuilder rather than string addition, which would copy the
    # whole document again for every chunk.
    $reader = $cmd.ExecuteReader()
    $json = New-Object System.Text.StringBuilder
    while ($reader.Read()) {
        [void]$json.Append($reader.GetString(0))
    }
    $reader.Close()
    $json.ToString()
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
    /// and filtered locally, and an address is wanted for exactly one of them at a time; carrying five
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
    LTRIM(RTRIM(
        CONCAT(
            ISNULL(CAST(wosa.[FORMATTEDADDRESS] AS NVARCHAR(4000)), ''),
            CASE
                WHEN NULLIF(LTRIM(RTRIM(CAST(wosa.[CITY] AS NVARCHAR(4000)))), '') IS NOT NULL
                    THEN ', ' + LTRIM(RTRIM(CAST(wosa.[CITY] AS NVARCHAR(4000))))
                ELSE ''
            END,
            CASE
                WHEN NULLIF(LTRIM(RTRIM(CAST(wosa.[STATEPROVINCE] AS NVARCHAR(100)))), '') IS NOT NULL
                    THEN ', ' + LTRIM(RTRIM(CAST(wosa.[STATEPROVINCE] AS NVARCHAR(100))))
                ELSE ''
            END,
            CASE
                WHEN NULLIF(LTRIM(RTRIM(CAST(wosa.[POSTALCODE] AS NVARCHAR(100)))), '') IS NOT NULL
                    THEN ' ' + LTRIM(RTRIM(CAST(wosa.[POSTALCODE] AS NVARCHAR(100))))
                ELSE ''
            END
        )
    )) AS [FullAddress],
    ISNULL(CAST(wosa.[FORMATTEDADDRESS] AS NVARCHAR(4000)), '') AS [Address],
    ISNULL(CAST(wosa.[CITY] AS NVARCHAR(4000)), '') AS [City],
    ISNULL(CAST(wosa.[STATEPROVINCE] AS NVARCHAR(100)), '') AS [State],
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
            FullAddress = [string]$reader['FullAddress']
            Address = [string]$reader['Address']
            City = [string]$reader['City']
            State = [string]$reader['State']
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
