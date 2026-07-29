using System.Text.Json;
using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.Services;

public sealed class ExportProfileStore
{
    // CamelCase matches the casing the profile files already use. Reads stay case insensitive, and
    // the naming policy does not apply to dictionary keys, so layer paths are written verbatim.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<ExportProfile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var profile = await JsonSerializer.DeserializeAsync<ExportProfile>(stream, JsonOptions, cancellationToken);
        return profile ?? new ExportProfile();
    }

    /// <summary>
    /// Writes the profile back to disk through a temporary file, so an interrupted save cannot
    /// leave a partially written profile in place of a good one.
    /// </summary>
    public async Task SaveAsync(ExportProfile profile, string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) { Directory.CreateDirectory(directory); }

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, profile, JsonOptions, cancellationToken);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public string GetDefaultProfilePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configDirectory = Path.Combine(local, "NationalGrid", "GisCadExporter");
        Directory.CreateDirectory(configDirectory);
        var profilePath = Path.Combine(configDirectory, "ng-gis-export-profile.json");

        if (!File.Exists(profilePath))
        {
            File.WriteAllText(profilePath, """
{
  "profileName": "National Grid GIS CAD Export Starter",
  "portalRootUrl": "https://gis.nationalgrid.com",
  "defaultOutputSpatialReferenceWkid": 2249,
  "request": {
    "objectIdBatchSize": 500,
    "maxConcurrentLayers": 4,
    "returnZ": false,
    "returnM": false,
    "geometryPrecision": null
  },
  "services": [
    {
      "serviceName": "Material_View_MA",
      "serviceUrl": "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer",
      "enabled": true
    },
    {
      "serviceName": "Landbase_MA",
      "serviceUrl": "https://gis.nationalgrid.com/arcgis/rest/services/MA/Landbase_MA/MapServer",
      "enabled": true
    },
    {
      "serviceName": "GasNetwork_NY",
      "serviceUrl": "https://gis.nationalgrid.com/un/rest/services/GasNetwork_NY/FeatureServer",
      "enabled": false
    }
  ]
}
""");
        }

        return profilePath;
    }
}
