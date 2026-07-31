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

    /// <summary>
    /// Whether this layer is areas. Hatching only means anything for those, so the settings for it are
    /// shown for a polygon layer and nowhere else.
    /// </summary>
    public bool IsPolygon =>
        Rule.GeometryType.Contains("Polygon", StringComparison.OrdinalIgnoreCase);

    public bool HatchPolygons
    {
        get => Rule.HatchPolygons;
        set
        {
            Rule.HatchPolygons = value;
            RaisePropertyChanged();
        }
    }

    public string HatchPattern
    {
        get => Rule.HatchPattern;
        set
        {
            Rule.HatchPattern = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HatchScaleApplies));
        }
    }

    /// <summary>
    /// Whether the spacing box is worth offering. SOLID has nothing to space, so a value typed against
    /// it would be taken and ignored, which reads as the setting not working.
    /// </summary>
    public bool HatchScaleApplies =>
        !string.Equals(Rule.HatchPattern?.Trim(), "SOLID", StringComparison.OrdinalIgnoreCase);

    public double HatchScale
    {
        get => Rule.HatchScale;
        set
        {
            Rule.HatchScale = value;
            RaisePropertyChanged();
        }
    }

    public int HatchTransparencyPercent
    {
        get => Rule.HatchTransparencyPercent;
        set
        {
            Rule.HatchTransparencyPercent = Math.Clamp(value, 0, 90);
            RaisePropertyChanged();
        }
    }

    /// <summary>The patterns offered, being the ones that ship with AutoCAD and read on a plan.</summary>
    public IReadOnlyList<string> HatchPatterns { get; } = new[]
    {
        "SOLID", "ANSI31", "ANSI32", "ANSI37", "EARTH", "GRAVEL", "HONEY", "NET", "STEEL"
    };
    public string ColorMode
    {
        get => Rule.ColorMode;
        set
        {
            Rule.ColorMode = value;
            RaisePropertyChanged();
            RaiseColorDisplayChanged();
        }
    }
    public int AciColor
    {
        get => Rule.AciColor;
        set
        {
            Rule.AciColor = value;

            // Only the colour dialog can turn an index into an RGB, so an index typed here leaves the
            // stored RGB describing whichever colour was picked before. Clearing it blanks the swatch
            // rather than showing a colour that is no longer the one named beside it. The dialog is
            // the way to get a swatch back.
            Rule.RgbColor = string.Empty;

            RaisePropertyChanged();
            RaisePropertyChanged(nameof(RgbColor));
            RaiseColorDisplayChanged();
        }
    }

    /// <summary>
    /// The true colour as "#RRGGBB". Editable, so a known value can be typed rather than picked off
    /// the dialog, which is the same affordance the ACI box gives an index.
    /// </summary>
    public string RgbColor
    {
        get => Rule.RgbColor;
        set
        {
            // Stored as typed rather than normalised, so a half finished value is not rewritten out
            // from under the caret. Everything that reads it goes through NormalizeRgbHex instead.
            Rule.RgbColor = value ?? string.Empty;
            RaisePropertyChanged();
            RaiseColorDisplayChanged();
        }
    }

    /// <summary>
    /// The swatch colour as "#RRGGBB", or null when nothing usable has been set. Null lets the binding
    /// fall back rather than showing black, which would read as a deliberate choice, and keeps a
    /// part typed value from flickering the swatch.
    /// </summary>
    public string? ColorPreview => NormalizeRgbHex(Rule.RgbColor);

    /// <summary>Says what the colour actually is, since a swatch alone does not distinguish ACI 1 from red.</summary>
    public string ColorDescription => Rule.ColorMode switch
    {
        "ACI" => "ACI " + Rule.AciColor,
        "RGB" => DescribeRgb(Rule.RgbColor),
        _ => "ByLayer"
    };

    /// <summary>
    /// Whether the colour is an index. The ACI box is only shown for these: after a true colour pick
    /// the index is whatever was chosen last and no longer describes the colour, so displaying it
    /// would be worse than showing nothing.
    /// </summary>
    public bool IsAciColor => string.Equals(Rule.ColorMode, "ACI", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the colour is a true colour, which is when the RGB box is the useful one to show.</summary>
    public bool IsRgbColor => string.Equals(Rule.ColorMode, "RGB", StringComparison.OrdinalIgnoreCase);

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
        RaisePropertyChanged(nameof(RgbColor));
        RaiseColorDisplayChanged();
    }

    /// <summary>
    /// Records a ByLayer or ByBlock pick. The stored RGB is cleared with it, so the swatch stops
    /// showing whatever colour was chosen before rather than implying this rule still has one.
    /// </summary>
    public void ApplyByLayerColor()
    {
        Rule.ColorMode = "ByLayer";
        Rule.RgbColor = string.Empty;

        RaisePropertyChanged(nameof(ColorMode));
        RaisePropertyChanged(nameof(RgbColor));
        RaiseColorDisplayChanged();
    }

    private void RaiseColorDisplayChanged()
    {
        RaisePropertyChanged(nameof(ColorPreview));
        RaisePropertyChanged(nameof(ColorDescription));
        RaisePropertyChanged(nameof(IsAciColor));
        RaisePropertyChanged(nameof(IsRgbColor));
    }

    /// <summary>
    /// Reads the true colour back in AutoCAD's own notation, so what the panel says matches what the
    /// True Color tab showed when it was picked.
    /// </summary>
    private static string DescribeRgb(string? value)
    {
        var normalized = NormalizeRgbHex(value);
        if (normalized == null) { return "RGB (not set)"; }
        var r = Convert.ToInt32(normalized.Substring(1, 2), 16);
        var g = Convert.ToInt32(normalized.Substring(3, 2), 16);
        var b = Convert.ToInt32(normalized.Substring(5, 2), 16);
        return "RGB " + r + "," + g + "," + b;
    }

    /// <summary>
    /// Returns "#RRGGBB" for anything that reads as a six digit hex colour, with or without the hash,
    /// and null for anything that does not. Accepting a typed value without the hash means the box
    /// does not demand a character the user has no reason to think matters.
    /// </summary>
    private static string? NormalizeRgbHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var digits = value.Trim();
        if (digits.StartsWith("#", StringComparison.Ordinal)) { digits = digits[1..]; }
        if (digits.Length != 6) { return null; }
        foreach (var character in digits)
        {
            if (!Uri.IsHexDigit(character)) { return null; }
        }
        return "#" + digits.ToUpperInvariant();
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
