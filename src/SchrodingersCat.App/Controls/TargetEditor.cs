using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using SchrodingersCat.App.Editing;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Controls;

/// <summary>
/// Edits one segment's target through a <see cref="TargetEditModel"/>, with codes as chips (step 5 of
/// <c>docs/design/inline-code-editing.md</c>, built on what the AvaloniaEdit spike showed).
/// </summary>
/// <remarks>
/// <para>The document holds one <see cref="ChipCharacter"/> per code or annotation half, which a
/// <see cref="VisualLineElementGenerator"/> draws as a chip. The model counts a chip as one position, so document
/// offsets and model positions are the same. Which offsets are chips is tracked separately, so the same character
/// in the translation's text stays text.</para>
/// <para>AvaloniaEdit never edits the document itself: a read-only section provider blocks its own edits, and
/// typing, Backspace/Delete, Enter, paste, cut and undo are routed to the model, whose result is drawn again. So
/// every rule the model enforces holds in the editor too. Refusals go to <see cref="Status"/>.</para>
/// </remarks>
public sealed class TargetEditor : UserControl
{
    /// <summary>The document character standing for a chip: U+FFFC OBJECT REPLACEMENT CHARACTER.</summary>
    public const char ChipCharacter = '￼';

    public static readonly StyledProperty<TargetEditModel?> ModelProperty =
        AvaloniaProperty.Register<TargetEditor, TargetEditModel?>(nameof(Model));

    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<TargetEditor, string?>(nameof(Status), defaultBindingMode: BindingMode.TwoWay);

    private readonly Dictionary<int, InlineElement> _chips = [];

    static TargetEditor()
    {
        ModelProperty.Changed.AddClassHandler<TargetEditor>((editor, e) =>
            editor.OnModelChanged((TargetEditModel?)e.OldValue, (TargetEditModel?)e.NewValue));
    }

    public TargetEditor()
    {
        Editor = new TextEditor
        {
            ShowLineNumbers = false,
            WordWrap = true,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Editor[!TextEditor.FontFamilyProperty] = this[!FontFamilyProperty];
        Editor[!TextEditor.FontSizeProperty] = this[!FontSizeProperty];
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Document.UndoStack.SizeLimit = 0; // the model owns undo

        var area = Editor.TextArea;
        area.ReadOnlySectionProvider = new BlockAllEdits();
        area.TextView.ElementGenerators.Add(new ChipGenerator(this));
        area.TextEntering += OnTextEntering;
        area.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Content = Editor;
    }

    /// <summary>The AvaloniaEdit control underneath.</summary>
    public TextEditor Editor { get; }

    public TargetEditModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>The model's message for the last edit: why it was refused, or a note. Null after a plain edit.</summary>
    public string? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>The code or annotation half drawn at a document offset, or null if the offset holds text.</summary>
    public InlineElement? ChipAt(int offset) => _chips.GetValueOrDefault(offset);

    private void OnModelChanged(TargetEditModel? oldModel, TargetEditModel? newModel)
    {
        oldModel?.Changed -= OnModelEdited;
        newModel?.Changed += OnModelEdited;
        Render();

        // A newly active segment is ready to type into.
        if (newModel is not null)
            Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
    }

    private void OnModelEdited(object? sender, EventArgs e) => Render();

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrEmpty(e.Text))
            Apply(m => m.InsertText(e.Text));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Func<TargetEditModel, EditResult>? edit = (e.Key, ctrl, shift) switch
        {
            (Key.Back, _, _) => m => m.Delete(DeleteDirection.Backward),
            (Key.Delete, false, true) => Cut,
            (Key.Delete, _, _) => m => m.Delete(DeleteDirection.Forward),
            (Key.Enter, false, _) => m => m.InsertText("\n"),
            (Key.Z, true, false) => m => m.Undo(),
            (Key.Z, true, true) or (Key.Y, true, false) => m => m.Redo(),
            (Key.X, true, false) => Cut,
            _ => null,
        };

        if ((e.Key == Key.V && ctrl && !shift) || (e.Key == Key.Insert && shift && !ctrl))
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }

        if (edit is null)
            return;
        e.Handled = true;
        Apply(edit);
    }

    // Copies the selection with AvaloniaEdit's own copy, then deletes it through the model.
    private EditResult Cut(TargetEditModel model)
    {
        if (!model.HasSelection)
            return EditResult.Unchanged;
        Editor.Copy();
        return model.Delete(DeleteDirection.Backward);
    }

    // Plain text only for now; the clipboard format that keeps chips is step 7. Chip characters copied from an
    // editor are dropped, because without the format they would become plain text that looks like a code.
    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var text = await clipboard.TryGetTextAsync();
        text = text?.Replace(ChipCharacter.ToString(), "", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(text))
            Apply(m => m.InsertText(text));
    }

    // The editor's caret and selection say where an edit happens.
    private void Apply(Func<TargetEditModel, EditResult> edit)
    {
        if (Model is not { } model)
            return;

        var area = Editor.TextArea;
        var caret = area.Caret.Offset;
        var anchor = area.Selection.IsEmpty
            ? caret
            : area.Selection.SurroundingSegment.Offset == caret ? area.Selection.SurroundingSegment.EndOffset : area.Selection.SurroundingSegment.Offset;
        model.Select(anchor, caret);

        Status = edit(model).Message;
    }

    private void Render()
    {
        _chips.Clear();
        var text = new StringBuilder();
        foreach (var token in Model?.Tokens ?? [])
        {
            switch (token)
            {
                case TextToken t:
                    text.Append(t.Text);
                    break;
                case CodeToken c:
                    _chips[text.Length] = c.Code;
                    text.Append(ChipCharacter);
                    break;
                case MarkerToken m:
                    _chips[text.Length] = m.Marker;
                    text.Append(ChipCharacter);
                    break;
            }
        }

        var area = Editor.TextArea;
        Editor.Document.Text = text.ToString();
        if (Model is { } model)
        {
            area.Caret.Offset = model.Caret;
            var anchor = model.Caret == model.SelectionStart ? model.SelectionEnd : model.SelectionStart;
            area.Selection = model.HasSelection ? Selection.Create(area, anchor, model.Caret) : Selection.Create(area, model.Caret, model.Caret);
        }

        area.TextView.Redraw();
    }

    /// <summary>Draws each chip offset as a chip control.</summary>
    private sealed class ChipGenerator(TargetEditor owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            for (var offset = startOffset; offset < end; offset++)
            {
                if (owner._chips.ContainsKey(offset))
                    return offset;
            }

            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (owner.Model is not { } model || owner.ChipAt(offset) is not { } element)
                return null;

            var chip = element switch
            {
                InlineCode code => Chips.Create(ContentRuns.Chip(code, model.Unit, model.Hints)),
                InlineMarker marker => Chips.CreateMarker(marker),
                _ => null,
            };
            return chip is null ? null : new InlineObjectElement(1, chip);
        }
    }

    /// <summary>Stops AvaloniaEdit from editing the document directly (drag and drop, its own commands); edits go through the model.</summary>
    private sealed class BlockAllEdits : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => false;

        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment) => [];
    }
}
