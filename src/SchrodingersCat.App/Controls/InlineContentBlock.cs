using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SchrodingersCat.App.ViewModels;

namespace SchrodingersCat.App.Controls;

/// <summary>
/// Read-only segment content: text, with codes as chips (design section 1; see <see cref="Chips"/>).
/// </summary>
public sealed class InlineContentBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<ContentRun>?> RunsProperty =
        AvaloniaProperty.Register<InlineContentBlock, IReadOnlyList<ContentRun>?>(nameof(Runs));

    private static readonly TextDecorationCollection TermUnderline =
    [
        new TextDecoration { Location = TextDecorationLocation.Underline, StrokeDashArray = new AvaloniaList<double>([1, 2]) },
    ];

    private static readonly IBrush CommentBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD5, 0x4F));
    private static readonly IBrush NoTranslateBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    static InlineContentBlock()
    {
        RunsProperty.Changed.AddClassHandler<InlineContentBlock>((block, _) => block.Rebuild());
    }

    public IReadOnlyList<ContentRun>? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        foreach (var run in Runs ?? [])
        {
            inlines.Add(run switch
            {
                TextRun text => TextInline(text),
                ChipRun chip => new InlineUIContainer(Chips.Create(chip)) { BaselineAlignment = BaselineAlignment.Center },
                _ => throw new InvalidOperationException($"Unknown run {run.GetType().Name}."),
            });
        }

        Inlines = inlines;
    }

    private static Run TextInline(TextRun text)
    {
        var run = new Run(text.Text);
        if (text.Annotations.HasFlag(AnnotationStyle.Term))
            run.TextDecorations = TermUnderline;
        if (text.Annotations.HasFlag(AnnotationStyle.NoTranslate))
            run.Background = NoTranslateBackground;
        else if (text.Annotations.HasFlag(AnnotationStyle.Comment))
            run.Background = CommentBackground;
        return run;
    }
}
