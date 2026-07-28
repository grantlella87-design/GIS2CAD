namespace NG.GIS.CAD.Exporter.Models;
public sealed class ExportProfile
{
    public string ProfileName { get; set; } = "National Grid GIS CAD Export";
    public string PortalRootUrl { get; set; } = string.Empty;
    public int DefaultOutputSpatialReferenceWkid { get; set; } = 2249;
    public RequestOptions Request { get; set; } = new();
    public List<ServiceProfile> Services { get; set; } = new();
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
