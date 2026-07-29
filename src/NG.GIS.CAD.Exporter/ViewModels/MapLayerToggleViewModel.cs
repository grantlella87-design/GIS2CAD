using System.Windows;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One toggleable entry in the extent page map layer list. Entries are created by the view once
/// the web map has loaded, one per operational layer and one per sublayer beneath it.
/// </summary>
public sealed class MapLayerToggleViewModel : ObservableObject
{
    private bool _isVisible;

    public MapLayerToggleViewModel(string path, string name, int depth, bool isVisible)
    {
        Path = path;
        Name = name;
        Depth = depth;
        _isVisible = isVisible;
    }

    /// <summary>Stable key used to persist this entry: "Layer" or "Layer/Sublayer".</summary>
    public string Path { get; }

    public string Name { get; }

    public int Depth { get; }

    /// <summary>Indents sublayers under their parent in the list.</summary>
    public Thickness Indent => new(Depth * 16, 2, 0, 2);

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
