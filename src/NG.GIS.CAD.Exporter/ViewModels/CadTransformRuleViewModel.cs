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
        }
    }
    public int AciColor
    {
        get => Rule.AciColor;
        set
        {
            Rule.AciColor = value;
            RaisePropertyChanged();
        }
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
