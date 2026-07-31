using System.Globalization;
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

    /// <summary>
    /// Puts the query geometry on a request: the buffer polygon where the export is scoped to one, the
    /// bounding box otherwise.
    ///
    /// This is the single place the import scope is decided, so the shape drawn into the drawing and the
    /// shape the service filtered by cannot come apart. Asking for a polygon costs a longer request and
    /// a little more work at the service, and it is what stops a diagonal corridor dragging in the whole
    /// rectangle around it.
    ///
    /// Coordinates are written invariant. A service parses "1.5" and not "1,5", and the separator would
    /// otherwise follow whatever regional settings the workstation happens to have.
    /// </summary>
    private static void ApplyBoundary(
        Dictionary<string, string> parameters, ExportExtent extent, ExportBoundary? boundary)
    {
        if (boundary is { HasRings: true })
        {
            parameters["geometryType"] = "esriGeometryPolygon";
            parameters["geometry"] = BuildPolygonJson(boundary);
            parameters["inSR"] = boundary.Wkid.ToString(CultureInfo.InvariantCulture);
            return;
        }

        // A box, either because that is what the method produces or because there was no buffer to use.
        var wkid = boundary?.Wkid ?? extent.Wkid;
        var minX = boundary?.XMin ?? extent.XMin;
        var minY = boundary?.YMin ?? extent.YMin;
        var maxX = boundary?.XMax ?? extent.XMax;
        var maxY = boundary?.YMax ?? extent.YMax;

        parameters["geometryType"] = "esriGeometryEnvelope";
        parameters["geometry"] = string.Join(",", new[]
        {
            minX.ToString("R", CultureInfo.InvariantCulture),
            minY.ToString("R", CultureInfo.InvariantCulture),
            maxX.ToString("R", CultureInfo.InvariantCulture),
            maxY.ToString("R", CultureInfo.InvariantCulture)
        });
        parameters["inSR"] = wkid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The buffer as ArcGIS polygon JSON. Built by hand rather than round-tripped through the runtime's
    /// geometry so this stays usable from the query path without a runtime geometry in hand.
    /// </summary>
    private static string BuildPolygonJson(ExportBoundary boundary)
    {
        var json = new StringBuilder("{\"rings\":[");
        var firstRing = true;

        foreach (var ring in boundary.Rings)
        {
            if (ring.Count < 3) { continue; }
            if (!firstRing) { json.Append(','); }
            firstRing = false;

            json.Append('[');
            for (var i = 0; i < ring.Count; i++)
            {
                if (i > 0) { json.Append(','); }
                AppendPoint(json, ring[i]);
            }

            // ArcGIS wants the ring closed explicitly; the rest of this codebase carries them open. The
            // tolerance matches the one the rings were opened with, so a ring is not closed twice.
            if (Math.Abs(ring[0].X - ring[^1].X) > 1e-9 || Math.Abs(ring[0].Y - ring[^1].Y) > 1e-9)
            {
                json.Append(',');
                AppendPoint(json, ring[0]);
            }
            json.Append(']');
        }

        json.Append("],\"spatialReference\":{\"wkid\":")
            .Append(boundary.Wkid.ToString(CultureInfo.InvariantCulture))
            .Append("}}");
        return json.ToString();

        static void AppendPoint(StringBuilder builder, ExportVertex vertex) =>
            builder.Append('[')
                   .Append(vertex.X.ToString("R", CultureInfo.InvariantCulture))
                   .Append(',')
                   .Append(vertex.Y.ToString("R", CultureInfo.InvariantCulture))
                   .Append(']');
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
    public async Task<int> QueryCountAsync(
        string layerUrl, ExportExtent extent, CancellationToken cancellationToken, ExportBoundary? boundary = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = "1=1",
            ["returnCountOnly"] = "true",
            ["spatialRel"] = "esriSpatialRelIntersects"
        };
        ApplyBoundary(parameters, extent, boundary);
        var json = await PostJsonAsync(layerUrl + "/query", parameters, cancellationToken);
        if (json.RootElement.TryGetProperty("count", out var count))
        {
            return count.GetInt32();
        }
        return 0;
    }
    /// <summary>
    /// Fetches the features to export from one layer.
    ///
    /// The service is asked for <paramref name="outWkid"/> directly, so the coordinates that come back
    /// are already drawing coordinates and nothing downstream has to reproject. Reprojecting locally
    /// would mean picking a datum transformation, which the service is better placed to do.
    ///
    /// Paged, because a service caps how many records one query returns and quietly says so through
    /// exceededTransferLimit rather than by failing. Reading one page and stopping would silently drop
    /// features the user asked to export.
    /// </summary>
    public async Task<IReadOnlyList<ExportFeature>> QueryFeaturesAsync(
        string layerUrl, ExportExtent extent, IReadOnlyList<string> fields, int outWkid,
        CancellationToken cancellationToken, ExportBoundary? boundary = null)
    {
        var features = new List<ExportFeature>();
        var offset = 0;
        const int pageSize = 1000;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new Dictionary<string, string>
            {
                ["f"] = "json",
                ["where"] = "1=1",
                ["outSR"] = outWkid.ToString(),
                ["spatialRel"] = "esriSpatialRelIntersects",
                ["outFields"] = fields.Count == 0 ? "*" : string.Join(",", fields),
                ["returnGeometry"] = "true",
                ["resultOffset"] = offset.ToString(),
                ["resultRecordCount"] = pageSize.ToString()
            };
            ApplyBoundary(parameters, extent, boundary);

            using var json = await PostJsonAsync(layerUrl.TrimEnd('/') + "/query", parameters, cancellationToken);
            var root = json.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var text) ? text.GetString() : "unknown error";
                throw new InvalidOperationException("The service rejected the feature query: " + message);
            }

            var pageCount = 0;
            if (root.TryGetProperty("features", out var featureArray) && featureArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var featureElement in featureArray.EnumerateArray())
                {
                    pageCount++;
                    var feature = ReadFeature(featureElement);
                    if (feature.Parts.Count > 0) { features.Add(feature); }
                }
            }

            var more = root.TryGetProperty("exceededTransferLimit", out var exceeded)
                && exceeded.ValueKind == JsonValueKind.True;

            // A service that ignores resultOffset would return the same page forever, so paging stops
            // unless the page was full as well as flagged.
            if (!more || pageCount < pageSize) { break; }
            offset += pageCount;
        }

        return features;
    }

    private static ExportFeature ReadFeature(JsonElement featureElement)
    {
        var feature = new ExportFeature();

        if (featureElement.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
        {
            foreach (var attribute in attributes.EnumerateObject())
            {
                feature.Attributes[attribute.Name] = attribute.Value.ValueKind switch
                {
                    JsonValueKind.String => attribute.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null => string.Empty,
                    _ => attribute.Value.ToString()
                };
            }
        }

        if (!featureElement.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
        {
            return feature;
        }

        // A point carries x and y directly; everything else carries paths or rings.
        if (geometry.TryGetProperty("x", out var x) && geometry.TryGetProperty("y", out var y)
            && x.TryGetDouble(out var pointX) && y.TryGetDouble(out var pointY))
        {
            feature.Parts.Add(new List<ExportVertex> { new ExportVertex(pointX, pointY) });
            return feature;
        }

        AddParts(feature, geometry, "paths");
        AddParts(feature, geometry, "rings");
        return feature;
    }

    private static void AddParts(ExportFeature feature, JsonElement geometry, string propertyName)
    {
        if (!geometry.TryGetProperty(propertyName, out var partArray) || partArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var partElement in partArray.EnumerateArray())
        {
            if (partElement.ValueKind != JsonValueKind.Array) { continue; }

            var part = new List<ExportVertex>();
            foreach (var vertexElement in partElement.EnumerateArray())
            {
                if (vertexElement.ValueKind != JsonValueKind.Array) { continue; }
                var values = vertexElement.EnumerateArray().ToArray();
                if (values.Length < 2) { continue; }
                if (values[0].TryGetDouble(out var vx) && values[1].TryGetDouble(out var vy))
                {
                    part.Add(new ExportVertex(vx, vy));
                }
            }
            if (part.Count > 0) { feature.Parts.Add(part); }
        }
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
