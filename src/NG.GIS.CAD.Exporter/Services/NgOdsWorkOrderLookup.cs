using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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

public static class NgOdsWorkOrderLookup
{
    private const string ConnectionStringVariable = "NGGISCAD_ODS_CONN";

    private static string ResolveConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(ConnectionStringVariable, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(ConnectionStringVariable, EnvironmentVariableTarget.Machine);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The NG_ODS connection string is not configured. Set the " + ConnectionStringVariable +
                " environment variable to the NG_ODS SQL connection string before running the work order lookup.");
        }

        return connectionString;
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
        var scriptPath = Path.Combine(Path.GetTempPath(), "NGGisCadExporter_WorkOrderFullLoad_" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(scriptPath, LookupScript, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
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

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell work order lookup process.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0) { throw new InvalidOperationException("NG_ODS work order lookup failed: " + error); }
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
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
}
