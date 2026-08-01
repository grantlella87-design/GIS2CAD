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
/// </summary>
public sealed record BaseMapLayerHandle(string Name, Func<bool> GetVisible, Action<bool> SetVisible, Action Remove);

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
