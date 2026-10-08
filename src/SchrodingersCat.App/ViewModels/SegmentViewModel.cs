using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// One translatable segment as the editor shows it. Edits go through a <see cref="TargetEditModel"/>, which writes
/// them straight to the wrapped Core <see cref="Segment"/>.
/// </summary>
public sealed partial class SegmentViewModel : ObservableObject
{
    private readonly UnitCheck _check;
    private readonly TranslationUnit _unit;
    private readonly Segment _segment;
    private readonly Action _changed;
    private TargetEditModel? _editModel;
    private IReadOnlyList<TmMatch>? _fileMatches;
    private bool _keepState;
    private bool _autoFilled;
    private IReadOnlyList<InlineCodeIssue> _issues = [];

    /// <param name="check">The unit's code check, shared with the unit's other segments.</param>
    public SegmentViewModel(UnitCheck check, Segment segment, Action changed)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _unit = check.Unit;

        Label = segment.Id is null ? _unit.Id : $"{_unit.Id} / {segment.Id}";
        StoredTarget = segment.State >= SegmentState.Translated ? segment.Target : null;
        SourceText = InlineFormatter.Format(segment.Source);
        UpdateIssues();
        _check.Checked += OnUnitChecked;
    }

    public string Label { get; }

    /// <summary>The unit the segment belongs to.</summary>
    public TranslationUnit Unit => _unit;

    public string SourceText { get; }

    /// <summary>The segment's source, for looking up matches.</summary>
    public InlineContent Source => _segment.Source;

    /// <summary>The segment's target; null while it isn't translated.</summary>
    public InlineContent? Target => _segment.Target;

    /// <summary>
    /// The translation of this segment the TM is assumed to hold: what Ctrl+Enter stored last, or, for a segment that
    /// was already confirmed when the file was opened, its target then. Storing a corrected translation replaces it.
    /// </summary>
    public InlineContent? StoredTarget { get; set; }

    /// <summary>
    /// The segment can take an exact match automatically (decision 2): it has no target, and hasn't had one inserted
    /// automatically yet, so undoing an automatic insertion sticks.
    /// </summary>
    public bool CanAutoInsert => _segment.Target is null && !_autoFilled;

    /// <summary>What the unit's source says about its codes.</summary>
    public InlineCodeHints Hints => _check.Hints;

    /// <summary>
    /// The matches the XLIFF file carries for this segment (matches module, design section 5): those whose <c>ref</c>
    /// points to it, and in a unit with only this segment to translate, also those that point elsewhere in it (e.g.
    /// at an annotation). Read once.
    /// </summary>
    public IReadOnlyList<TmMatch> FileMatches => _fileMatches ??= [.. XliffMatches.Read(_unit)
        .Where(m => m.SegmentId is { } id ? id == _segment.Id : _unit.TranslatableSegments.Count() == 1)
        .Select(m => m.Match)];

    /// <summary>The source with codes as chips; codes missing from the target are marked as problems.</summary>
    public IReadOnlyList<ContentRun> SourceRuns =>
        ContentRuns.MarkProblems(
            ContentRuns.Build(_segment.Source, _unit, _check.Hints, OpenMarkersBefore(s => s.Source)),
            ProblemCodes(IsMissing));

    /// <summary>The target with codes as chips, for the rows not being edited; empty when not translated.</summary>
    public IReadOnlyList<ContentRun> TargetRuns => _segment.Target is null
        ? []
        : ContentRuns.MarkProblems(
            ContentRuns.Build(_segment.Target, _unit, _check.Hints, OpenMarkersBefore(s => s.Target ?? InlineContent.Empty)),
            TargetProblemCodes);

    /// <summary>The ids of the target's codes that the code check reports a problem with, for the chips' outline.</summary>
    public IReadOnlySet<string> TargetProblemCodes => ProblemCodes(i => !IsMissing(i));

    /// <summary>The code check's issues that show up in this segment.</summary>
    public IReadOnlyList<InlineCodeIssue> Issues => _issues;

    public bool HasIssues => _issues.Count > 0;

    public bool HasErrors => _issues.Any(i => i.Severity == IssueSeverity.Error);

    public bool HasWarnings => _issues.Any(i => i.Severity == IssueSeverity.Warning);

    /// <summary>The issues in words, one per line, for the badge's tooltip.</summary>
    public string? IssueSummary => HasIssues ? string.Join("\n", _issues.Select(i => $"{i.Severity}: {i.Message}")) : null;

    /// <summary>The selected segment; its target is edited in the editor control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveEditModel))]
    [NotifyCanExecuteChangedFor(nameof(InsertCodeCommand))]
    public partial bool IsActive { get; set; }

    /// <summary>The edit model while <see cref="IsActive"/>, else null, so inactive rows don't create one.</summary>
    public TargetEditModel? ActiveEditModel => IsActive ? EditModel : null;

    /// <summary>
    /// The target's edit model, created on first use and kept while the document is open, so each segment keeps
    /// its own undo history (design section 5).
    /// </summary>
    public TargetEditModel EditModel
    {
        get
        {
            if (_editModel is null)
            {
                _editModel = new TargetEditModel(_unit, _segment);
                _editModel.Changed += OnTargetEdited;
            }

            return _editModel;
        }
    }

    /// <summary>The editor's last refusal or note for this segment, for the status line.</summary>
    [ObservableProperty]
    public partial string? EditStatus { get; set; }

    public string State => _segment.State.ToString();

    /// <summary>The target has content (copying the source over it asks first).</summary>
    public bool HasTarget => _segment.Target is { IsEmpty: false };

    /// <summary>
    /// Confirms the translation (Ctrl+Enter): an Initial segment becomes Translated; later states stay. Refused, with
    /// a status message, when there is no translation yet.
    /// </summary>
    public bool Confirm()
    {
        if (_segment.Target is null)
        {
            EditStatus = "Translate the segment before confirming it.";
            return false;
        }

        if (_segment.State == SegmentState.Initial)
        {
            _segment.State = SegmentState.Translated;
            OnPropertyChanged(nameof(State));
            _changed();
        }

        return true;
    }

    /// <summary>Replaces the target with the source (Ctrl+Insert), as one undoable edit.</summary>
    public void CopySource() => EditStatus = EditModel.CopySource().Message;

    /// <summary>
    /// Replaces the target with a match's translation (Ctrl+1 … Ctrl+9, design section 6), with the codes transferred
    /// onto this segment's, as one undoable edit. The status says which match went in, and whether codes are missing.
    /// </summary>
    /// <param name="automatic">
    /// Inserted on entering the segment (decision 2): the segment stays Initial until the translator confirms it.
    /// </param>
    public void InsertMatch(MatchViewModel match, bool automatic = false)
    {
        ArgumentNullException.ThrowIfNull(match);

        var notes = new List<string> { automatic ? $"Inserted the {match.Score} match automatically. Confirm it with Ctrl+Enter." : $"Inserted the {match.Score} match." };
        _autoFilled |= automatic;
        _keepState = automatic;
        EditResult result;
        try
        {
            result = EditModel.ReplaceTarget(match.Target.Target);
        }
        finally
        {
            _keepState = false;
        }

        if (result.Message is { } message)
            notes.Add(message);
        if (!match.Target.AllCodesPlaced)
            notes.Add("Not every code found its place: check the codes (F9 inserts the next missing one).");
        EditStatus = string.Join(" ", notes);
    }

    /// <summary>
    /// The target as text. Setting it replaces the target, through the edit model so it can be undone, as selecting
    /// everything and typing would: protected codes stay. An empty text means "not translated" (no target).
    /// </summary>
    public string TargetText
    {
        get => _segment.Target is null ? "" : InlineFormatter.Format(_segment.Target);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == TargetText)
                return;

            var model = EditModel;
            model.Select(0, model.Length);
            if (value.Length == 0)
                model.Delete(DeleteDirection.Backward);
            else
                model.InsertText(value);
        }
    }

    /// <summary>Inserts a source code at the editor's caret: what clicking a source chip does (design section 1).</summary>
    [RelayCommand(CanExecute = nameof(IsActive))]
    private void InsertCode(ChipRun chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        EditStatus = EditModel.InsertCode(chip.CodeId).Message;
    }

    // The first translation moves the segment from Initial to Translated; nothing moves it back (decision 8). An exact
    // match inserted automatically doesn't count: the translator hasn't looked at it yet.
    private void OnTargetEdited(object? sender, EventArgs e)
    {
        if (!_keepState && _segment.Target is not null && _segment.State == SegmentState.Initial)
        {
            _segment.State = SegmentState.Translated;
            OnPropertyChanged(nameof(State));
        }

        OnPropertyChanged(nameof(TargetText));
        _check.Run(); // updates this row's chips and those of the unit's other segments
        _changed();
    }

    private void OnUnitChecked(object? sender, EventArgs e)
    {
        UpdateIssues();
        OnPropertyChanged(nameof(Issues));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(IssueSummary));
        OnPropertyChanged(nameof(SourceRuns));
        OnPropertyChanged(nameof(TargetRuns));
        OnPropertyChanged(nameof(TargetProblemCodes));
    }

    private void UpdateIssues() => _issues = [.. _check.Issues.Where(i => ReferenceEquals(i.Segment, _segment))];

    // Missing codes are outlined in the source, where they are; the other problems in the target.
    private static bool IsMissing(InlineCodeIssue issue) =>
        issue.Kind is InlineCodeIssueKind.MissingCode or InlineCodeIssueKind.MissingProtectedCode;

    // Info (reordering, a missing annotation) is normal in translation and doesn't outline anything.
    private HashSet<string> ProblemCodes(Func<InlineCodeIssue, bool> where) =>
        _issues.Where(i => i.Severity >= IssueSeverity.Warning && where(i)).Select(i => i.CodeId).ToHashSet(StringComparer.Ordinal);

    // An annotation can span segments, so text at the start of this one may still be inside one.
    private IReadOnlyList<InlineMarker> OpenMarkersBefore(Func<Segment, InlineContent> side) =>
        ContentRuns.OpenMarkersAfter(_unit.Segments.TakeWhile(s => !ReferenceEquals(s, _segment)).SelectMany(side));
}
