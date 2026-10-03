using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly TranslationUnit _unit;
    private readonly Segment _segment;
    private readonly Action _changed;
    private readonly InlineCodeHints _hints;
    private TargetEditModel? _editModel;

    public SegmentViewModel(TranslationUnit unit, Segment segment, Action changed)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));

        Label = segment.Id is null ? unit.Id : $"{unit.Id} / {segment.Id}";
        SourceText = InlineFormatter.Format(segment.Source);
        _hints = new InlineCodeHints(unit);
        SourceRuns = ContentRuns.Build(segment.Source, unit, _hints, OpenMarkersBefore(s => s.Source));

        // Editing content with inline codes as plain text would silently drop the codes.
        IsEditable = !segment.Source.HasTags && segment.Target is not { HasTags: true };
    }

    public string Label { get; }

    public string SourceText { get; }

    /// <summary>The source with codes as chips.</summary>
    public IReadOnlyList<ContentRun> SourceRuns { get; }

    /// <summary>The target with codes as chips, for segments that can't be edited as text; empty when not translated.</summary>
    public IReadOnlyList<ContentRun> TargetRuns => _segment.Target is null
        ? []
        : ContentRuns.Build(_segment.Target, _unit, _hints, OpenMarkersBefore(s => s.Target ?? InlineContent.Empty));

    public bool IsEditable { get; }

    /// <summary>The selected segment; its target is edited in the editor control.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(ActiveEditModel))]
    public partial bool IsActive { get; set; }

    /// <summary>The target is shown in the editor control (the segment is active and editable).</summary>
    public bool IsEditing => IsActive && IsEditable;

    /// <summary>The edit model while <see cref="IsEditing"/>, else null, so inactive rows don't create one.</summary>
    public TargetEditModel? ActiveEditModel => IsEditing ? EditModel : null;

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

    public string? ReadOnlyReason => IsEditable ? null : "This segment contains inline codes, which can't be edited yet.";

    public string State => _segment.State.ToString();

    /// <summary>
    /// The target as text. Setting it replaces the target, through the edit model so it can be undone; an empty text
    /// means "not translated" (no target).
    /// </summary>
    public string TargetText
    {
        get => _segment.Target is null ? "" : InlineFormatter.Format(_segment.Target);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsEditable || value == TargetText)
                return;

            var model = EditModel;
            model.Select(0, model.Length);
            if (value.Length == 0)
                model.Delete(DeleteDirection.Backward);
            else
                model.InsertText(value);
        }
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
        OnPropertyChanged(nameof(TargetRuns));
        _changed();
    }

    // An annotation can span segments, so text at the start of this one may still be inside one.
    private IReadOnlyList<InlineMarker> OpenMarkersBefore(Func<Segment, InlineContent> side) =>
        ContentRuns.OpenMarkersAfter(_unit.Segments.TakeWhile(s => !ReferenceEquals(s, _segment)).SelectMany(side));
}
