using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One row of the proposed main attribute table on page 2: a field read from GIS and the value the
/// user is filling in for it.
///
/// The field's own rules travel with it rather than being restated here, so what the page insists on
/// is what the service insists on. A field GIS has since made mandatory becomes mandatory here on the
/// next run without this code being touched.
/// </summary>
public sealed class ProposedMainAttributeViewModel : ObservableObject
{
    private string _value = string.Empty;

    public ProposedMainAttributeViewModel(ProposedMainField field)
    {
        Field = field;
    }

    public ProposedMainField Field { get; }

    public string Name => Field.Name;

    /// <summary>The label, marked when a value has to be given, so the table says what it needs.</summary>
    public string Display => Field.Required ? Field.Display + " *" : Field.Display;

    public bool Required => Field.Required;

    public bool HasCodedValues => Field.HasCodedValues;

    /// <summary>The allowed values, for a field with a coded domain. Empty for free text.</summary>
    public IReadOnlyList<ProposedMainCodedValue> CodedValues => Field.CodedValues;

    /// <summary>What the field will hold. Empty is empty: nothing is invented for a blank.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value ?? string.Empty)) { return; }
            RaisePropertyChanged(nameof(IsMissing));
            ValueChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// The coded value picked, kept in step with <see cref="Value"/> so the table can bind a dropdown
    /// to one field and a text box to another without two ideas of what has been entered.
    /// </summary>
    public ProposedMainCodedValue? SelectedCodedValue
    {
        get => CodedValues.FirstOrDefault(c => string.Equals(c.Code, _value, StringComparison.OrdinalIgnoreCase));
        set
        {
            Value = value?.Code ?? string.Empty;
            RaisePropertyChanged();
        }
    }

    /// <summary>A required field with nothing in it. What the page will not let past.</summary>
    public bool IsMissing => Required && string.IsNullOrWhiteSpace(_value);

    /// <summary>Longest text the field will take, or 0 where the service did not say.</summary>
    public int MaxLength => Field.Length;

    public event Action<ProposedMainAttributeViewModel>? ValueChanged;
}
