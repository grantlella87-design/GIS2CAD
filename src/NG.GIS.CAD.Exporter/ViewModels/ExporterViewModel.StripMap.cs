using System.Globalization;
using NG.GIS.CAD.Exporter.Models;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// The strip map index settings on page 2: which viewport sizes the sheets, at what scale, and how
/// much they overlap. The sheets themselves are laid out by the view, which is where the proposed
/// main geometry and the map live.
/// </summary>
public sealed partial class ExporterViewModel
{
    private CadViewport? _selectedStripMapViewport;
    private double _stripMapSheetWidth;
    private double _stripMapSheetHeight;
    private bool _stripMapSheetIsMillimetres;
    private double _stripMapScaleDenominator = 240;
    private double _stripMapOverlapPercent = 5;
    private string _stripMapSummary = "No strip map sheets laid out yet.";

    /// <summary>Viewports found in the template, or in the open drawing when no template is set.</summary>
    public ObservableCollection<CadViewport> StripMapViewports { get; } = new();

    /// <summary>
    /// The viewport the sheet size is taken from. Choosing one fills the width and height, which stay
    /// editable afterwards: a viewport is a convenient starting point, not a constraint.
    /// </summary>
    public CadViewport? SelectedStripMapViewport
    {
        get => _selectedStripMapViewport;
        set
        {
            if (!SetProperty(ref _selectedStripMapViewport, value)) { return; }
            if (value == null) { return; }

            StripMapSheetWidth = value.Width;
            StripMapSheetHeight = value.Height;
            StripMapSheetIsMillimetres = value.IsMillimetres;
        }
    }

    public double StripMapSheetWidth
    {
        get => _stripMapSheetWidth;
        set { if (SetProperty(ref _stripMapSheetWidth, value)) { RaiseStripMapGroundSizeChanged(); } }
    }

    public double StripMapSheetHeight
    {
        get => _stripMapSheetHeight;
        set { if (SetProperty(ref _stripMapSheetHeight, value)) { RaiseStripMapGroundSizeChanged(); } }
    }

    public bool StripMapSheetIsMillimetres
    {
        get => _stripMapSheetIsMillimetres;
        set
        {
            if (!SetProperty(ref _stripMapSheetIsMillimetres, value)) { return; }
            RaisePropertyChanged(nameof(StripMapSheetIsInches));
            RaiseStripMapGroundSizeChanged();
        }
    }

    /// <summary>Bound to the inches radio button, which is the other half of the millimetres one.</summary>
    public bool StripMapSheetIsInches
    {
        get => !StripMapSheetIsMillimetres;
        set { if (value) { StripMapSheetIsMillimetres = false; } }
    }

    /// <summary>The denominator of the plot scale: 240 for 1:240. Editable, defaulting to 240.</summary>
    public double StripMapScaleDenominator
    {
        get => _stripMapScaleDenominator;
        set { if (SetProperty(ref _stripMapScaleDenominator, value)) { RaiseStripMapGroundSizeChanged(); } }
    }

    /// <summary>Overlap between neighbouring sheets, as a percentage of the along-route size.</summary>
    public double StripMapOverlapPercent
    {
        get => _stripMapOverlapPercent;
        set => SetProperty(ref _stripMapOverlapPercent, value);
    }

    public double StripMapOverlapFraction => Math.Clamp(StripMapOverlapPercent / 100.0, 0.0, 0.9);

    /// <summary>Ground size of one sheet, which is the number that says whether the scale is right.</summary>
    public double StripMapGroundWidthMetres =>
        StripMapIndexService.PaperToGroundMetres(StripMapSheetWidth, StripMapSheetIsMillimetres, StripMapScaleDenominator);

    public double StripMapGroundHeightMetres =>
        StripMapIndexService.PaperToGroundMetres(StripMapSheetHeight, StripMapSheetIsMillimetres, StripMapScaleDenominator);

    /// <summary>Reads back what one sheet covers on the ground, so a wrong scale is obvious.</summary>
    public string StripMapGroundSizeText
    {
        get
        {
            if (StripMapSheetWidth <= 0 || StripMapSheetHeight <= 0 || StripMapScaleDenominator <= 0)
            {
                return "Choose a viewport, or type a sheet size.";
            }

            var widthFeet = StripMapIndexService.MetresToFeet(StripMapGroundWidthMetres);
            var heightFeet = StripMapIndexService.MetresToFeet(StripMapGroundHeightMetres);
            return string.Format(
                CultureInfo.InvariantCulture,
                "Each sheet covers {0:0.#} × {1:0.#} ft at 1:{2:0.##}.",
                widthFeet, heightFeet, StripMapScaleDenominator);
        }
    }

    /// <summary>What the last layout produced, or why it produced nothing.</summary>
    public string StripMapSummary
    {
        get => _stripMapSummary;
        set => SetProperty(ref _stripMapSummary, value);
    }

    private void RaiseStripMapGroundSizeChanged()
    {
        RaisePropertyChanged(nameof(StripMapGroundWidthMetres));
        RaisePropertyChanged(nameof(StripMapGroundHeightMetres));
        RaisePropertyChanged(nameof(StripMapGroundSizeText));
    }

    /// <summary>
    /// Refills the viewport list from the CAD catalog, keeping whichever viewport was chosen if the
    /// new drawing still has one by that key.
    /// </summary>
    public void LoadStripMapViewports(IReadOnlyList<CadViewport> viewports)
    {
        var previousKey = SelectedStripMapViewport?.Key ?? _profile.StripMapIndex.ViewportKey;

        StripMapViewports.Clear();
        foreach (var viewport in viewports) { StripMapViewports.Add(viewport); }

        // Assigned through the backing field, because the property's side effect is to overwrite the
        // sheet size, and restoring a selection should not discard dimensions the user has edited.
        _selectedStripMapViewport = string.IsNullOrWhiteSpace(previousKey)
            ? null
            : StripMapViewports.FirstOrDefault(v => string.Equals(v.Key, previousKey, StringComparison.OrdinalIgnoreCase));
        RaisePropertyChanged(nameof(SelectedStripMapViewport));
    }

    /// <summary>Reads the saved settings into the page. Called once the profile has loaded.</summary>
    private void LoadStripMapSettings()
    {
        var settings = _profile.StripMapIndex;
        _stripMapSheetWidth = settings.SheetWidth;
        _stripMapSheetHeight = settings.SheetHeight;
        _stripMapSheetIsMillimetres = settings.SheetIsMillimetres;
        _stripMapScaleDenominator = settings.ScaleDenominator > 0 ? settings.ScaleDenominator : 240;
        _stripMapOverlapPercent = Math.Clamp(settings.OverlapFraction * 100.0, 0.0, 90.0);

        RaisePropertyChanged(nameof(StripMapSheetWidth));
        RaisePropertyChanged(nameof(StripMapSheetHeight));
        RaisePropertyChanged(nameof(StripMapSheetIsMillimetres));
        RaisePropertyChanged(nameof(StripMapSheetIsInches));
        RaisePropertyChanged(nameof(StripMapScaleDenominator));
        RaisePropertyChanged(nameof(StripMapOverlapPercent));
        RaiseStripMapGroundSizeChanged();
    }

    /// <summary>Writes the current settings back to the profile so the next session starts where this one left off.</summary>
    public async Task SaveStripMapSettingsAsync()
    {
        try
        {
            _profile.StripMapIndex = new StripMapIndexSettings
            {
                ViewportKey = SelectedStripMapViewport?.Key ?? string.Empty,
                SheetWidth = StripMapSheetWidth,
                SheetHeight = StripMapSheetHeight,
                SheetIsMillimetres = StripMapSheetIsMillimetres,
                ScaleDenominator = StripMapScaleDenominator,
                OverlapFraction = StripMapOverlapFraction
            };
            await _services.ProfileStore.SaveAsync(_profile, ProfilePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = "Saving the strip map settings failed: " + ex.Message;
        }
    }
}
