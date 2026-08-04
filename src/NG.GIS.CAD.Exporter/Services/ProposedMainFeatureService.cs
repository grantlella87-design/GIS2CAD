using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

/// <summary>What came of adding hand drawn segments to the layer.</summary>
public sealed class ProposedMainAddResult
{
    public int Attempted { get; init; }
    public int Added { get; init; }

    /// <summary>Why it did not all land, or null when it did.</summary>
    public string? Error { get; init; }

    public bool Succeeded => Error == null && Added == Attempted && Attempted > 0;
}

/// <summary>One field on the proposed main layer that a user can be asked to fill in.</summary>
public sealed class ProposedMainField
{
    public string Name { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;

    /// <summary>Maximum length for a text field, or 0 where the service did not say.</summary>
    public int Length { get; init; }

    /// <summary>
    /// Whether GIS will reject the feature without a value. Read from the field's own nullable flag,
    /// so what the page insists on is what the service insists on rather than a list kept here.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// The values a coded domain allows, empty when the field is free text. Offered as a list so a
    /// field with a domain cannot be filled in with something GIS will refuse.
    /// </summary>
    public IReadOnlyList<ProposedMainCodedValue> CodedValues { get; init; } = Array.Empty<ProposedMainCodedValue>();

    public bool HasCodedValues => CodedValues.Count > 0;

    /// <summary>What to show as the field's label: its alias where it has one, its name otherwise.</summary>
    public string Display => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
}

/// <summary>One allowed value of a coded domain, with the label GIS shows for it.</summary>
public sealed record ProposedMainCodedValue(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One subtype of the layer, and the domains that apply to a feature of that subtype.
///
/// A subtype narrows what the rest of the fields are allowed to hold: a distribution main and a
/// service line are the same table with different rules about material, pressure and diameter. The
/// field's own domain is the general case; this is the one that actually applies once the kind of
/// feature is known, and it is usually the shorter and more useful list.
/// </summary>
public sealed class ProposedMainSubtype
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Coded values by field name, for the fields this subtype constrains.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ProposedMainCodedValue>> Domains { get; init; }
        = new Dictionary<string, IReadOnlyList<ProposedMainCodedValue>>(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

/// <summary>The layer's fields, its subtypes, and which field chooses between them.</summary>
public sealed class ProposedMainLayerSchema
{
    public IReadOnlyList<ProposedMainField> Fields { get; init; } = Array.Empty<ProposedMainField>();

    /// <summary>The field whose value picks the subtype, or empty when the layer has no subtypes.</summary>
    public string SubtypeFieldName { get; init; } = string.Empty;

    public IReadOnlyList<ProposedMainSubtype> Subtypes { get; init; } = Array.Empty<ProposedMainSubtype>();

    public bool HasSubtypes => Subtypes.Count > 0 && !string.IsNullOrWhiteSpace(SubtypeFieldName);

    /// <summary>The subtype a value selects, or null when it selects none.</summary>
    public ProposedMainSubtype? FindSubtype(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Subtypes.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The values a field may hold for a feature of this subtype, best list first.
    ///
    /// The subtype's own domain wins where it has one, because it is the narrower and more accurate
    /// answer. Falling back to the field's general domain matters as much: a field the subtype says
    /// nothing about still has whatever the field itself allows, and dropping to a free text box there
    /// would be offering less than is known.
    /// </summary>
    public IReadOnlyList<ProposedMainCodedValue> CodedValuesFor(ProposedMainField field, string? subtypeCode)
    {
        var subtype = FindSubtype(subtypeCode);
        if (subtype != null
            && subtype.Domains.TryGetValue(field.Name, out var narrowed)
            && narrowed.Count > 0)
        {
            return narrowed;
        }

        return field.CodedValues;
    }
}

public static class ProposedMainFeatureService
{
    private const string LayerUrl = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer/54";
    private const string QueryUrl = LayerUrl + "/query";
    private const string ApplyEditsUrl = LayerUrl + "/applyEdits";
    private static readonly HttpClient Http = new HttpClient();

    /// <summary>
    /// The fields a user can be asked to fill in for a new proposed main, read from the layer itself.
    ///
    /// Taken from the service rather than written down here, because which fields exist and which of
    /// them are mandatory is GIS's to decide and changes without this code being touched. A list kept
    /// here would be wrong the first time somebody added a field, and wrong silently.
    /// </summary>
    public static async Task<ProposedMainLayerSchema> GetLayerSchemaAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("ArcGIS access token is required to read the proposed main layer."); }

        var url = LayerUrl + "?f=json&token=" + Uri.EscapeDataString(accessToken);
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            throw new InvalidOperationException("ArcGIS proposed main schema query failed: " + message);
        }

        if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return new ProposedMainLayerSchema();
        }

        var editable = new List<ProposedMainField>();
        foreach (var field in fields.EnumerateArray())
        {
            var parsed = ToProposedMainField(field);
            if (parsed != null) { editable.Add(parsed); }
        }

        return new ProposedMainLayerSchema
        {
            Fields = editable,
            SubtypeFieldName = ReadSubtypeFieldName(doc.RootElement),
            Subtypes = ReadSubtypes(doc.RootElement)
        };
    }

    /// <summary>
    /// The field that picks the subtype. Services disagree on what to call it, so both spellings are
    /// read: subtypeField is the newer one and typeIdField the older, and a layer answering with only
    /// one of them is the ordinary case rather than a broken one.
    /// </summary>
    private static string ReadSubtypeFieldName(JsonElement root)
    {
        foreach (var name in new[] { "subtypeField", "typeIdField" })
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) { return text; }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The layer's subtypes and the domains each one imposes.
    ///
    /// A subtype's domains are the ones that actually apply to a feature of that kind, which is the
    /// list worth offering: a field with fifty values across the whole layer often has four for the
    /// kind of main being drawn.
    /// </summary>
    private static IReadOnlyList<ProposedMainSubtype> ReadSubtypes(JsonElement root)
    {
        if (!root.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProposedMainSubtype>();
        }

        var subtypes = new List<ProposedMainSubtype>();
        foreach (var type in types.EnumerateArray())
        {
            var code = type.TryGetProperty("id", out var id)
                ? (id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString())
                : null;
            if (string.IsNullOrEmpty(code)) { continue; }

            var name = type.TryGetProperty("name", out var n) ? n.GetString() ?? code : code;

            var domains = new Dictionary<string, IReadOnlyList<ProposedMainCodedValue>>(StringComparer.OrdinalIgnoreCase);
            if (type.TryGetProperty("domains", out var typeDomains) && typeDomains.ValueKind == JsonValueKind.Object)
            {
                foreach (var domain in typeDomains.EnumerateObject())
                {
                    var values = ReadCodedValuesFromDomain(domain.Value);
                    if (values.Count > 0) { domains[domain.Name] = values; }
                }
            }

            subtypes.Add(new ProposedMainSubtype { Code = code, Name = name, Domains = domains });
        }

        return subtypes;
    }

    /// <summary>
    /// A field a user can fill in, or null for one they cannot.
    ///
    /// Left out: the identity fields GIS maintains, the geometry columns, and anything the service
    /// marks as not editable. Offering those would be offering to type a value that is going to be
    /// discarded or refused.
    /// </summary>
    private static ProposedMainField? ToProposedMainField(JsonElement field)
    {
        var name = field.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(name)) { return null; }

        var type = field.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        if (type is "esriFieldTypeOID" or "esriFieldTypeGlobalID" or "esriFieldTypeGUID" or "esriFieldTypeGeometry")
        {
            return null;
        }

        if (name.StartsWith("SHAPE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OBJECTID", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GLOBALID", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (field.TryGetProperty("editable", out var editable)
            && editable.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        // Nullable absent is read as nullable. A service that does not say is not one insisting, and
        // making the page demand a value it was never told to demand would block a legitimate save.
        var required = field.TryGetProperty("nullable", out var nullable)
                       && nullable.ValueKind == JsonValueKind.False;

        return new ProposedMainField
        {
            Name = name,
            Alias = field.TryGetProperty("alias", out var a) ? a.GetString() ?? name : name,
            Type = type,
            Length = field.TryGetProperty("length", out var l) && l.TryGetInt32(out var length) ? length : 0,
            Required = required,
            CodedValues = ReadCodedValues(field)
        };
    }

    private static IReadOnlyList<ProposedMainCodedValue> ReadCodedValues(JsonElement field) =>
        field.TryGetProperty("domain", out var domain)
            ? ReadCodedValuesFromDomain(domain)
            : Array.Empty<ProposedMainCodedValue>();

    /// <summary>
    /// The allowed values of one domain, empty for a domain that is not a list. A range domain has
    /// bounds rather than choices, so there is nothing to put in a dropdown for it.
    /// </summary>
    private static IReadOnlyList<ProposedMainCodedValue> ReadCodedValuesFromDomain(JsonElement domain)
    {
        if (domain.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ProposedMainCodedValue>();
        }

        if (!domain.TryGetProperty("codedValues", out var coded) || coded.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProposedMainCodedValue>();
        }

        var values = new List<ProposedMainCodedValue>();
        foreach (var entry in coded.EnumerateArray())
        {
            var code = entry.TryGetProperty("code", out var c)
                ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString())
                : null;
            if (string.IsNullOrEmpty(code)) { continue; }

            var label = entry.TryGetProperty("name", out var nm) ? nm.GetString() ?? code : code;
            values.Add(new ProposedMainCodedValue(code, label));
        }

        return values;
    }

    /// <summary>
    /// Adds hand drawn proposed main segments to the GIS layer, and returns how many landed.
    ///
    /// One feature per segment rather than one multipart feature, so a segment can be found, edited or
    /// retired on its own afterwards, which is how they were drawn.
    ///
    /// This writes to GIS. It is the only thing in this application that does, so it is deliberately
    /// the last step of a page rather than something that happens while drawing: a user still moving
    /// points around has not decided anything yet.
    /// </summary>
    /// <param name="attributesPerGeometry">
    /// One set of attributes per geometry, in the same order. Per segment rather than shared, because
    /// each becomes its own feature and two segments of one corridor can differ in size or material.
    /// </param>
    public static async Task<ProposedMainAddResult> AddFeaturesAsync(
        IReadOnlyList<Geometry> geometries,
        IReadOnlyList<IReadOnlyDictionary<string, string>> attributesPerGeometry,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (geometries == null || geometries.Count == 0) { throw new ArgumentException("There are no segments to add.", nameof(geometries)); }
        if (attributesPerGeometry == null || attributesPerGeometry.Count != geometries.Count)
        {
            throw new ArgumentException("Every segment needs its own attributes.", nameof(attributesPerGeometry));
        }
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("ArcGIS access token is required to add proposed mains."); }

        // Written with a JSON writer rather than by joining strings. Attribute values are typed by a
        // user and a quote or a backslash in one of them would otherwise end the request early and
        // send something malformed to a service that is about to write to the database.
        var written = 0;
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();

            for (var i = 0; i < geometries.Count; i++)
            {
                var geometry = geometries[i];
                if (geometry == null || geometry.IsEmpty) { continue; }

                json.WriteStartObject();

                // The runtime's own JSON for the geometry, carried across as parsed structure so it is
                // never re-encoded or re-rounded on the way.
                using (var geometryJson = JsonDocument.Parse(geometry.ToJson()))
                {
                    json.WritePropertyName("geometry");
                    geometryJson.RootElement.WriteTo(json);
                }

                json.WriteStartObject("attributes");
                foreach (var pair in attributesPerGeometry[i])
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) { continue; }
                    json.WriteString(pair.Key, pair.Value ?? string.Empty);
                }
                json.WriteEndObject();

                json.WriteEndObject();
                written++;
            }

            json.WriteEndArray();
        }

        if (written == 0) { throw new ArgumentException("Every segment was empty.", nameof(geometries)); }
        var adds = Encoding.UTF8.GetString(buffer.ToArray());

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["adds"] = adds,

            // All or nothing. Half a run of segments in GIS is worse than none: the drawing would be
            // right, the layer would be part right, and nothing would say which segments made it.
            ["rollbackOnFailure"] = "true",
            ["token"] = accessToken
        });

        using var response = await Http.PostAsync(ApplyEditsUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return ReadAddResult(body, written);
    }

    /// <summary>
    /// Reads what applyEdits said. A 200 response is not a success here: a rejected edit comes back as
    /// an ordinary reply carrying success false against each row, so the rows have to be read.
    /// </summary>
    private static ProposedMainAddResult ReadAddResult(string body, int attempted)
    {
        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            return new ProposedMainAddResult { Attempted = attempted, Added = 0, Error = message ?? "the service refused the edit" };
        }

        var added = 0;
        string? failure = null;

        if (doc.RootElement.TryGetProperty("addResults", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in results.EnumerateArray())
            {
                var success = entry.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                if (success) { added++; continue; }

                if (failure == null && entry.TryGetProperty("error", out var rowError))
                {
                    failure = rowError.TryGetProperty("description", out var description)
                        ? description.GetString()
                        : rowError.ToString();
                }
            }
        }

        return new ProposedMainAddResult { Attempted = attempted, Added = added, Error = added == attempted ? null : failure ?? "the service did not say why" };
    }

    /// <summary>
    /// How many proposed main features the layer holds for a work order, without fetching any of them.
    ///
    /// Asked with returnCountOnly, so the service answers with a number rather than with geometry. That
    /// matters because this runs while the user is still choosing on page 1: the answer only has to
    /// decide which export method fits, and pulling every segment down to count them would make choosing
    /// a work order as slow as importing one.
    /// </summary>
    public static async Task<int> CountByWorkOrderAsync(string workOrderId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workOrderId)) { throw new ArgumentException("Work order number is required.", nameof(workOrderId)); }
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("ArcGIS access token is required to query proposed mains."); }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = BuildWorkOrderWhere(workOrderId),
            ["returnCountOnly"] = "true",
            ["token"] = accessToken
        });
        using var response = await Http.PostAsync(QueryUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var msg) ? msg.GetString() : error.ToString();
            throw new InvalidOperationException("ArcGIS proposed main count failed: " + message);
        }

        // A layer that will not answer returnCountOnly returns the features instead of a count rather
        // than refusing, so both shapes are read. Treating a missing count as zero would report "no
        // proposed main" for a work order that has one, which is the one wrong answer worth avoiding
        // here: it sends the user off to draw by hand over a route GIS already knows.
        if (doc.RootElement.TryGetProperty("count", out var count) && count.TryGetInt32(out var parsed)) { return parsed; }
        if (doc.RootElement.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            return features.GetArrayLength();
        }

        throw new InvalidOperationException("ArcGIS proposed main count returned neither a count nor features.");
    }

    /// <summary>The work order filter, with quotes escaped so a stray one cannot change the clause.</summary>
    private static string BuildWorkOrderWhere(string workOrderId) =>
        "workorderid = '" + workOrderId.Replace("'", "''") + "'";

    public static async Task<ProposedMainQueryResult> QueryByWorkOrderAsync(string workOrderId, string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workOrderId)) { throw new ArgumentException("Work order number is required.", nameof(workOrderId)); }
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("ArcGIS access token is required to query proposed mains."); }
        var symbol = await GetLayerSymbolAsync(accessToken, cancellationToken).ConfigureAwait(false);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = BuildWorkOrderWhere(workOrderId),
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

    /// <summary>
    /// The layer's own line symbol. Public so a hand drawn main can be shown in it too: a segment the
    /// user is adding to this layer should look like the layer it is being added to, not like a
    /// placeholder that happens to be red.
    /// </summary>
    public static async Task<ProposedMainRestSymbol> GetLayerSymbolAsync(string accessToken, CancellationToken cancellationToken = default)
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
