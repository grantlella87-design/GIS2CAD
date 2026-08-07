using System.Windows.Media;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One symbol in the palette beside the map: what GIS draws a feature of this class with, and what
/// value a feature has to carry to be one.
/// </summary>
public sealed class SymbolPaletteItemViewModel : ObservableObject
{
    private bool _isSelected;

    public SymbolPaletteItemViewModel(GisPaletteSymbol symbol, string layerUrl, string drawnByFieldName, ImageSource? swatch)
    {
        Symbol = symbol;
        LayerUrl = layerUrl;
        DrawnByFieldName = drawnByFieldName;
        Swatch = swatch;
    }

    public GisPaletteSymbol Symbol { get; }

    /// <summary>Where a feature placed with this symbol goes. Carried on the item so the click that
    /// places one needs to know nothing beyond which symbol was picked.</summary>
    public string LayerUrl { get; }

    public string DrawnByFieldName { get; }

    public string Label => Symbol.Label;

    /// <summary>The swatch as the service drew it, rendered once when the palette is read.</summary>
    public ImageSource? Swatch { get; }

    /// <summary>The colour to fall back to when the service described the symbol without a picture.</summary>
    public Brush Fallback => new SolidColorBrush(Color.FromArgb(
        (byte)Symbol.A, (byte)Symbol.R, (byte)Symbol.G, (byte)Symbol.B));

    /// <summary>Whether this is the symbol the next click on the map will place.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// One feature already placed on the map and not yet sent, listed so it can be moved or taken off
/// again.
///
/// A list rather than only the graphics on the map. A symbol dropped in the wrong place is a mistake
/// somebody notices a minute later, and without a list the only way back was to leave the page and
/// lose everything placed with it.
/// </summary>
public sealed class PlacedFeatureViewModel : ObservableObject
{
    private bool _isMoving;
    private string _position = string.Empty;

    public PlacedFeatureViewModel(SymbolPaletteItemViewModel symbol)
    {
        Symbol = symbol;
    }

    public SymbolPaletteItemViewModel Symbol { get; }

    public string Label => Symbol.Label;

    /// <summary>Where it is, in a form that tells two of the same kind apart in the list.</summary>
    public string Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    /// <summary>Whether the next click on the map moves this one rather than placing something new.</summary>
    public bool IsMoving
    {
        get => _isMoving;
        set => SetProperty(ref _isMoving, value);
    }
}

/// <summary>
/// One layer's section of the palette. Collapsible, because there will be more layers than this one
/// and a panel of every symbol of every layer at once would be a wall rather than a tool.
/// </summary>
public sealed class SymbolPaletteLayerViewModel : ObservableObject
{
    private bool _isExpanded = true;
    private string _status = string.Empty;

    public SymbolPaletteLayerViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ObservableCollection<SymbolPaletteItemViewModel> Symbols { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>What happened while reading this layer, so a palette that could not be read says so
    /// rather than appearing empty as though the layer had no symbols.</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value)) { RaisePropertyChanged(nameof(HasStatus)); }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
}
