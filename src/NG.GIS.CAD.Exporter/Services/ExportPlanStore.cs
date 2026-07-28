using NG.GIS.CAD.Exporter.Models;
namespace NG.GIS.CAD.Exporter.Services;
public sealed class ExportPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public async Task<string> SaveLatestAsync(ExportPlan plan, CancellationToken cancellationToken)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, "NationalGrid", "GisCadExporter");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "export-plan-latest.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, plan, JsonOptions, cancellationToken);
        return path;
    }
}
