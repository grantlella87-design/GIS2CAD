using System.Windows.Media;

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
    /// What this layer draws with. One entry for a layer with a single symbol, one per class for a
    /// layer drawn by category or by range, and none for a group, which draws nothing of its own.
    /// </summary>
    public ObservableCollection<MapLegendItemViewModel> LegendItems { get; } = new();

    /// <summary>
    /// The swatch shown beside the layer name, for a layer that draws with a single symbol. Null for
    /// anything else, because a layer drawn by category has no one symbol that stands for it.
    /// </summary>
    public ImageSource? SingleSwatch => LegendItems.Count == 1 ? LegendItems[0].Swatch : null;

    /// <summary>
    /// Whether this layer needs a row per symbol rather than one swatch beside its name. Kept off for
    /// a single symbol so the common case stays a one line entry.
    /// </summary>
    public bool HasLegendList => LegendItems.Count > 1;

    /// <summary>
    /// Fills in the legend once the runtime has rendered the swatches. Separate from the constructor
    /// because rendering a symbol is asynchronous and the tree is built before it finishes.
    /// </summary>
    public void SetLegendItems(IEnumerable<MapLegendItemViewModel> items)
    {
        LegendItems.Clear();
        foreach (var item in items) { LegendItems.Add(item); }

        RaisePropertyChanged(nameof(SingleSwatch));
        RaisePropertyChanged(nameof(HasLegendList));
    }

    /// <summary>The node this one sits under, or null for a top level layer.</summary>
    public MapLayerToggleViewModel? Parent { get; internal set; }

    /// <summary>
    /// Whether this layer is actually drawn on the map. A sublayer keeps its own checkbox ticked when
    /// its group is unticked, so its own flag alone does not say whether it is on screen: every
    /// ancestor has to be visible too. Pages 3 and 4 select layers from this, not from IsVisible.
    /// </summary>
    public bool IsEffectivelyVisible => IsVisible && (Parent == null || Parent.IsEffectivelyVisible);

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
