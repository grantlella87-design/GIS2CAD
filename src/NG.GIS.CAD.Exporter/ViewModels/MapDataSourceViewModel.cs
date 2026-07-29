using NG.GIS.CAD.Exporter.Models;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One user-added service shown on the extent page map. Toggling <see cref="Enabled"/> adds or
/// removes the service's layer without deleting the entry from the profile.
/// </summary>
public sealed class MapDataSourceViewModel : ObservableObject
{
    private string _status = string.Empty;
    private bool _enabled;

    public MapDataSourceViewModel(MapDataSource source)
    {
        Source = source;
        _enabled = source.Enabled;
    }

    public MapDataSource Source { get; }

    public string Name => string.IsNullOrWhiteSpace(Source.Name) ? Source.Url : Source.Name;

    public string Url => Source.Url;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                Source.Enabled = value;
                EnabledChanged?.Invoke(this);
            }
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
