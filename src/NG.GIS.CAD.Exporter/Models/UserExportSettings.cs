namespace NG.GIS.CAD.Exporter.Models;

public sealed class UserExportSettings
{
    public string LastProfilePath { get; set; } = string.Empty;
    public Dictionary<string, LayerUserSettings> Layers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LayerUserSettings
{
    public bool Enabled { get; set; }
    public List<string> SelectedFields { get; set; } = new();
}
