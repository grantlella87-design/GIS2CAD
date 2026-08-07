using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NG.GIS.CAD.Exporter.Services;

/// <summary>One symbol a layer draws with, and the value that picks it.</summary>
public sealed class GisPaletteSymbol
{
    /// <summary>What to show beside the swatch. The subtype or class name the service gave it.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// The value of the field the layer is drawn by, for the feature this symbol stands for. Written
    /// into the new feature so GIS draws it the same way the palette did.
    /// </summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// The swatch as the service supplied it: a PNG, base64 encoded, from an esriPMS picture marker.
    /// Null for a symbol the service described geometrically rather than as a picture, which is drawn
    /// from its colour instead.
    /// </summary>
    public byte[]? ImageData { get; init; }

    /// <summary>The symbol's colour, used to draw a swatch when the service supplied no picture.</summary>
    public int R { get; init; } = 0;
    public int G { get; init; } = 90;
    public int B { get; init; } = 255;
    public int A { get; init; } = 255;

    /// <summary>Point size the service draws this at, so a placed feature is the size GIS would draw.</summary>
    public double Size { get; init; } = 10;
}

/// <summary>One layer's worth of palette: what it is called, where it lives, and what it draws with.</summary>
public sealed class GisSymbolLayer
{
    public string Name { get; init; } = string.Empty;

    public string LayerUrl { get; init; } = string.Empty;

    /// <summary>
    /// The field the layer is drawn by, which is the field a placed feature has to carry for GIS to
    /// draw it the same way. Empty when the layer draws everything the same, in which case there is
    /// nothing to write.
    /// </summary>
    public string DrawnByFieldName { get; init; } = string.Empty;

    public IReadOnlyList<GisPaletteSymbol> Symbols { get; init; } = Array.Empty<GisPaletteSymbol>();
}

/// <summary>
/// Reads what a layer draws with, so the symbols a user picks from are the layer's own rather than a
/// set drawn here to look like them.
///
/// The alternative was a hand made palette kept in step with GIS by hand. It would be wrong the first
/// time somebody added a subtype, and wrong silently: the user would pick the closest thing on offer
/// and the feature would go up as something else.
/// </summary>
public static class GisSymbolPaletteService
{
    private static readonly HttpClient Http = new HttpClient();

    /// <summary>
    /// Reads one layer's renderer into a palette. The layer name comes from the service too, so the
    /// panel is labelled the way the portal labels it.
    /// </summary>
    public static async Task<GisSymbolLayer> LoadAsync(
        string layerUrl, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(layerUrl)) { throw new ArgumentException("A layer URL is required.", nameof(layerUrl)); }
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("ArcGIS access token is required to read a layer's symbols."); }

        var url = layerUrl.TrimEnd('/') + "?f=json&token=" + Uri.EscapeDataString(accessToken);
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            throw new InvalidOperationException("ArcGIS layer symbol query failed: " + message);
        }

        var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? layerUrl
            : layerUrl;

        var symbols = new List<GisPaletteSymbol>();
        var drawnBy = string.Empty;

        if (root.TryGetProperty("drawingInfo", out var drawingInfo)
            && drawingInfo.TryGetProperty("renderer", out var renderer))
        {
            drawnBy = ReadDrawnByFieldName(renderer);
            ReadRendererSymbols(renderer, symbols);
        }

        // Subtype names beat renderer class names where both exist. A renderer labels its classes for
        // the cartographer; the subtype list is what the field the feature carries actually means, and
        // that is the list a user is choosing from here.
        ApplySubtypeNames(root, drawnBy, symbols);

        return new GisSymbolLayer
        {
            Name = name,
            LayerUrl = layerUrl.TrimEnd('/'),
            DrawnByFieldName = drawnBy,
            Symbols = symbols
        };
    }

    private static string ReadDrawnByFieldName(JsonElement renderer)
    {
        if (renderer.TryGetProperty("field1", out var field1) && field1.ValueKind == JsonValueKind.String)
        {
            return field1.GetString() ?? string.Empty;
        }
        if (renderer.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String)
        {
            return field.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static void ReadRendererSymbols(JsonElement renderer, List<GisPaletteSymbol> symbols)
    {
        // Drawn by value: one symbol per class, which is the case this panel exists for.
        if (renderer.TryGetProperty("uniqueValueInfos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in infos.EnumerateArray())
            {
                if (!info.TryGetProperty("symbol", out var symbol)) { continue; }

                var value = info.TryGetProperty("value", out var valueElement) ? ReadValue(valueElement) : string.Empty;
                var label = info.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String
                    ? labelElement.GetString() ?? value
                    : value;

                symbols.Add(ParseSymbol(symbol, label, value));
            }

            if (symbols.Count > 0) { return; }
        }

        // Drawn all the same: one symbol, and nothing to choose between.
        if (renderer.TryGetProperty("symbol", out var single))
        {
            symbols.Add(ParseSymbol(single, "Default", string.Empty));
        }
    }

    /// <summary>
    /// Relabels each symbol with the subtype name for its value, where the layer has subtypes and is
    /// drawn by the subtype field.
    /// </summary>
    private static void ApplySubtypeNames(JsonElement root, string drawnBy, List<GisPaletteSymbol> symbols)
    {
        if (string.IsNullOrWhiteSpace(drawnBy)) { return; }
        if (!root.TryGetProperty("subtypeField", out var subtypeField) || subtypeField.ValueKind != JsonValueKind.String) { return; }
        if (!string.Equals(subtypeField.GetString(), drawnBy, StringComparison.OrdinalIgnoreCase)) { return; }
        if (!root.TryGetProperty("subtypes", out var subtypes) || subtypes.ValueKind != JsonValueKind.Array) { return; }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subtype in subtypes.EnumerateArray())
        {
            if (!subtype.TryGetProperty("code", out var code)) { continue; }
            if (!subtype.TryGetProperty("name", out var subtypeName) || subtypeName.ValueKind != JsonValueKind.String) { continue; }

            names[ReadValue(code)] = subtypeName.GetString() ?? string.Empty;
        }

        for (var i = 0; i < symbols.Count; i++)
        {
            if (!names.TryGetValue(symbols[i].Value, out var subtypeName)) { continue; }
            if (string.IsNullOrWhiteSpace(subtypeName)) { continue; }

            symbols[i] = new GisPaletteSymbol
            {
                Label = subtypeName,
                Value = symbols[i].Value,
                ImageData = symbols[i].ImageData,
                R = symbols[i].R,
                G = symbols[i].G,
                B = symbols[i].B,
                A = symbols[i].A,
                Size = symbols[i].Size
            };
        }
    }

    private static GisPaletteSymbol ParseSymbol(JsonElement symbol, string label, string value)
    {
        byte[]? imageData = null;
        if (symbol.TryGetProperty("imageData", out var image) && image.ValueKind == JsonValueKind.String)
        {
            var encoded = image.GetString();
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                try { imageData = Convert.FromBase64String(encoded); }
                catch (FormatException) { imageData = null; }
            }
        }

        int r = 0, g = 90, b = 255, a = 255;
        if (symbol.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.Array)
        {
            var values = new List<int>();
            foreach (var c in color.EnumerateArray()) { if (c.TryGetInt32(out var v)) { values.Add(v); } }
            if (values.Count >= 3) { r = values[0]; g = values[1]; b = values[2]; }
            if (values.Count >= 4) { a = values[3]; }
        }

        var size = 10.0;
        if (symbol.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetDouble(out var parsedSize) && parsedSize > 0)
        {
            size = parsedSize;
        }

        return new GisPaletteSymbol
        {
            Label = string.IsNullOrWhiteSpace(label) ? value : label,
            Value = value,
            ImageData = imageData,
            R = r,
            G = g,
            B = b,
            A = a,
            Size = size
        };
    }

    /// <summary>
    /// A renderer's value comes back as a number or a string depending on the field it draws by, and
    /// both have to end up as the same text so a symbol and a subtype can be matched to one another.
    /// </summary>
    private static string ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.ToString(),
        _ => element.ToString()
    };

    /// <summary>
    /// Adds one point feature to a layer, for a symbol placed from the palette.
    ///
    /// Its own call rather than the proposed main's, which is bound to the main layer and to lines. The
    /// shape of the request is the same; the layer, the geometry and the attributes are not.
    /// </summary>
    public static async Task<ProposedMainAddResult> AddPointAsync(
        string layerUrl,
        double x,
        double y,
        int wkid,
        IReadOnlyDictionary<string, string> attributes,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) { return new ProposedMainAddResult { Attempted = 1, Error = "No ArcGIS access token." }; }

        var feature = new StringBuilder();
        feature.Append("[{\"geometry\":{\"x\":").Append(x.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Append(",\"y\":").Append(y.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Append(",\"spatialReference\":{\"wkid\":").Append(wkid.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Append("}},\"attributes\":{");

        var first = true;
        foreach (var pair in attributes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) { continue; }
            if (!first) { feature.Append(','); }
            first = false;
            feature.Append(JsonSerializer.Serialize(pair.Key)).Append(':').Append(JsonSerializer.Serialize(pair.Value));
        }
        feature.Append("}}]");

        var form = new List<KeyValuePair<string, string>>
        {
            new("f", "json"),
            new("token", accessToken),
            new("rollbackOnFailure", "true"),
            new("adds", feature.ToString())
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(layerUrl.TrimEnd('/') + "/applyEdits", content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new ProposedMainAddResult { Attempted = 1, Error = "HTTP " + (int)response.StatusCode + ": " + body };
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            return new ProposedMainAddResult { Attempted = 1, Error = message };
        }

        if (!doc.RootElement.TryGetProperty("addResults", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return new ProposedMainAddResult { Attempted = 1, Error = "The service did not say what it did with the feature." };
        }

        foreach (var result in results.EnumerateArray())
        {
            var ok = result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
            if (ok) { return new ProposedMainAddResult { Attempted = 1, Added = 1 }; }

            var reason = result.TryGetProperty("error", out var resultError) && resultError.TryGetProperty("description", out var description)
                ? description.GetString()
                : "the service refused it without saying why";
            return new ProposedMainAddResult { Attempted = 1, Error = reason };
        }

        return new ProposedMainAddResult { Attempted = 1, Error = "The service returned no result for the feature." };
    }
}
