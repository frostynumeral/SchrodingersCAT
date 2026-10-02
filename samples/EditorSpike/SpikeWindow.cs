using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace EditorSpike;

/// <summary>Source on top, the chip editor below, then the model's content and status, for checking by eye.</summary>
public sealed class SpikeWindow : Window
{
    public SpikeWindow(TranslationUnit unit)
    {
        var segment = unit.Segments[0];
        Title = "SchrödingersCAT editor spike (AvaloniaEdit)";
        Width = 900;
        Height = 420;

        ChipEditor = new ChipEditor(unit, segment) { Height = 120 };
        StatusText = new TextBlock { Name = "Status", Foreground = Avalonia.Media.Brushes.DarkOrange, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        ContentText = new SelectableTextBlock { Name = "TargetContent", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.7 };

        ChipEditor.StatusChanged += (_, _) => StatusText.Text = ChipEditor.Status;
        ChipEditor.Model.Changed += (_, _) => ContentText.Text = InlineFormatter.Format(segment.Target ?? InlineContent.Empty);
        ContentText.Text = InlineFormatter.Format(segment.Target ?? InlineContent.Empty);

        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Source", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new SelectableTextBlock { Text = InlineFormatter.Format(segment.Source), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Target (F9: next missing code, Ctrl+Insert: copy source, Ctrl+Z/Y: undo/redo)", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new Border { BorderBrush = Avalonia.Media.Brushes.Gray, BorderThickness = new Thickness(1), Child = ChipEditor },
                StatusText,
                new TextBlock { Text = "What the model holds", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                ContentText,
            },
        };
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    public ChipEditor ChipEditor { get; }

    public TextBlock StatusText { get; }

    public SelectableTextBlock ContentText { get; }
}
