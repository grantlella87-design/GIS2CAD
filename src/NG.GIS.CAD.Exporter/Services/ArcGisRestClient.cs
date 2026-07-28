using NG.GIS.CAD.Exporter.Models;
namespace NG.GIS.CAD.Exporter.Services;
public sealed class ArcGisRestClient
{
    private readonly HttpClient _httpClient;
    private readonly ArcGisTokenProvider _tokenProvider;
    public ArcGisRestClient(HttpClient httpClient, ArcGisTokenProvider tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }
    public async Task<IReadOnlyList<LayerMetadata>> LoadServiceLayersAsync(string serviceUrl, CancellationToken cancellationToken)
    {
        var layers = new List<LayerMetadata>();
        try
        {
            var json = await GetJsonAsync(serviceUrl, cancellationToken);
            var root = json.RootElement;
            if (root.TryGetProperty("layers", out var layerArray))
            {
                foreach (var layer in layerArray.EnumerateArray())
                {
                    var id = layer.TryGetProperty("id", out var idValue) ? idValue.GetInt32() : -1;
                    var name = layer.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "Layer " + id : "Layer " + id;
                    if (id < 0)
                    {
                        continue;
                    }
                    var layerUrl = serviceUrl.TrimEnd('/') + "/" + id;
                    var metadata = await LoadLayerMetadataAsync(layerUrl, id, name, cancellationToken);
                    layers.Add(metadata);
                }
            }
            else if (root.TryGetProperty("fields", out _))
            {
                var id = ParseLayerId(serviceUrl);
                var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "Layer " + id : "Layer " + id;
                layers.Add(ToLayerMetadata(root, serviceUrl, id, name));
            }
        }
        catch (Exception ex)
        {
            Log("Failed loading " + serviceUrl + ": " + ex);
        }
        return layers;
    }
    public async Task<int> QueryCountAsync(string layerUrl, ExportExtent extent, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = "1=1",
            ["returnCountOnly"] = "true",
            ["geometryType"] = "esriGeometryEnvelope",
            ["geometry"] = $"{extent.XMin},{extent.YMin},{extent.XMax},{extent.YMax}",
            ["inSR"] = extent.Wkid.ToString(),
            ["spatialRel"] = "esriSpatialRelIntersects"
        };
        var json = await PostJsonAsync(layerUrl + "/query", parameters, cancellationToken);
        if (json.RootElement.TryGetProperty("count", out var count))
        {
            return count.GetInt32();
        }
        return 0;
    }
    public async Task<IReadOnlyList<string>> QueryDistinctWorkOrderIdsAsync(string proposedLayerUrl, CancellationToken cancellationToken)
    {
        var results = new List<string>();

        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = "workorderid IS NOT NULL",
            ["outFields"] = "workorderid",
            ["returnGeometry"] = "false",
            ["returnDistinctValues"] = "true",
            ["orderByFields"] = "workorderid ASC"
        };

        try
        {
            var json = await PostJsonAsync(proposedLayerUrl.TrimEnd('/') + "/query", parameters, cancellationToken);

            if (json.RootElement.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateArray())
                {
                    if (!feature.TryGetProperty("attributes", out var attributes))
                    {
                        continue;
                    }

                    if (!attributes.TryGetProperty("workorderid", out var value))
                    {
                        continue;
                    }

                    var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(text.Trim());
                    }
                }
            }
        }
        catch
        {
            var fallback = new Dictionary<string, string>
            {
                ["f"] = "json",
                ["where"] = "workorderid IS NOT NULL",
                ["outFields"] = "workorderid",
                ["returnGeometry"] = "false",
                ["resultRecordCount"] = "2000"
            };

            var json = await PostJsonAsync(proposedLayerUrl.TrimEnd('/') + "/query", fallback, cancellationToken);

            if (json.RootElement.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateArray())
                {
                    if (!feature.TryGetProperty("attributes", out var attributes))
                    {
                        continue;
                    }

                    if (!attributes.TryGetProperty("workorderid", out var value))
                    {
                        continue;
                    }

                    var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(text.Trim());
                    }
                }
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    }
    public async Task<ExportExtent> ResolveWorkOrderExtentAsync(string proposedLayerUrl, string workOrderId, double paddingFeet, int wkid, CancellationToken cancellationToken)
    {
        var where = "workorderid = '" + workOrderId.Replace("'", "''") + "'";
        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = where,
            ["outFields"] = "OBJECTID,workorderid,GLOBALID",
            ["returnGeometry"] = "true",
            ["outSR"] = wkid.ToString()
        };
        var json = await PostJsonAsync(proposedLayerUrl.TrimEnd('/') + "/query", parameters, cancellationToken);
        if (!json.RootElement.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("No proposed pipeline features were found for work order " + workOrderId + ".");
        }
        var xmin = double.PositiveInfinity;
        var ymin = double.PositiveInfinity;
        var xmax = double.NegativeInfinity;
        var ymax = double.NegativeInfinity;
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry))
            {
                continue;
            }
            AccumulateGeometryBounds(geometry, ref xmin, ref ymin, ref xmax, ref ymax);
        }
        if (double.IsInfinity(xmin))
        {
            throw new InvalidOperationException("Work order features had no readable geometry.");
        }
        return new ExportExtent
        {
            Mode = "WorkOrder",
            WorkOrderId = workOrderId,
            XMin = xmin - paddingFeet,
            YMin = ymin - paddingFeet,
            XMax = xmax + paddingFeet,
            YMax = ymax + paddingFeet,
            Wkid = wkid,
            PaddingFeet = paddingFeet
        };
    }
    private async Task<LayerMetadata> LoadLayerMetadataAsync(string layerUrl, int id, string name, CancellationToken cancellationToken)
    {
        var json = await GetJsonAsync(layerUrl, cancellationToken);
        return ToLayerMetadata(json.RootElement, layerUrl, id, name);
    }
    private static LayerMetadata ToLayerMetadata(JsonElement root, string layerUrl, int id, string name)
    {
        var metadata = new LayerMetadata
        {
            Id = id,
            Name = name,
            Url = layerUrl,
            GeometryType = root.TryGetProperty("geometryType", out var gt) ? gt.GetString() ?? string.Empty : string.Empty,
            ObjectIdField = root.TryGetProperty("objectIdField", out var oid) ? oid.GetString() ?? "OBJECTID" : "OBJECTID",
            MaxRecordCount = root.TryGetProperty("maxRecordCount", out var max) ? max.GetInt32() : 1000
        };
        if (root.TryGetProperty("fields", out var fields) &&
            fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fields.EnumerateArray())
            {
                metadata.Fields.Add(ToFieldMetadata(field, metadata.ObjectIdField));
            }
        }
        return metadata;
    }
    private static FieldMetadata ToFieldMetadata(JsonElement field, string objectIdField)
    {
        var name = field.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var type = field.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var required = name.Equals(objectIdField, StringComparison.OrdinalIgnoreCase) || name.Equals("GLOBALID", StringComparison.OrdinalIgnoreCase);
        return new FieldMetadata
        {
            Name = name,
            Alias = field.TryGetProperty("alias", out var a) ? a.GetString() ?? name : name,
            Type = type,
            Required = required,
            DefaultSelected = required || name.Contains("workorder", StringComparison.OrdinalIgnoreCase)
        };
    }
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidAccessTokenAsync(cancellationToken);
        var separator = url.Contains("?") ? "&" : "?";
        var fullUrl = url + separator + "f=json&token=" + Uri.EscapeDataString(token);
        using var response = await _httpClient.GetAsync(fullUrl, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(body);
    }
    private async Task<JsonDocument> PostJsonAsync(string url, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidAccessTokenAsync(cancellationToken);
        parameters["token"] = token;
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(body);
    }
    private static void AccumulateGeometryBounds(JsonElement geometry, ref double xmin, ref double ymin, ref double xmax, ref double ymax)
    {
        if (geometry.TryGetProperty("x", out var xValue) && geometry.TryGetProperty("y", out var yValue))
        {
            AddPoint(xValue.GetDouble(), yValue.GetDouble(), ref xmin, ref ymin, ref xmax, ref ymax);
        }
        if (geometry.TryGetProperty("paths", out var paths))
        {
            foreach (var path in paths.EnumerateArray())
            {
                foreach (var point in path.EnumerateArray())
                {
                    if (point.GetArrayLength() >= 2)
                    {
                        AddPoint(point[0].GetDouble(), point[1].GetDouble(), ref xmin, ref ymin, ref xmax, ref ymax);
                    }
                }
            }
        }
        if (geometry.TryGetProperty("rings", out var rings))
        {
            foreach (var ring in rings.EnumerateArray())
            {
                foreach (var point in ring.EnumerateArray())
                {
                    if (point.GetArrayLength() >= 2)
                    {
                        AddPoint(point[0].GetDouble(), point[1].GetDouble(), ref xmin, ref ymin, ref xmax, ref ymax);
                    }
                }
            }
        }
    }
    private static void AddPoint(double x, double y, ref double xmin, ref double ymin, ref double xmax, ref double ymax)
    {
        xmin = Math.Min(xmin, x);
        ymin = Math.Min(ymin, y);
        xmax = Math.Max(xmax, x);
        ymax = Math.Max(ymax, y);
    }
    private static int ParseLayerId(string url)
    {
        var slash = url.LastIndexOf('/');
        if (slash >= 0 && int.TryParse(url.Substring(slash + 1), out var id))
        {
            return id;
        }
        return -1;
    }
    private static void Log(string message)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "NationalGrid", "GisCadExporter");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "nggis-diagnostics.log");
            File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }
}


