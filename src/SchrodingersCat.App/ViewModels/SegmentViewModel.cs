using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

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
    private IReadOnlyList<InlineCodeIssue> _issues = [];

    /// <param name="check">The unit's code check, shared with the unit's other segments.</param>
    public SegmentViewModel(UnitCheck check, Segment segment, Action changed)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _unit = check.Unit;

        Label = segment.Id is null ? _unit.Id : $"{_unit.Id} / {segment.Id}";
        SourceText = InlineFormatter.Format(segment.Source);
        UpdateIssues();
        _check.Checked += OnUnitChecked;
    }

    public string Label { get; }

    /// <summary>The unit the segment belongs to.</summary>
    public TranslationUnit Unit => _unit;

    public string SourceText { get; }

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

    // The first translation moves the segment from Initial to Translated; nothing moves it back (decision 8).
    private void OnTargetEdited(object? sender, EventArgs e)
    {
        if (_segment.Target is not null && _segment.State == SegmentState.Initial)
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
