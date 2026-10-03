using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Controls;

/// <summary>
/// Read-only segment content: text, with codes as chips (design section 1). Chips are <c>Border.chip</c> elements
/// with the classes <c>opening</c>, <c>closing</c> or <c>standalone</c>, and <c>protected</c> for codes that can't be
/// deleted; their look comes from styles, so states can be added without touching this control.
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
                ChipRun chip => new InlineUIContainer(Chip(chip)) { BaselineAlignment = BaselineAlignment.Center },
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

    private static Border Chip(ChipRun chip)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        label.Children.Add(new TextBlock { Classes = { "label" }, Text = chip.Text });
        if (chip.IsProtected)
            label.Children.Add(new TextBlock { Classes = { "lock" }, Text = "🔒" });

        var border = new Border
        {
            Classes = { "chip", KindClass(chip.Kind) },
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        if (chip.IsProtected)
            border.Classes.Add("protected");
        ToolTip.SetTip(border, chip.ToolTip);
        return border;
    }

    private static string KindClass(InlineCodeKind kind) => kind switch
    {
        InlineCodeKind.Opening => "opening",
        InlineCodeKind.Closing => "closing",
        _ => "standalone",
    };
}
