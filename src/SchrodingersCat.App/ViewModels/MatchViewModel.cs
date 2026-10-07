using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// One match in the matches pane (design section 6): its score, where it comes from, its source with the differences
/// from the current source, and its translation as it would be inserted, with the current source's codes.
/// </summary>
public sealed class MatchViewModel
{
    /// <param name="number">The match's place in the pane, 1–9: Ctrl+<paramref name="number"/> inserts it.</param>
    /// <param name="fromFile">The XLIFF file carried the match (matches module), rather than the open TM.</param>
    public MatchViewModel(int number, TmMatch match, bool fromFile, SegmentViewModel segment)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(segment);

        Number = number;
        Match = match;
        IsFromFile = fromFile;
        Segment = segment;
        Target = CodeTransfer.Apply(match.Entry, segment.Source);
        SourceRuns = ContentRuns.BuildDiff(SourceDiff.Compare(segment.Source, match.Entry.Source), segment.Unit, segment.Hints);
        TargetRuns = ContentRuns.Build(Target.Target, segment.Unit, segment.Hints, []);
        InsertCommand = new RelayCommand(Insert);
    }

    public int Number { get; }

    public TmMatch Match { get; }

    public bool IsFromFile { get; }

    /// <summary>The segment the match is for.</summary>
    public SegmentViewModel Segment { get; }

    /// <summary>The translation with the current source's codes: what inserting the match puts into the target.</summary>
    public TransferredTarget Target { get; }

    /// <summary>The key that inserts the match.</summary>
    public string Shortcut => $"Ctrl+{Number}";

    public string Score => $"{Match.Score}%";

    /// <summary>A 100% or 101% match.</summary>
    public bool IsExact => Match.IsExact;

    /// <summary>The match's source, with what differs from the current source marked.</summary>
    public IReadOnlyList<ContentRun> SourceRuns { get; }

    public IReadOnlyList<ContentRun> TargetRuns { get; }

    /// <summary>Where the match comes from, in one line: the TM or the file's origin, the document, the date and the user.</summary>
    public string Details
    {
        get
        {
            var entry = Match.Entry;
            var origin = IsFromFile ? (Match.Origin is null ? "From the file" : $"From the file: {Match.Origin}") : Match.Origin;
            var date = entry.Changed == default ? null : entry.Changed.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return string.Join(" · ", new[] { origin, entry.Document is null ? null : Path.GetFileName(entry.Document), date, entry.ChangedBy }
                .Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    /// <summary>Inserts the match into its segment's target (what Ctrl+<see cref="Number"/> does).</summary>
    public IRelayCommand InsertCommand { get; }

    private void Insert() => Segment.InsertMatch(this);
}
