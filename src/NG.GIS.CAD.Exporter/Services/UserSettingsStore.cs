using System.Text.Json;
using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.Services;

public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _settingsPath;

    public UserSettingsStore()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(local, "NationalGrid", "GisCadExporter");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "user-settings.json");
    }

    public async Task<UserExportSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return new UserExportSettings();
        }
        await using var stream = File.OpenRead(_settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<UserExportSettings>(stream, JsonOptions, cancellationToken);
        return settings ?? new UserExportSettings();
    }

    public async Task SaveAsync(UserExportSettings settings, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}
