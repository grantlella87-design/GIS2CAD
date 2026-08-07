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

    /// <summary>
    /// One list per field, held so the same object comes back on every read. See ChoicesFor: a
    /// dropdown handed a new list mid-commit throws the value being committed away.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<ProposedMainCodedValue>> _choices =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ProposedMainLayerSchema _schema;

    public ProposedMainSegmentAttributesViewModel(int segmentNumber, ProposedMainLayerSchema schema)
    {
        SegmentNumber = segmentNumber;
        _schema = schema;
        Choices = new ProposedMainChoiceLookup(this);
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

            // Changing the subtype changes what every other field is allowed to hold, so the lists
            // are all reread and anything they no longer allow is dropped. Leaving a value behind
            // that the new subtype forbids would send GIS something it is going to refuse, and the
            // dropdown would be showing a blank while the row held a value.
            if (IsSubtypeField(name)) { OnSubtypeChanged(); }

            // The indexer as a whole, because a binding to [SOMEFIELD] listens for this rather than
            // for a property by that name.
            RaisePropertyChanged("Item[]");
            RaisePropertyChanged(nameof(IsComplete));
            RaisePropertyChanged(nameof(MissingDisplay));
            ValuesChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// The values each field may hold on this row, which depends on the subtype it is set to. Bound
    /// per cell, so two rows of different subtypes offer different lists for the same column.
    /// </summary>
    public ProposedMainChoiceLookup Choices { get; }

    private bool IsSubtypeField(string name) =>
        _schema.HasSubtypes && string.Equals(name, _schema.SubtypeFieldName, StringComparison.OrdinalIgnoreCase);

    private void OnSubtypeChanged()
    {
        var subtypeCode = this[_schema.SubtypeFieldName];

        foreach (var field in _schema.Fields)
        {
            if (IsSubtypeField(field.Name)) { continue; }

            var held = this[field.Name];
            if (string.IsNullOrWhiteSpace(held)) { continue; }

            var allowed = _schema.CodedValuesFor(field, subtypeCode);
            if (allowed.Count == 0) { continue; }
            if (allowed.Any(v => string.Equals(v.Code, held, StringComparison.OrdinalIgnoreCase))) { continue; }

            _values[field.Name] = string.Empty;
        }

        // Every list except the subtype's own is thrown away, so the next read rebuilds it against the
        // subtype now chosen. The subtype's list is kept: it is the same list of subtypes whatever is
        // picked from it, and replacing it is what used to lose the pick being made.
        foreach (var name in _choices.Keys.Where(k => !IsSubtypeField(k)).ToList())
        {
            _choices.Remove(name);
        }

        // The lists themselves, so every dropdown on the row refills from the new subtype.
        RaisePropertyChanged(nameof(Choices));
    }

    /// <summary>
    /// The values one field may hold on this row, for a cell to bind its list to.
    ///
    /// The same list object comes back every time until the subtype changes, and that is the whole
    /// point of the cache rather than a saving. Choosing a subtype announces that every list on the row
    /// has changed, and a dropdown told its list has changed swaps it out; a dropdown that swaps its
    /// list out in the middle of committing a value drops the value on the way past and writes the
    /// blank back to the row. Asset Group is the subtype field, so it did that to itself: pick a value,
    /// and it announced the change that threw the value away. Handing back the identical list means
    /// there is nothing to swap and nothing to drop.
    /// </summary>
    public IReadOnlyList<ProposedMainCodedValue> ChoicesFor(string fieldName)
    {
        var name = fieldName ?? string.Empty;
        if (_choices.TryGetValue(name, out var cached)) { return cached; }

        var built = BuildChoicesFor(name);
        _choices[name] = built;
        return built;
    }

    private IReadOnlyList<ProposedMainCodedValue> BuildChoicesFor(string fieldName)
    {
        var field = _schema.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (field == null) { return Array.Empty<ProposedMainCodedValue>(); }

        // The subtype field itself offers the subtypes, which is the list that decides all the others.
        if (IsSubtypeField(fieldName))
        {
            return _schema.Subtypes.Select(s => new ProposedMainCodedValue(s.Code, s.Name)).ToList();
        }

        return _schema.CodedValuesFor(field, this[_schema.SubtypeFieldName]);
    }

    /// <summary>
    /// Whether anything this row holds mentions the given word, by its value or by the name of the
    /// coded value it stands for.
    ///
    /// Both, because a row holds codes rather than names: the material that reads "Steel" in the
    /// dropdown is stored as whatever number GIS gave it, and a test against the stored value alone
    /// would never match the word a user was looking at when they chose it.
    /// </summary>
    public bool AnyValueMentions(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) { return false; }

        foreach (var pair in _values)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) { continue; }
            if (pair.Value.Contains(word, StringComparison.OrdinalIgnoreCase)) { return true; }

            foreach (var choice in ChoicesFor(pair.Key))
            {
                if (!string.Equals(choice.Code, pair.Value, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (choice.Name.Contains(word, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
        }

        return false;
    }

    /// <summary>The required fields still empty on this row, by their labels.</summary>
    public IReadOnlyList<string> Missing =>
        _schema.Fields.Where(f => f.Required && string.IsNullOrWhiteSpace(this[f.Name]))
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

/// <summary>
/// A row's dropdown lists, reached by field name.
///
/// Its own object rather than a method on the row, because a cell binds to a path and a path can
/// index a property but cannot call a method: Choices[MATERIAL] is how a column asks one row what it
/// is allowed to offer.
/// </summary>
public sealed class ProposedMainChoiceLookup
{
    private readonly ProposedMainSegmentAttributesViewModel _row;

    public ProposedMainChoiceLookup(ProposedMainSegmentAttributesViewModel row)
    {
        _row = row;
    }

    public IReadOnlyList<ProposedMainCodedValue> this[string fieldName] => _row.ChoicesFor(fieldName);
}
