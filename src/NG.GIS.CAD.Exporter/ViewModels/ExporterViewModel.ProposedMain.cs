using System.Collections.ObjectModel;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// Page 2's side of a hand drawn proposed main: how far apart segment ends may be and still be joined,
/// and the attribute table the drawn segments carry into GIS.
/// </summary>
public sealed partial class ExporterViewModel
{
    private bool _snapProposedMainSegmentEnds = true;
    private double _proposedMainSnapToleranceFeet = 1.0;
    private string _proposedMainAttributeStatus = string.Empty;
    private bool _uploadProposedMainToGis = true;
    private ProposedMainLayerSchema _proposedMainSchema = new();

    /// <summary>
    /// Whether segment ends close to each other are pulled together. On, because a corridor drawn in
    /// pieces is meant to be continuous and a gap of an inch is a drawing error rather than a decision.
    /// Off is for the case where two mains genuinely stop near each other without meeting.
    /// </summary>
    public bool SnapProposedMainSegmentEnds
    {
        get => _snapProposedMainSegmentEnds;
        set
        {
            if (SetProperty(ref _snapProposedMainSegmentEnds, value)) { RaisePropertyChanged(nameof(SnapSummary)); }
        }
    }

    /// <summary>
    /// How close two segment ends have to be before they are treated as the same point.
    ///
    /// It was a foot, fixed, with nothing on the page to say so or to change it. A foot suits a street
    /// but not a plant yard where mains run a few inches apart, and not a sketch drawn well zoomed out
    /// where ends land further apart than that and were being left unjoined.
    /// </summary>
    public double ProposedMainSnapToleranceFeet
    {
        get => _proposedMainSnapToleranceFeet;
        set
        {
            // Clamped rather than validated. A negative tolerance has no meaning, and one large enough
            // to swallow a whole segment would join ends that were never meant to meet.
            var clamped = Math.Clamp(double.IsNaN(value) ? 1.0 : value, 0.0, 100.0);
            if (SetProperty(ref _proposedMainSnapToleranceFeet, clamped)) { RaisePropertyChanged(nameof(SnapSummary)); }
        }
    }

    /// <summary>What the snapping will do, in a sentence, so the setting is not read as decoration.</summary>
    public string SnapSummary => !SnapProposedMainSegmentEnds
        ? "Segment ends are left exactly where they are drawn."
        : ProposedMainSnapToleranceFeet <= 0
            ? "Snapping is on but the tolerance is zero, so only ends drawn on the same point are joined."
            : "Segment ends within " + ProposedMainSnapToleranceFeet.ToString("0.##") + " ft of each other are joined.";

    /// <summary>
    /// Whether the hand drawn main is written to the GIS layer on the way to page 3.
    ///
    /// On, because that is what drawing one is for. Left switchable because this is the only thing in
    /// this application that writes to GIS, and a user practising or re-running an export should be
    /// able to say no without having to avoid page 3.
    /// </summary>
    public bool UploadProposedMainToGis
    {
        get => _uploadProposedMainToGis;
        set => SetProperty(ref _uploadProposedMainToGis, value);
    }

    /// <summary>
    /// The layer's fields, in the order the service lists them. The table's columns are built from
    /// this, so a field added in GIS becomes a column without anything here being changed.
    /// </summary>
    public IReadOnlyList<ProposedMainField> ProposedMainFields => _proposedMainSchema.Fields;

    /// <summary>The layer's fields, subtypes and domains, which the table's columns are built from.</summary>
    public ProposedMainLayerSchema ProposedMainSchema => _proposedMainSchema;

    /// <summary>One row per drawn segment, because each becomes its own feature in GIS.</summary>
    public ObservableCollection<ProposedMainSegmentAttributesViewModel> ProposedMainSegmentRows { get; } = new();

    /// <summary>Raised when the columns change, so the view can rebuild them.</summary>
    public event Action? ProposedMainFieldsChanged;

    /// <summary>Whether there is a table worth showing, so an empty one stays out of the way.</summary>
    public bool HasProposedMainAttributes => _proposedMainSchema.Fields.Count > 0 && ProposedMainSegmentRows.Count > 0;

    /// <summary>What the attribute table is doing: what was read, what is missing, what was written.</summary>
    public string ProposedMainAttributeStatus
    {
        get => _proposedMainAttributeStatus;
        set => SetProperty(ref _proposedMainAttributeStatus, value);
    }

    private bool _hasProposedMainRowSelected;

    /// <summary>
    /// Whether a row is picked out at the moment. Only there to say whether Clear selection has
    /// anything to clear, so the button is not offered when nothing is lit.
    /// </summary>
    public bool HasProposedMainRowSelected
    {
        get => _hasProposedMainRowSelected;
        set => SetProperty(ref _hasProposedMainRowSelected, value);
    }

    /// <summary>
    /// What is still missing, named by segment, so a message points at the row to fix rather than
    /// saying that something somewhere is incomplete.
    /// </summary>
    public IReadOnlyList<string> MissingRequiredProposedMainAttributes =>
        ProposedMainSegmentRows
            .Where(r => !r.IsComplete)
            .Select(r => r.SegmentDisplay + ": " + string.Join(", ", r.Missing))
            .ToList();

    /// <summary>Takes the layer's schema and rebuilds the columns from it.</summary>
    public void LoadProposedMainSchema(ProposedMainLayerSchema schema)
    {
        _proposedMainSchema = schema;

        // Rows built against the previous schema would offer the previous lists, so they go and are
        // rebuilt from the segments still drawn.
        var segmentCount = ProposedMainSegmentRows.Count;
        ProposedMainSegmentRows.Clear();

        RaisePropertyChanged(nameof(ProposedMainFields));
        RaisePropertyChanged(nameof(ProposedMainSchema));
        ProposedMainFieldsChanged?.Invoke();

        var fields = schema.Fields;
        var required = fields.Count(f => f.Required);
        var subtypes = schema.HasSubtypes
            ? " " + schema.Subtypes.Count + " subtype(s) narrow what the other fields may hold."
            : string.Empty;

        ProposedMainAttributeStatus = fields.Count == 0
            ? "The proposed main layer offered no fields to fill in."
            : "Read " + fields.Count + " field(s) from the proposed main layer, " + required
              + " of them required." + subtypes;

        SyncProposedMainSegmentRows(segmentCount);
    }

    /// <summary>
    /// Brings the table in line with the segments on the map: one row each, numbered as they are.
    ///
    /// Rows that already exist keep what has been typed into them, so drawing another segment does not
    /// disturb the ones already filled in. A new row starts as a copy of the one before it, because
    /// consecutive segments of one corridor are usually the same pipe and retyping identical values
    /// for each is the sort of work nobody checks carefully by the fourth time.
    /// </summary>
    public void SyncProposedMainSegmentRows(int segmentCount)
    {
        if (segmentCount < 0) { segmentCount = 0; }

        while (ProposedMainSegmentRows.Count > segmentCount)
        {
            ProposedMainSegmentRows.RemoveAt(ProposedMainSegmentRows.Count - 1);
        }

        while (ProposedMainSegmentRows.Count < segmentCount)
        {
            var row = new ProposedMainSegmentAttributesViewModel(ProposedMainSegmentRows.Count + 1, _proposedMainSchema);
            if (ProposedMainSegmentRows.Count > 0) { row.CopyValuesFrom(ProposedMainSegmentRows[^1]); }
            row.ValuesChanged += _ => RaisePropertyChanged(nameof(MissingRequiredProposedMainAttributes));
            ProposedMainSegmentRows.Add(row);
        }

        RaiseProposedMainTableChanged();
    }

    private void RaiseProposedMainTableChanged()
    {
        RaisePropertyChanged(nameof(HasProposedMainAttributes));
        RaisePropertyChanged(nameof(MissingRequiredProposedMainAttributes));
    }

    /// <summary>The attributes to send for each segment, in the order the segments were drawn.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> BuildProposedMainAttributeValues() =>
        ProposedMainSegmentRows.Select(r => r.ToAttributeValues()).ToList();
}
