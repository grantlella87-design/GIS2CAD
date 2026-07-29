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
    /// Extra services layered onto the extent page map on top of the web map's own layers. These
    /// are display sources for page 2 and are separate from <see cref="Services"/>, which drives
    /// the layer and field selection used by the export itself.
    /// </summary>
    public List<MapDataSource> MapDataSources { get; set; } = new();

    /// <summary>Where the SharePoint CAD template browser looks, and which app registration it signs in with.</summary>
    public SharePointTemplateSettings SharePointTemplates { get; set; } = new();

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
/// <summary>
/// Settings for the SharePoint CAD template browser. None of these are secrets: the client id is
/// Microsoft's published public client for Graph command line tools, which lets the plug-in request
/// delegated access as the signed-in user without registering its own application. Replace it with
/// your own registration's id here if you would rather the consent show your app's name.
/// </summary>
public sealed class SharePointTemplateSettings
{
    public string ClientId { get; set; } = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    public string TenantId { get; set; } = "f98a6a53-25f3-4212-901c-c7787fcd3495";
    public string DriveId { get; set; } = "b!oQf4lQnCX0qt-hTuSFmXrP4QVHfYL_1Mo26SJGoClkz_SltDsJ3rT4gTLzMV1xCP";
    public string FolderPath { get; set; } = "Boston Cape Gas Dist Eng Design/CAD Template/Template - BOSTON TEAM";
}

/// <summary>A service the user added to the extent page map.</summary>
public sealed class MapDataSource
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
