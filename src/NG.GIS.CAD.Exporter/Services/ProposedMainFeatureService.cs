using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Geometry;

namespace NG.GIS.CAD.Exporter.Services;

public sealed class ProposedMainRestSymbol
{
    public int R { get; init; } = 0;
    public int G { get; init; } = 90;
    public int B { get; init; } = 255;
    public int A { get; init; } = 255;
    public double Width { get; init; } = 4.0;
    public string Style { get; init; } = "esriSLSSolid";
}

public sealed class ProposedMainQueryResult
{
    public string WorkOrderId { get; init; } = string.Empty;
    public IReadOnlyList<Geometry> Geometries { get; init; } = Array.Empty<Geometry>();
    public Envelope? Extent { get; init; }
    public ProposedMainRestSymbol Symbol { get; init; } = new();
    public int FeatureCount => Geometries.Count;
}

public static class ProposedMainFeatureService
{
    private const string LayerUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer/54";
    private const string QueryUrl = LayerUrl + "/query";
    private static readonly HttpClient Http = new HttpClient();

    public static async Task<ProposedMainQueryResult> QueryByWorkOrderAsync(string workOrderId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workOrderId)) { throw new ArgumentException("Work order number is required.", nameof(workOrderId)); }
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("ArcGIS access token is required to query proposed mains."); }
        var symbol = await GetLayerSymbolAsync(accessToken, cancellationToken).ConfigureAwait(false);
        var where = "workorderid = '" + workOrderId.Replace("'", "''") + "'";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = where,
            ["outFields"] = "objectid,workorderid",
            ["returnGeometry"] = "true",
            ["returnTrueCurves"] = "true",
            ["outSR"] = "3857",
            ["token"] = accessToken
        });
        using var response = await Http.PostAsync(QueryUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            throw new InvalidOperationException("ArcGIS proposed main query failed: " + message);
        }
        var geometries = new List<Geometry>();
        if (doc.RootElement.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("geometry", out var geometryElement)) { continue; }
                var geometry = Geometry.FromJson(geometryElement.GetRawText());
                if (geometry == null || geometry.IsEmpty) { continue; }
                var projected = geometry.SpatialReference?.Wkid == 3857 ? geometry : GeometryEngine.Project(geometry, SpatialReferences.WebMercator);
                if (projected != null && !projected.IsEmpty) { geometries.Add(projected); }
            }
        }
        Envelope? extent = null;
        foreach (var geometry in geometries)
        {
            if (extent == null) { extent = geometry.Extent; }
            else
            {
                extent = new Envelope(
                    Math.Min(extent.XMin, geometry.Extent.XMin),
                    Math.Min(extent.YMin, geometry.Extent.YMin),
                    Math.Max(extent.XMax, geometry.Extent.XMax),
                    Math.Max(extent.YMax, geometry.Extent.YMax),
                    SpatialReferences.WebMercator);
            }
        }
        return new ProposedMainQueryResult { WorkOrderId = workOrderId, Geometries = geometries, Extent = extent, Symbol = symbol };
    }

    private static async Task<ProposedMainRestSymbol> GetLayerSymbolAsync(string accessToken, CancellationToken cancellationToken)
    {
        var url = LayerUrl + "?f=json&token=" + Uri.EscapeDataString(accessToken);
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            throw new InvalidOperationException("ArcGIS layer metadata query failed: " + message);
        }
        var symbol = ResolveSymbolElement(doc.RootElement);
        if (symbol.ValueKind == JsonValueKind.Undefined) { return new ProposedMainRestSymbol(); }
        return ParseSymbol(symbol);
    }

    private static JsonElement ResolveSymbolElement(JsonElement root)
    {
        if (!root.TryGetProperty("drawingInfo", out var drawingInfo)) { return default; }
        if (!drawingInfo.TryGetProperty("renderer", out var renderer)) { return default; }
        if (renderer.TryGetProperty("symbol", out var directSymbol)) { return directSymbol; }
        if (renderer.TryGetProperty("defaultSymbol", out var defaultSymbol)) { return defaultSymbol; }
        if (renderer.TryGetProperty("uniqueValueInfos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in infos.EnumerateArray())
            {
                if (info.TryGetProperty("symbol", out var infoSymbol)) { return infoSymbol; }
            }
        }
        return default;
    }

    private static ProposedMainRestSymbol ParseSymbol(JsonElement symbol)
    {
        int r = 0, g = 90, b = 255, a = 255;
        double width = 4.0;
        string style = "esriSLSSolid";
        if (symbol.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.Array)
        {
            var values = new List<int>();
            foreach (var c in color.EnumerateArray()) { if (c.TryGetInt32(out var v)) { values.Add(v); } }
            if (values.Count >= 3) { r = values[0]; g = values[1]; b = values[2]; }
            if (values.Count >= 4) { a = values[3]; }
        }
        if (symbol.TryGetProperty("width", out var widthElement) && widthElement.TryGetDouble(out var parsedWidth) && parsedWidth > 0)
        {
            width = Math.Max(2.0, parsedWidth);
        }
        if (symbol.TryGetProperty("style", out var styleElement) && styleElement.ValueKind == JsonValueKind.String)
        {
            var parsedStyle = styleElement.GetString();
            if (!string.IsNullOrWhiteSpace(parsedStyle)) { style = parsedStyle; }
        }
        return new ProposedMainRestSymbol { R = r, G = g, B = b, A = a, Width = width, Style = style };
    }
}
