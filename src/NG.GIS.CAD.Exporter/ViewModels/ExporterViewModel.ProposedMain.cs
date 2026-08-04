using System.Collections.ObjectModel;
using NG.GIS.CAD.Exporter.Services;

namespace NG.GIS.CAD.Exporter.ViewModels;

/// <summary>
/// Page 2's side of a hand drawn proposed main: how far apart segment ends may be and still be joined,
/// and the attributes the drawn main will carry into GIS.
/// </summary>
public sealed partial class ExporterViewModel
{
    private bool _snapProposedMainSegmentEnds = true;
    private double _proposedMainSnapToleranceFeet = 1.0;
    private string _proposedMainAttributeStatus = string.Empty;
    private bool _uploadProposedMainToGis = true;

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

    /// <summary>The attribute rows read from the layer, in the order the service lists its fields.</summary>
    public ObservableCollection<ProposedMainAttributeViewModel> ProposedMainAttributes { get; } = new();

    /// <summary>Whether there is a table to show at all, so an empty one can be hidden rather than shown empty.</summary>
    public bool HasProposedMainAttributes => ProposedMainAttributes.Count > 0;

    /// <summary>What the attribute table is doing: loading, what is missing, or what was written.</summary>
    public string ProposedMainAttributeStatus
    {
        get => _proposedMainAttributeStatus;
        set => SetProperty(ref _proposedMainAttributeStatus, value);
    }

    /// <summary>The required fields still empty, by their labels, for a message that names them.</summary>
    public IReadOnlyList<string> MissingRequiredProposedMainAttributes =>
        ProposedMainAttributes.Where(a => a.IsMissing).Select(a => a.Field.Display).ToList();

    /// <summary>
    /// Fills the table from the layer's own fields, keeping anything already typed against a field of
    /// the same name so a reload does not throw the user's work away.
    /// </summary>
    public void LoadProposedMainAttributes(IReadOnlyList<ProposedMainField> fields)
    {
        var typed = ProposedMainAttributes.ToDictionary(a => a.Name, a => a.Value, StringComparer.OrdinalIgnoreCase);

        ProposedMainAttributes.Clear();
        foreach (var field in fields)
        {
            var row = new ProposedMainAttributeViewModel(field);
            if (typed.TryGetValue(field.Name, out var previous)) { row.Value = previous; }
            row.ValueChanged += _ => RaisePropertyChanged(nameof(MissingRequiredProposedMainAttributes));
            ProposedMainAttributes.Add(row);
        }

        RaisePropertyChanged(nameof(HasProposedMainAttributes));
        RaisePropertyChanged(nameof(MissingRequiredProposedMainAttributes));

        var required = fields.Count(f => f.Required);
        ProposedMainAttributeStatus = fields.Count == 0
            ? "The proposed main layer offered no fields to fill in."
            : "Read " + fields.Count + " field(s) from the proposed main layer, " + required + " of them required.";
    }

    /// <summary>
    /// The attributes to send, leaving out the ones nobody filled in. An empty value is not the same as
    /// a value of empty: sending blanks would overwrite whatever GIS would otherwise default them to.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildProposedMainAttributeValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in ProposedMainAttributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Value)) { continue; }
            values[attribute.Name] = attribute.Value.Trim();
        }
        return values;
    }
}
