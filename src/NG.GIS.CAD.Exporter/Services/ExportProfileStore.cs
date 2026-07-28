using System.Text.Json;
using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.Services;

public sealed class ExportProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task<ExportProfile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var profile = await JsonSerializer.DeserializeAsync<ExportProfile>(stream, JsonOptions, cancellationToken);
        return profile ?? new ExportProfile();
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
