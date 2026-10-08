using System.Globalization;
using System.Windows.Input;
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
    /// <param name="editCommand">Edits a <see cref="StoredEntry"/> (the window's); null where entries can't be changed.</param>
    /// <param name="deleteCommand">Deletes a <see cref="StoredEntry"/> (the window's).</param>
    public MatchViewModel(int number, TmMatch match, bool fromFile, SegmentViewModel segment, ICommand? editCommand = null, ICommand? deleteCommand = null)
    {
        EditCommand = editCommand;
        DeleteCommand = deleteCommand;
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
    public string Details =>
        DetailsOf(IsFromFile ? (Match.Origin is null ? "From the file" : $"From the file: {Match.Origin}") : Match.Origin, Match.Entry);

    /// <summary>A TM entry's origin, document, date and user, in one line (also for concordance results).</summary>
    internal static string DetailsOf(string? origin, TmEntry entry)
    {
        var date = entry.Changed == default ? null : entry.Changed.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.Join(" · ", new[] { origin, entry.Document is null ? null : Path.GetFileName(entry.Document), date, entry.ChangedBy }
            .Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>The TM entry behind the match, for editing or deleting it; null for a match from the file.</summary>
    public StoredEntry? Stored => !IsFromFile && Match.EntryId is { } id ? new StoredEntry(id, Match.Entry) : null;

    public ICommand? EditCommand { get; }

    public ICommand? DeleteCommand { get; }

    /// <summary>The key that deletes the match's entry.</summary>
    public string DeleteShortcut => $"Ctrl+Shift+{Number}";

    /// <summary>Inserts the match into its segment's target (what Ctrl+<see cref="Number"/> does).</summary>
    public IRelayCommand InsertCommand { get; }

    private void Insert() => Segment.InsertMatch(this);
}
