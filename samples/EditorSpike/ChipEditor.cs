using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using SchrodingersCat.App.Editing;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;

namespace EditorSpike;

/// <summary>
/// AvaloniaEdit showing a <see cref="TargetEditModel"/>. The document text holds each code or annotation half as one
/// private-use character, which a <see cref="VisualLineElementGenerator"/> draws as a chip. Because the model counts
/// a chip as one position, document offsets and model positions are the same.
/// </summary>
/// <remarks>
/// AvaloniaEdit never edits the document itself: a read-only section provider blocks its own edits, and typing,
/// Backspace/Delete and undo are routed to the model, whose result is written back. So every XLIFF rule the model
/// enforces holds in the editor too.
/// </remarks>
public sealed class ChipEditor : UserControl
{
    /// <summary>The first private-use character; chip <c>i</c> is <c>U+E000 + i</c>.</summary>
    public const char FirstChip = '';

    private readonly TargetEditModel _model;
    private readonly InlineCodeHints _hints;
    private readonly List<InlineElement> _chips = [];

    public ChipEditor(TranslationUnit unit, Segment segment)
    {
        _model = new TargetEditModel(unit, segment);
        _hints = new InlineCodeHints(unit);

        Editor = new TextEditor
        {
            ShowLineNumbers = false,
            WordWrap = true,
            FontSize = 15,
            Padding = new Thickness(6),
        };
        Editor.TextArea.ReadOnlySectionProvider = new BlockAllEdits();
        Editor.TextArea.TextView.ElementGenerators.Add(new ChipGenerator(this));
        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Editor.Document.UndoStack.SizeLimit = 0; // the model owns undo
        Content = Editor;

        Render();
    }

    public TextEditor Editor { get; }

    public TargetEditModel Model => _model;

    /// <summary>The last refusal or note from the model, for the status line.</summary>
    public string? Status { get; private set; }

    public event EventHandler? StatusChanged;

    /// <summary>The element a private-use character stands for, or null.</summary>
    public InlineElement? ChipAt(char c) => c >= FirstChip && c - FirstChip < _chips.Count ? _chips[c - FirstChip] : null;

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrEmpty(e.Text))
            Apply(() => _model.InsertText(e.Text));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        Func<EditResult>? edit = (e.Key, ctrl) switch
        {
            (Key.Back, _) => () => _model.Delete(DeleteDirection.Backward),
            (Key.Delete, _) => () => _model.Delete(DeleteDirection.Forward),
            (Key.Z, true) => _model.Undo,
            (Key.Y, true) => _model.Redo,
            (Key.F9, _) => _model.InsertNextMissingCode,
            (Key.Insert, true) => _model.CopySource,
            _ => null,
        };

        if (edit is null)
            return;
        e.Handled = true;
        Apply(edit);
    }

    // The editor's caret and selection are the source of truth for where an edit happens.
    private void Apply(Func<EditResult> edit)
    {
        var area = Editor.TextArea;
        var caret = area.Caret.Offset;
        var anchor = area.Selection.IsEmpty
            ? caret
            : area.Selection.SurroundingSegment.Offset == caret ? area.Selection.SurroundingSegment.EndOffset : area.Selection.SurroundingSegment.Offset;
        _model.Select(anchor, caret);

        var result = edit();
        Status = result.Message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (result.Applied)
            Render();
    }

    private void Render()
    {
        _chips.Clear();
        var text = new System.Text.StringBuilder();
        foreach (var token in _model.Tokens)
        {
            switch (token)
            {
                case TextToken t:
                    text.Append(t.Text);
                    break;
                case CodeToken c:
                    text.Append((char)(FirstChip + _chips.Count));
                    _chips.Add(c.Code);
                    break;
                case MarkerToken m:
                    text.Append((char)(FirstChip + _chips.Count));
                    _chips.Add(m.Marker);
                    break;
            }
        }

        Editor.Document.Text = text.ToString();
        Editor.TextArea.Caret.Offset = _model.Caret;
        Editor.TextArea.ClearSelection();
        Editor.TextArea.TextView.Redraw();
    }

    internal bool IsProtected(InlineCode code) => _hints.SourceCode(code.Id, code.Kind) is not null && _hints.IsProtected(code.Id);

    /// <summary>Draws each private-use character as a chip control.</summary>
    private sealed class ChipGenerator(ChipEditor owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            for (var offset = startOffset; offset < end; offset++)
            {
                if (owner.ChipAt(document.GetCharAt(offset)) is not null)
                    return offset;
            }

            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var element = owner.ChipAt(CurrentContext.Document.GetCharAt(offset));
            return element is null ? null : new InlineObjectElement(1, CreateChip(element));
        }

        private Border CreateChip(InlineElement element)
        {
            var (label, tip, isProtected) = element switch
            {
                InlineCode { Kind: InlineCodeKind.Opening } c => ($"⟨{Label(c)}", Tip(c), owner.IsProtected(c)),
                InlineCode { Kind: InlineCodeKind.Closing } c => ($"{Label(c)}⟩", Tip(c), owner.IsProtected(c)),
                InlineCode c => (Label(c), Tip(c), owner.IsProtected(c)),
                InlineMarker { Kind: InlineMarkerKind.Opening } m => ("⟦", $"Annotation '{m.Id}' ({m.Type})", false),
                InlineMarker m => ("⟧", $"End of annotation '{m.Id}'", false),
                _ => ("?", null, false),
            };

            var chip = new Border
            {
                Classes = { "chip" },
                Padding = new Thickness(4, 0),
                Margin = new Thickness(1, 0),
                CornerRadius = new CornerRadius(element is InlineCode { Kind: InlineCodeKind.Standalone } ? 8 : 3),
                Background = element is InlineMarker ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0xE3, 0xEC, 0xFA)),
                BorderBrush = isProtected ? Brushes.DarkOrange : new SolidColorBrush(Color.FromRgb(0x5B, 0x7F, 0xC7)),
                BorderThickness = new Thickness(element is InlineMarker ? 0 : 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = isProtected ? $"{label} 🔒" : label,
                    FontSize = 12,
                    Foreground = element is InlineMarker ? Brushes.Gray : Brushes.Black,
                },
            };
            ToolTip.SetTip(chip, tip);
            return chip;
        }

        private static string Label(InlineCode code) => code.Disp ?? code.SubType switch
        {
            "xlf:b" => "b",
            "xlf:i" => "i",
            "xlf:u" => "u",
            "xlf:lb" => "↵",
            _ => code.Id,
        };

        private static string Tip(InlineCode code) => $"Code '{code.Id}'" + (code.Equiv is null ? "" : $", reads as \"{code.Equiv}\"");
    }

    /// <summary>Stops AvaloniaEdit from editing the document directly (paste, drag and drop, …); edits go through the model.</summary>
    private sealed class BlockAllEdits : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => false;

        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment) => [];
    }
}
