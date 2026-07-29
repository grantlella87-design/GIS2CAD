using System.Windows.Media;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One row of a layer's legend: the symbol it draws with, and what that symbol means.
///
/// The swatch is a WPF ImageSource because that is what the runtime hands back once a symbol has been
/// rendered, and re-wrapping it would buy nothing in a plug-in that only ever runs under WPF.
/// </summary>
public sealed class MapLegendItemViewModel
{
    public MapLegendItemViewModel(string label, ImageSource? swatch)
    {
        Label = label;
        Swatch = swatch;
    }

    /// <summary>
    /// The class this symbol stands for, as the service names it. Empty for a layer drawn with a
    /// single symbol, where the layer's own name is already the label.
    /// </summary>
    public string Label { get; }

    public ImageSource? Swatch { get; }
}
