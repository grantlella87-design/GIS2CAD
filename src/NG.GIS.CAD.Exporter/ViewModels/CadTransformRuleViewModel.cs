using NG.GIS.CAD.Exporter.Models;
namespace NG.GIS.CAD.Exporter.ViewModels;
public sealed class CadTransformRuleViewModel : ObservableObject
{
    public CadTransformRule Rule { get; }
    public CadTransformRuleViewModel(CadTransformRule rule)
    {
        Rule = rule;
    }
    public string LayerName => Rule.LayerName;
    public string LayerUrl => Rule.LayerUrl;
    public string GeometryType => Rule.GeometryType;

    /// <summary>
    /// Point features are drawn as block references, so only they need a block, a rotation and a
    /// scale. Multipoint counts as a point for this purpose.
    /// </summary>
    public bool IsPoint =>
        Rule.GeometryType.Contains("Point", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lines are drawn as polylines, so they need a line type. Polygon boundaries are drawn the same
    /// way and take a line type too.
    /// </summary>
    public bool IsLine =>
        Rule.GeometryType.Contains("Polyline", StringComparison.OrdinalIgnoreCase)
        || Rule.GeometryType.Contains("Line", StringComparison.OrdinalIgnoreCase)
        || Rule.GeometryType.Contains("Polygon", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads as "Point" or "Polyline" rather than "esriGeometryPolyline".</summary>
    public string GeometryDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Rule.GeometryType)) { return "unknown"; }
            return Rule.GeometryType.StartsWith("esriGeometry", StringComparison.OrdinalIgnoreCase)
                ? Rule.GeometryType["esriGeometry".Length..]
                : Rule.GeometryType;
        }
    }

    /// <summary>Shown beside the layer name so the list says what kind of feature each rule covers.</summary>
    public string LayerNameWithGeometry => LayerName + "  (" + GeometryDisplay + ")";
    public string CadLayerName
    {
        get => Rule.CadLayerName;
        set
        {
            Rule.CadLayerName = value;
            RaisePropertyChanged();
        }
    }
    public string BlockName
    {
        get => Rule.BlockName;
        set
        {
            Rule.BlockName = value;
            RaisePropertyChanged();
        }
    }
    public string LineType
    {
        get => Rule.LineType;
        set
        {
            Rule.LineType = value;
            RaisePropertyChanged();
        }
    }
    public string ColorMode
    {
        get => Rule.ColorMode;
        set
        {
            Rule.ColorMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ColorDescription));
            RaisePropertyChanged(nameof(IsAciColor));
        }
    }
    public int AciColor
    {
        get => Rule.AciColor;
        set
        {
            Rule.AciColor = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ColorDescription));
            RaisePropertyChanged(nameof(IsAciColor));
        }
    }

    /// <summary>
    /// The swatch colour as "#RRGGBB", or null when nothing has been picked. Null lets the binding
    /// fall back rather than showing black, which would read as a deliberate choice.
    /// </summary>
    public string? ColorPreview => string.IsNullOrWhiteSpace(Rule.RgbColor) ? null : Rule.RgbColor;

    /// <summary>Says what the colour actually is, since a swatch alone does not distinguish ACI 1 from red.</summary>
    public string ColorDescription => Rule.ColorMode switch
    {
        "ACI" => "ACI " + Rule.AciColor,
        "RGB" => string.IsNullOrWhiteSpace(Rule.RgbColor) ? "RGB" : Rule.RgbColor,
        _ => "ByLayer"
    };

    /// <summary>
    /// Whether the colour is an index. The ACI box is only shown for these: after a true colour pick
    /// the index is whatever was chosen last and no longer describes the colour, so displaying it
    /// would be worse than showing nothing.
    /// </summary>
    public bool IsAciColor => string.Equals(Rule.ColorMode, "ACI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records a colour chosen in the CAD colour dialog. Takes plain values rather than an AutoCAD
    /// colour so the view models stay clear of the AutoCAD assemblies.
    /// </summary>
    public void ApplyPickedColor(bool isAci, int aciIndex, string rgbHex)
    {
        Rule.ColorMode = isAci ? "ACI" : "RGB";
        if (isAci) { Rule.AciColor = aciIndex; }
        Rule.RgbColor = rgbHex;

        RaisePropertyChanged(nameof(ColorMode));
        RaisePropertyChanged(nameof(AciColor));
        RaisePropertyChanged(nameof(ColorPreview));
        RaisePropertyChanged(nameof(ColorDescription));
        RaisePropertyChanged(nameof(IsAciColor));
    }
    public double RotationOffsetDegrees
    {
        get => Rule.RotationOffsetDegrees;
        set
        {
            Rule.RotationOffsetDegrees = value;
            RaisePropertyChanged();
        }
    }
    public double ScaleValue
    {
        get => Rule.ScaleValue;
        set
        {
            Rule.ScaleValue = value;
            RaisePropertyChanged();
        }
    }
}
