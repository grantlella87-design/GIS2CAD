namespace NG.GIS.CAD.Exporter.Models;
public sealed class ExportProfile
{
    public string ProfileName { get; set; } = "National Grid GIS CAD Export";
    public string PortalRootUrl { get; set; } = string.Empty;
    public int DefaultOutputSpatialReferenceWkid { get; set; } = 2249;
    public RequestOptions Request { get; set; } = new();
    public List<ServiceProfile> Services { get; set; } = new();

    /// <summary>
    /// Visibility of the extent page map layers, keyed by layer path. Top level layers are keyed
    /// by name and sublayers by "parent/sublayer". Layers absent from this map fall back to the
    /// visibility the web map itself was authored with.
    /// </summary>
    public Dictionary<string, bool> MapLayerVisibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Properties present in the profile file that this model does not declare, captured so that
    /// saving the profile round-trips them instead of dropping them. The profile carries keys such
    /// as page2WebMapItemId that nothing binds to yet.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; set; } = new();
}
public sealed class RequestOptions
{
    public int ObjectIdBatchSize { get; set; } = 500;
    public int MaxConcurrentLayers { get; set; } = 4;
    public bool ReturnZ { get; set; }
    public bool ReturnM { get; set; }
    public int? GeometryPrecision { get; set; }
}
public sealed class ServiceProfile
{
    public string ServiceName { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
