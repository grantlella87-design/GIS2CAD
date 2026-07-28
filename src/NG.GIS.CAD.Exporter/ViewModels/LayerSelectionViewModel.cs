using NG.GIS.CAD.Exporter.Services;
namespace NG.GIS.CAD.Exporter.ViewModels;
public sealed class LayerSelectionViewModel : ObservableObject
{
    private readonly LayerSelectionViewState _state;
    private bool _enabled;
    public LayerSelectionViewModel(LayerSelectionViewState state)
    {
        _state = state;
        _enabled = state.Enabled;
        Fields = state.Fields.Select(f => new FieldSelectionViewModel(f)).ToList();
    }
    public string Name => _state.Layer.Name;
    public string GeometryType => _state.Layer.GeometryType;
    public string Url => _state.Layer.Url;
    public IReadOnlyList<FieldSelectionViewModel> Fields { get; }
    public LayerSelectionViewState State => _state;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _state.Enabled = value;
            }
        }
    }
}
public sealed class FieldSelectionViewModel : ObservableObject
{
    private readonly FieldSelectionViewState _state;
    private bool _selected;
    public FieldSelectionViewModel(FieldSelectionViewState state)
    {
        _state = state;
        _selected = state.Selected;
    }
    public string Name => _state.Field.Name;
    public string Alias => _state.Field.Alias;
    public string Type => _state.Field.Type;
    public bool Required => _state.Field.Required;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (Required)
            {
                value = true;
            }
            if (SetProperty(ref _selected, value))
            {
                _state.Selected = value;
            }
        }
    }
}
