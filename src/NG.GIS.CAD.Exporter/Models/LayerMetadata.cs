namespace NG.GIS.CAD.Exporter.Models;

public sealed class LayerMetadata
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string GeometryType { get; set; } = string.Empty;
    public string ObjectIdField { get; set; } = "OBJECTID";
    public int MaxRecordCount { get; set; } = 1000;
    public List<FieldMetadata> Fields { get; set; } = new();
}

public sealed class FieldMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool DefaultSelected { get; set; }
}
