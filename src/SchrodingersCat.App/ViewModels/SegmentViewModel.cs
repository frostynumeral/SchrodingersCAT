using CommunityToolkit.Mvvm.ComponentModel;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.ViewModels;

/// <summary>One translatable segment as the editor shows it. Edits go straight to the wrapped Core <see cref="Segment"/>.</summary>
public sealed partial class SegmentViewModel : ObservableObject
{
    private readonly TranslationUnit _unit;
    private readonly Segment _segment;
    private readonly Action _changed;
    private readonly InlineCodeHints _hints;

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

    public string? ReadOnlyReason => IsEditable ? null : "This segment contains inline codes, which can't be edited yet.";

    public string State => _segment.State.ToString();

    /// <summary>
    /// The target as text. Setting it replaces the target; an empty text means "not translated" (no target).
    /// The first translation moves the segment from Initial to Translated.
    /// </summary>
    public string TargetText
    {
        get => _segment.Target is null ? "" : InlineFormatter.Format(_segment.Target);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsEditable || value == TargetText)
                return;

            _segment.Target = value.Length == 0 ? null : InlineContent.FromText(value);
            if (_segment.Target is not null && _segment.State == SegmentState.Initial)
            {
                _segment.State = SegmentState.Translated;
                OnPropertyChanged(nameof(State));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetRuns));
            _changed();
        }
    }

    // An annotation can span segments, so text at the start of this one may still be inside one.
    private IReadOnlyList<InlineMarker> OpenMarkersBefore(Func<Segment, InlineContent> side) =>
        ContentRuns.OpenMarkersAfter(_unit.Segments.TakeWhile(s => !ReferenceEquals(s, _segment)).SelectMany(side));
}
