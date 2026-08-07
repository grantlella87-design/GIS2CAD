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

            // Arming a symbol calls off a move that was waiting for a click, for the same reason a move
            // calls off an armed symbol: one click, one meaning.
            if (value != null && _movingPlacedFeature != null) { MovingPlacedFeature = null; }

            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsPlacingPaletteSymbol));
            RaisePropertyChanged(nameof(PalettePlacementStatus));
        }
    }

    public bool IsPlacingPaletteSymbol => _selectedPaletteSymbol != null;

    /// <summary>
    /// What has been placed and not yet sent to GIS, in the order it was placed, so each one can be
    /// moved or taken off again before it goes anywhere.
    /// </summary>
    public ObservableCollection<PlacedFeatureViewModel> PlacedFeatures { get; } = new();

    public bool HasPlacedFeatures => PlacedFeatures.Count > 0;

    /// <summary>Called by the view when something is placed or removed, so the section can hide itself
    /// while there is nothing in it.</summary>
    public void RaisePlacedFeaturesChanged() => RaisePropertyChanged(nameof(HasPlacedFeatures));

    private PlacedFeatureViewModel? _movingPlacedFeature;

    /// <summary>
    /// The placed feature the next click on the map will move, or null when the next click places
    /// something new instead.
    ///
    /// One at a time, and never at the same time as a symbol is armed: a click on the map has to mean
    /// one thing, and moving something that is already there and putting another one down are two.
    /// </summary>
    public PlacedFeatureViewModel? MovingPlacedFeature
    {
        get => _movingPlacedFeature;
        set
        {
            if (ReferenceEquals(_movingPlacedFeature, value)) { return; }

            foreach (var placed in PlacedFeatures) { placed.IsMoving = ReferenceEquals(placed, value); }

            _movingPlacedFeature = value;
            if (value != null) { SelectedPaletteSymbol = null; }

            RaisePropertyChanged();
            RaisePropertyChanged(nameof(PalettePlacementStatus));
        }
    }

    /// <summary>What the map is about to do, said above the palette rather than in the status line at
    /// the bottom, because it describes the very next click.</summary>
    public string PalettePlacementStatus => _movingPlacedFeature != null
        ? "Click the map to move " + _movingPlacedFeature.Label + ". Press Move again to leave it where it is."
        : _selectedPaletteSymbol == null
            ? "Pick a symbol, then click the map to place one."
            : "Click the map to place " + _selectedPaletteSymbol.Label + ". Pick it again to stop.";
}
