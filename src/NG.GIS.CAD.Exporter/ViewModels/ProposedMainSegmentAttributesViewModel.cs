using System.Globalization;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// One row of the proposed main attribute table: the attributes of a single drawn segment.
///
/// A row rather than a shared set of values, because each segment becomes its own feature in GIS and
/// they are not the same pipe. Two segments of a corridor can differ in size, material or pressure,
/// and one set of boxes for all of them could only ever describe the case where they do not.
///
/// Values are held against field names and reached through the indexer, so the table's columns can be
/// built from whatever fields the layer turns out to have rather than from properties written here.
/// </summary>
public sealed class ProposedMainSegmentAttributesViewModel : ObservableObject
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<ProposedMainField> _fields;

    public ProposedMainSegmentAttributesViewModel(int segmentNumber, IReadOnlyList<ProposedMainField> fields)
    {
        SegmentNumber = segmentNumber;
        _fields = fields;
    }

    /// <summary>Which drawn segment this row is for, counting from one as the map labels them.</summary>
    public int SegmentNumber { get; }

    public string SegmentDisplay => "Segment " + SegmentNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The value for one field. The indexer is what lets a column bind to a field the code has never
    /// heard of, which is the point: the layer decides what the columns are.
    /// </summary>
    public string this[string fieldName]
    {
        get => _values.TryGetValue(fieldName ?? string.Empty, out var value) ? value : string.Empty;
        set
        {
            var name = fieldName ?? string.Empty;
            var next = value ?? string.Empty;
            if (_values.TryGetValue(name, out var current) && string.Equals(current, next, StringComparison.Ordinal))
            {
                return;
            }

            _values[name] = next;

            // The indexer as a whole, because a binding to [SOMEFIELD] listens for this rather than
            // for a property by that name.
            RaisePropertyChanged("Item[]");
            RaisePropertyChanged(nameof(IsComplete));
            RaisePropertyChanged(nameof(MissingDisplay));
            ValuesChanged?.Invoke(this);
        }
    }

    /// <summary>The required fields still empty on this row, by their labels.</summary>
    public IReadOnlyList<string> Missing =>
        _fields.Where(f => f.Required && string.IsNullOrWhiteSpace(this[f.Name]))
               .Select(f => f.Display)
               .ToList();

    public bool IsComplete => Missing.Count == 0;

    /// <summary>What is still needed on this row, for the cell that shows it at a glance.</summary>
    public string MissingDisplay
    {
        get
        {
            var missing = Missing;
            return missing.Count == 0 ? "Ready" : "Needs " + string.Join(", ", missing);
        }
    }

    /// <summary>
    /// What to send for this segment, leaving out what nobody filled in. An empty value is not the
    /// same as a value of empty: sending blanks would overwrite whatever GIS would otherwise default.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToAttributeValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _values)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) { continue; }
            values[pair.Key] = pair.Value.Trim();
        }
        return values;
    }

    /// <summary>Copies another row's values in, for filling a new segment from the one before it.</summary>
    public void CopyValuesFrom(ProposedMainSegmentAttributesViewModel other)
    {
        foreach (var pair in other._values) { this[pair.Key] = pair.Value; }
    }

    public event Action<ProposedMainSegmentAttributesViewModel>? ValuesChanged;
}
