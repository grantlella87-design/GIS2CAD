namespace NG.GIS.CAD.Exporter.ViewModels;

public sealed partial class ExporterViewModel
{
    /// <summary>
    /// The layers whose symbols can be placed on the map from the panel beside it, one section each.
    ///
    /// A collection rather than one layer, because this starts with Network Junction and does not end
    /// there. Each section collapses, so adding the next layer costs the panel a header rather than
    /// the length of another symbol list.
    /// </summary>
    public ObservableCollection<SymbolPaletteLayerViewModel> SymbolPalettes { get; } = new();

    private SymbolPaletteItemViewModel? _selectedPaletteSymbol;

    /// <summary>
    /// The symbol the next click on the map will place, or null when the map is not placing anything.
    ///
    /// One at a time across every section: picking a valve should put down a valve, and leaving a
    /// symbol lit in another section while a different one is armed would be two answers to what the
    /// next click does.
    /// </summary>
    public SymbolPaletteItemViewModel? SelectedPaletteSymbol
    {
        get => _selectedPaletteSymbol;
        set
        {
            if (ReferenceEquals(_selectedPaletteSymbol, value)) { return; }

            foreach (var layer in SymbolPalettes)
            {
                foreach (var symbol in layer.Symbols) { symbol.IsSelected = ReferenceEquals(symbol, value); }
            }

            _selectedPaletteSymbol = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsPlacingPaletteSymbol));
            RaisePropertyChanged(nameof(PalettePlacementStatus));
        }
    }

    public bool IsPlacingPaletteSymbol => _selectedPaletteSymbol != null;

    /// <summary>What the map is about to do, said above the palette rather than in the status line at
    /// the bottom, because it describes the very next click.</summary>
    public string PalettePlacementStatus => _selectedPaletteSymbol == null
        ? "Pick a symbol, then click the map to place one."
        : "Click the map to place " + _selectedPaletteSymbol.Label + ". Pick it again to stop.";
}
