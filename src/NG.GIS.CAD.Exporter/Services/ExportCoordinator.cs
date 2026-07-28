using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.Services;

public sealed class ExportCoordinator
{
    private readonly ArcGisRestClient _arcGisRestClient;
    private readonly UserSettingsStore _userSettingsStore;

    public ExportCoordinator(ArcGisRestClient arcGisRestClient, UserSettingsStore userSettingsStore)
    {
        _arcGisRestClient = arcGisRestClient;
        _userSettingsStore = userSettingsStore;
    }

    public async Task SaveSelectionsAsync(IEnumerable<LayerSelectionViewState> layers, string profilePath, CancellationToken cancellationToken)
    {
        var settings = new UserExportSettings { LastProfilePath = profilePath };
        foreach (var layer in layers)
        {
            settings.Layers[layer.Layer.Url] = new LayerUserSettings
            {
                Enabled = layer.Enabled,
                SelectedFields = layer.Fields.Where(f => f.Selected).Select(f => f.Field.Name).ToList()
            };
        }
        await _userSettingsStore.SaveAsync(settings, cancellationToken);
    }

    public Task RunExportAsync(IEnumerable<LayerSelectionViewState> layers, CancellationToken cancellationToken)
    {
        var selected = layers.Where(l => l.Enabled).ToList();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("No layers are selected for export.");
        }
        return Task.CompletedTask;
    }
}

public sealed class LayerSelectionViewState
{
    public LayerSelectionViewState(LayerMetadata layer)
    {
        Layer = layer;
        Fields = layer.Fields.Select(f => new FieldSelectionViewState(f) { Selected = f.DefaultSelected }).ToList();
    }

    public LayerMetadata Layer { get; }
    public bool Enabled { get; set; }
    public List<FieldSelectionViewState> Fields { get; }
}

public sealed class FieldSelectionViewState
{
    public FieldSelectionViewState(FieldMetadata field)
    {
        Field = field;
    }

    public FieldMetadata Field { get; }
    public bool Selected { get; set; }
}
