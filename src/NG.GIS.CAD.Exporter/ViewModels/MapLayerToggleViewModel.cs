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

    public MapLayerToggleViewModel(string path, string name, bool isVisible, string? serviceUrl, bool isLeaf)
    {
        Path = path;
        Name = name;
        _isVisible = isVisible;
        ServiceUrl = serviceUrl;
        IsLeaf = isLeaf;
    }

    /// <summary>Stable key used to persist this node: "Layer" or "Layer/Sublayer".</summary>
    public string Path { get; }

    public string Name { get; }

    /// <summary>
    /// REST URL of the service layer behind this node, when one can be worked out. Pages 3 and 4 use
    /// it to read fields and to build transform rules, so a node without one cannot be exported.
    /// </summary>
    public string? ServiceUrl { get; }

    /// <summary>
    /// True when this node has no sublayers of its own. Only leaves are offered for export: a parent's
    /// URL is the whole service, which would duplicate every sublayer already listed beneath it.
    /// </summary>
    public bool IsLeaf { get; }

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
