using System.Windows.Input;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Controls;

/// <summary>
/// Read-only segment content: text, with codes as chips (design section 1; see <see cref="Chips"/>). Clicking a chip
/// runs <see cref="ChipCommand"/> with its <see cref="ChipRun"/>, if the command can execute.
/// </summary>
public sealed class InlineContentBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<ContentRun>?> RunsProperty =
        AvaloniaProperty.Register<InlineContentBlock, IReadOnlyList<ContentRun>?>(nameof(Runs));

    public static readonly StyledProperty<ICommand?> ChipCommandProperty =
        AvaloniaProperty.Register<InlineContentBlock, ICommand?>(nameof(ChipCommand));

    public static readonly StyledProperty<TranslationUnit?> ClipboardUnitProperty =
        AvaloniaProperty.Register<InlineContentBlock, TranslationUnit?>(nameof(ClipboardUnit));

    private static readonly TextDecorationCollection TermUnderline =
    [
        new TextDecoration { Location = TextDecorationLocation.Underline, StrokeDashArray = new AvaloniaList<double>([1, 2]) },
    ];

    // A termbase term (section 6 of docs/design/termbase.md): solid, unlike the dotted underline of the file's own term
    // annotations.
    private static readonly TextDecorationCollection TermbaseUnderline = [new TextDecoration { Location = TextDecorationLocation.Underline }];

    private static readonly TextDecorationCollection RemovedStrikethrough = [new TextDecoration { Location = TextDecorationLocation.Strikethrough }];

    private static readonly IBrush RemovedForeground = new ImmutableSolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly IBrush AddedBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0x43, 0xA0, 0x47));
    private static readonly IBrush HitBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xC1, 0x07));
    private static readonly IBrush CommentBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD5, 0x4F));
    private static readonly IBrush NoTranslateBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    private bool _stale;

    // Some run is a termbase term with a tooltip, so moving the pointer looks for the run under it.
    private bool _hasTermTips;

    static InlineContentBlock()
    {
        RunsProperty.Changed.AddClassHandler<InlineContentBlock>((block, _) => block.Rebuild());
        IsVisibleProperty.Changed.AddClassHandler<InlineContentBlock>((block, _) =>
        {
            if (block.IsVisible && block._stale)
                block.Rebuild();
        });
    }

    public IReadOnlyList<ContentRun>? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    /// <summary>Run when a chip is clicked, with the chip's <see cref="ChipRun"/> as the parameter.</summary>
    public ICommand? ChipCommand
    {
        get => GetValue(ChipCommandProperty);
        set => SetValue(ChipCommandProperty, value);
    }

    /// <summary>
    /// The unit the runs belong to. When set, copying (Ctrl+C) keeps the chips, so pasting into a target of the same
    /// unit inserts the codes (see <see cref="SegmentClipboard"/>).
    /// </summary>
    public TranslationUnit? ClipboardUnit
    {
        get => GetValue(ClipboardUnitProperty);
        set => SetValue(ClipboardUnitProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var copy = e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control;
        if (copy && ClipboardUnit is { } unit && SelectionStart != SelectionEnd && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            var content = ContentRuns.Slice(Runs ?? [], Math.Min(SelectionStart, SelectionEnd), Math.Max(SelectionStart, SelectionEnd));
            SegmentClipboard.SetAsync(clipboard, content, unit).Forget();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // A Run can't have a tooltip of its own: the block's tooltip follows the termbase term under the pointer.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnPointerMoved(e);
        if (!_hasTermTips)
            return;

        var point = e.GetPosition(this) - new Point(Padding.Left, Padding.Top);
        var hit = TextLayout.HitTestPoint(point);
        var tip = hit.IsInside ? ContentRuns.RunAt(Runs ?? [], hit.CharacterHit.FirstCharacterIndex)?.TermToolTip : null;
        if (!Equals(ToolTip.GetTip(this), tip))
            ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hasTermTips)
            ToolTip.SetTip(this, null);
    }

    // While hidden, the runs aren't shown: Avalonia still renders a hidden TextBlock whose inlines changed, measures
    // the new, never-measured chips during that render, and throws "Visual was invalidated during the render pass".
    // So a hidden block (the active row's read-only target) rebuilds when it is shown again.
    private void Rebuild()
    {
        _stale = !IsVisible;
        if (_stale)
            return;

        var hadTermTips = _hasTermTips;
        _hasTermTips = Runs?.Any(r => r is TextRun { TermToolTip: not null }) == true;
        if (hadTermTips)
            ToolTip.SetTip(this, null); // the runs changed: the pointer has to move again to find a term
        var inlines = new InlineCollection();
        foreach (var run in Runs ?? [])
        {
            inlines.Add(run switch
            {
                TextRun text => TextInline(text),
                ChipRun chip => new InlineUIContainer(ClickableChip(chip)) { BaselineAlignment = BaselineAlignment.Center },
                _ => throw new InvalidOperationException($"Unknown run {run.GetType().Name}."),
            });
        }

        Inlines = inlines;
    }

    // A click the command takes doesn't also start a text selection or reach the list (which would select the row).
    private Border ClickableChip(ChipRun chip)
    {
        var border = Chips.Create(chip);
        border.PointerPressed += (_, e) =>
        {
            if (ChipCommand is { } command && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed && command.CanExecute(chip))
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(() => command.Execute(chip));
            }
        };
        return border;
    }

    // In a match's source (design section 6): what the current source lacks is struck through, what it adds is
    // highlighted. The difference wins over annotations, which a match's source doesn't show anyway.
    private static Run TextInline(TextRun text)
    {
        var run = new Run(text.Text);
        if (text.IsHit)
        {
            run.FontWeight = FontWeight.Bold;
            run.Background = HitBackground;
            return run;
        }

        if (text.Difference == DiffKind.Removed)
        {
            run.TextDecorations = RemovedStrikethrough;
            run.Foreground = RemovedForeground;
            return run;
        }

        if (text.Difference == DiffKind.Added)
        {
            run.Background = AddedBackground;
            return run;
        }

        // A termbase term that is also a term annotation shows as the termbase's; its tooltip says what it is.
        if (text.Annotations.HasFlag(AnnotationStyle.TermbaseTerm))
            run.TextDecorations = TermbaseUnderline;
        else if (text.Annotations.HasFlag(AnnotationStyle.Term))
            run.TextDecorations = TermUnderline;
        if (text.Annotations.HasFlag(AnnotationStyle.NoTranslate))
            run.Background = NoTranslateBackground;
        else if (text.Annotations.HasFlag(AnnotationStyle.Comment))
            run.Background = CommentBackground;
        return run;
    }
}
