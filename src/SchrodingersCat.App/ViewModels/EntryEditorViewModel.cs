using CommunityToolkit.Mvvm.ComponentModel;
using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// Editing a TM entry's translation (section 3 of <c>docs/design/tm-maintenance.md</c>): the entry as a one-segment
/// unit, so the translation is edited with the same <see cref="TargetEditModel"/> and rules as in the grid.
/// </summary>
public sealed partial class EntryEditorViewModel : ObservableObject
{
    private readonly TranslationUnit _unit;
    private readonly Segment _segment;
    private readonly HashSet<(InlineCodeIssueKind, string)> _errorsBefore;

    public EntryEditorViewModel(TmEntry entry, string memoryName)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        ArgumentNullException.ThrowIfNull(memoryName);

        _segment = new Segment { Source = entry.Source, Target = entry.Target, State = SegmentState.Translated };
        _unit = new TranslationUnit { Id = "entry" };
        _unit.Segments.Add(_segment);
        Model = new TargetEditModel(_unit, _segment);
        Model.Changed += (_, _) =>
        {
            Problem = null;
            OnPropertyChanged(nameof(TargetProblemCodes));
        };

        Details = MatchViewModel.DetailsOf(memoryName, entry);
        SourceRuns = ContentRuns.Build(entry.Source, _unit, Model.Hints, []);
        _errorsBefore = Errors();
    }

    /// <summary>The entry as it is stored.</summary>
    public TmEntry Entry { get; }

    /// <summary>The translation being edited.</summary>
    public TargetEditModel Model { get; }

    /// <summary>The TM, document, date and user of the entry, in one line.</summary>
    public string Details { get; }

    public IReadOnlyList<ContentRun> SourceRuns { get; }

    /// <summary>The editor's message for the last edit (two-way with the editor), shown under it.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Why the last attempt to save was refused; cleared by the next edit.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>The codes the edit has given errors, for the chips' outline.</summary>
    public IReadOnlySet<string> TargetProblemCodes =>
        NewErrors().Select(i => i.CodeId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The translation to store, or null with <see cref="Problem"/> set if it can't be: an entry needs a translation
    /// (delete it instead), and the edit may not add code errors. Errors the entry had already stay allowed: an entry
    /// can hold half a pair whose other half was in the next segment, which a unit of one segment always reports.
    /// </summary>
    public InlineContent? TrySave()
    {
        if (_segment.Target is not { IsEmpty: false } target)
        {
            Problem = "An entry needs a translation. To remove it from the TM, delete the entry instead.";
            return null;
        }

        if (NewErrors().FirstOrDefault() is { } error)
        {
            Problem = $"The translation has code errors: {error.Message}";
            return null;
        }

        Problem = null;
        return target;
    }

    private IEnumerable<InlineCodeIssue> NewErrors() =>
        InlineCodeChecker.Check(_unit).Where(i => i.Severity == IssueSeverity.Error && !_errorsBefore.Contains((i.Kind, i.CodeId)));

    private HashSet<(InlineCodeIssueKind, string)> Errors() =>
        [.. InlineCodeChecker.Check(_unit).Where(i => i.Severity == IssueSeverity.Error).Select(i => (i.Kind, i.CodeId))];
}
