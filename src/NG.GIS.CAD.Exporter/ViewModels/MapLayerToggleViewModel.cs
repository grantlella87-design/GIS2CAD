namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One node in the extent page map layer tree. Nodes are created by the view once the web map has
/// loaded, one per operational layer and one per sublayer beneath it. Nodes with children are
/// rendered as expandable groups.
/// </summary>
public sealed class MapLayerToggleViewModel : ObservableObject
{
    private bool _isVisible;
    private bool _isExpanded = true;

    public MapLayerToggleViewModel(string path, string name, bool isVisible)
    {
        Path = path;
        Name = name;
        _isVisible = isVisible;
    }

    /// <summary>Stable key used to persist this node: "Layer" or "Layer/Sublayer".</summary>
    public string Path { get; }

    public string Name { get; }

    public ObservableCollection<MapLayerToggleViewModel> Children { get; } = new();

    /// <summary>
    /// Whether the group is expanded in the tree. Groups start expanded so the list reads the same
    /// way it did before it became collapsible.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                VisibilityChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// Raised when the user toggles the checkbox. The view applies the change to the ArcGIS layer
    /// and persists the new state to the profile.
    /// </summary>
    public event Action<MapLayerToggleViewModel>? VisibilityChanged;
}
