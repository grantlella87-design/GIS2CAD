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
