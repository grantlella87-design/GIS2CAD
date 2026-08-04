using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One user-added service shown on the extent page map. Toggling <see cref="Enabled"/> adds or
/// removes the service's layer without deleting the entry from the profile.
/// </summary>
/// <summary>
/// A layer that came with the map rather than from the profile, and the two calls needed to read and
/// change whether it is drawn.
///
/// Held as functions rather than as the layer itself, so the view model can list and toggle what the map
/// is showing without taking a reference to the map or the runtime's layer types.
///
/// Layer is the one exception, and it is deliberately untyped. Ordering the tiles has to order the
/// layers on the map, and that means naming which layer a tile stands for. The view casts it back; the
/// view model only ever passes it along, so it still owes nothing to the runtime.
/// </summary>
public sealed record BaseMapLayerHandle(
    string Name,
    object Layer,
    Func<bool> GetVisible,
    Action<bool> SetVisible,
    Action Remove);

public sealed class MapDataSourceViewModel : ObservableObject
{
    private string _status = string.Empty;
    private bool _enabled;
    private readonly BaseMapLayerHandle? _baseLayer;

    public MapDataSourceViewModel(MapDataSource source)
    {
        Source = source;
        _enabled = source.Enabled;
    }

    /// <summary>
    /// A layer the map brought with it. It gets a stand-in <see cref="MapDataSource"/> so the list can
    /// hold one kind of thing, but toggling it goes to the layer rather than to the profile: these are
    /// not the user's entries to own, and would come back from the web map next time regardless.
    /// </summary>
    public MapDataSourceViewModel(BaseMapLayerHandle baseLayer)
    {
        _baseLayer = baseLayer;
        Source = new MapDataSource { Name = baseLayer.Name, Url = string.Empty, Enabled = baseLayer.GetVisible() };
        _enabled = Source.Enabled;
        _status = "From the map. Untick to hide it, or Remove to take it off.";
    }

    /// <summary>Whether this entry came with the map rather than from the profile.</summary>
    public bool IsFromMap => _baseLayer != null;

    /// <summary>
    /// The map layer this tile stands for, for a tile that came from the map. Null for a profile
    /// source, whose layer the view already knows by URL.
    /// </summary>
    public object? MapLayerRef => _baseLayer?.Layer;

    /// <summary>
    /// Every tile moves, including the layers the map brought with it.
    ///
    /// They did not at first, on the reasoning that a layer with no profile entry has nowhere to record
    /// a position. True, but it made the feature useless: the web map supplies most of what is on the
    /// map, so a profile source or two among them had nothing to move past and the tiles looked stuck.
    /// The order of the map's own layers lasts the session, which is the same bargain their tick boxes
    /// already make.
    /// </summary>
    public bool CanReorder => true;

    /// <summary>Takes a map layer off the map. Nothing to do for a profile source.</summary>
    public void RemoveFromMap() => _baseLayer?.Remove();

    public MapDataSource Source { get; }

    public string Name => string.IsNullOrWhiteSpace(Source.Name) ? Source.Url : Source.Name;

    public string Url => Source.Url;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) { return; }

            Source.Enabled = value;

            // A layer from the map is shown or hidden directly. Raising the changed event instead would
            // send it round the reconcile that adds and removes profile sources, which has nothing to
            // say about a layer the web map owns.
            if (_baseLayer != null)
            {
                _baseLayer.SetVisible(value);
                return;
            }

            EnabledChanged?.Invoke(this);
        }
    }

    /// <summary>Load result for this source, shown under the entry so failures are visible.</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public event Action<MapDataSourceViewModel>? EnabledChanged;
}
