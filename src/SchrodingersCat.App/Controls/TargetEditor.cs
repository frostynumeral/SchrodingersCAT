using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using SchrodingersCat.App.Editing;
using SchrodingersCat.App.Services;
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
/// typing, Backspace/Delete, Enter, copy, cut, paste, undo and the code shortcuts (F9, Ctrl+Shift+F9) are routed to
/// the model, whose result is drawn again. So every rule the model enforces holds in the editor too. Refusals go to
/// <see cref="Status"/>.</para>
/// <para>Two pickers open under the editor: the code picker (Ctrl+Shift+F9) and the term picker (Ctrl+T, opened by the
/// window with the segment's terms, see <see cref="OpenTermPicker"/>).</para>
/// </remarks>
public sealed class TargetEditor : UserControl
{
    /// <summary>The document character standing for a chip: U+FFFC OBJECT REPLACEMENT CHARACTER.</summary>
    public const char ChipCharacter = '￼';

    public static readonly StyledProperty<TargetEditModel?> ModelProperty =
        AvaloniaProperty.Register<TargetEditor, TargetEditModel?>(nameof(Model));

    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<TargetEditor, string?>(nameof(Status), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlySet<string>?> ProblemCodesProperty =
        AvaloniaProperty.Register<TargetEditor, IReadOnlySet<string>?>(nameof(ProblemCodes));

    private readonly Dictionary<int, InlineElement> _chips = [];
    private readonly Picker<CodeChoice> _codePicker;
    private readonly Picker<TermChoice> _termPicker;
    private bool _rendering;

    static TargetEditor()
    {
        ModelProperty.Changed.AddClassHandler<TargetEditor>((editor, e) =>
            editor.OnModelChanged((TargetEditModel?)e.OldValue, (TargetEditModel?)e.NewValue));
        ProblemCodesProperty.Changed.AddClassHandler<TargetEditor>((editor, _) => editor.Editor.TextArea.TextView.Redraw());
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
        area.Caret.PositionChanged += (_, _) => SyncSelection();
        area.SelectionChanged += (_, _) => SyncSelection();
        area.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // The code picker (Ctrl+Shift+F9): type-ahead over the unit's source codes, the first missing one preselected
        // (usually the one wanted next).
        _codePicker = new Picker<CodeChoice>(
            this, "CodeFilter", "CodeList", "Type to find a code", CodeChoiceView, (c, filter) => c.Matches(filter),
            shown => shown.FirstOrDefault(c => c.IsMissing) ?? (shown.Count > 0 ? shown[0] : null),
            choice => Apply(m => m.InsertCode(choice.CodeId)));

        // The term picker (Ctrl+T, section 6 of docs/design/termbase.md): type-ahead over the segment's term
        // translations, inserted as text.
        _termPicker = new Picker<TermChoice>(
            this, "TermFilter", "TermList", "Type to find a term", TermChoiceView, (c, filter) => c.Matches(filter),
            shown => shown.Count > 0 ? shown[0] : null,
            choice =>
            {
                if (Model is not { } model)
                    return;
                SyncSelection();
                Status = TermInsertion.Apply(model, choice.Text);
            });

        Content = new Panel { Children = { Editor, _codePicker.Popup, _termPicker.Popup } };
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

    /// <summary>The ids of the codes whose chips get the problem outline (from the code check).</summary>
    public IReadOnlySet<string>? ProblemCodes
    {
        get => GetValue(ProblemCodesProperty);
        set => SetValue(ProblemCodesProperty, value);
    }

    /// <summary>The code picker (Ctrl+Shift+F9).</summary>
    public Popup CodePicker => _codePicker.Popup;

    /// <summary>The term picker (Ctrl+T).</summary>
    public Popup TermPicker => _termPicker.Popup;

    /// <summary>A picker is open and has the keyboard, so the window's shortcuts leave it alone.</summary>
    public bool IsPickerOpen => CodePicker.IsOpen || TermPicker.IsOpen;

    /// <summary>
    /// Opens the term picker with these choices (the window asks for them on Ctrl+T); the chosen translation goes in
    /// at the caret as text, replacing the selection.
    /// </summary>
    public void OpenTermPicker(IReadOnlyList<TermChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (Model is null || choices.Count == 0)
            return;

        SyncSelection();
        _termPicker.Open(choices);
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

    /// <summary>Gives the keyboard to the editor (e.g. Escape in the concordance's search box).</summary>
    public void FocusEditor() => Editor.TextArea.Focus();

    // An edit can come from outside the editor (a click on a source chip); typing should carry on here.
    private void OnModelEdited(object? sender, EventArgs e)
    {
        Render();
        if (!Editor.TextArea.IsKeyboardFocusWithin)
            Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
    }

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
            (Key.F9, false, false) => m => m.InsertNextMissingCode(),
            _ => null,
        };

        if ((e.Key == Key.V && ctrl && !shift) || (e.Key == Key.Insert && shift && !ctrl))
        {
            e.Handled = true;
            PasteAsync().Forget();
            return;
        }

        if (e.Key == Key.C && ctrl && !shift)
        {
            e.Handled = true;
            Copy();
            return;
        }

        if (e.Key == Key.F9 && ctrl && shift)
        {
            e.Handled = true;
            OpenCodePicker();
            return;
        }

        if (edit is null)
            return;
        e.Handled = true;
        Apply(edit);
    }

    // The selection with its codes (see SegmentClipboard), then deleted through the model, which keeps protected codes.
    private EditResult Cut(TargetEditModel model)
    {
        if (!model.HasSelection)
            return EditResult.Unchanged;
        Copy();
        return model.Delete(DeleteDirection.Backward);
    }

    private void Copy()
    {
        SyncSelection();
        if (Model is { HasSelection: true } model && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            SegmentClipboard.SetAsync(clipboard, model.SelectedContent(), model.Unit).Forget();
    }

    // Codes come along when they were copied in this app from the same unit; the model applies the rules.
    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await SegmentClipboard.GetAsync(clipboard) is not { } clip)
            return;
        Apply(m => m.Paste(clip.Content, clip.Unit));
    }

    private void OpenCodePicker()
    {
        if (Model is not { } model)
            return;

        var choices = CodeChoice.For(model);
        if (choices.Count == 0)
        {
            Status = "There are no codes in the source.";
            return;
        }

        SyncSelection();
        _codePicker.Open(choices);
    }

    // A row of the code picker; it shows whichever choice the list gives it as its data context.
    private static StackPanel CodeChoiceView() => ChoiceView<CodeChoice>(c => (c.Text, c.IsMissing ? "missing" : "in the target", c.Description));

    // A row of the term picker: the translation, the source term it translates, and the entry in the tooltip.
    private static StackPanel TermChoiceView() => ChoiceView<TermChoice>(c => (c.Text, c.Source, c.Description));

    private static StackPanel ChoiceView<T>(Func<T, (string Label, string Detail, string ToolTip)> show)
    {
        var label = new TextBlock { Classes = { "label" }, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        var detail = new TextBlock { Opacity = 0.6 };
        var view = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { label, detail } };
        view.DataContextChanged += (_, _) =>
        {
            if (view.DataContext is not T choice)
                return;
            var (text, more, tip) = show(choice);
            label.Text = text;
            detail.Text = more;
            ToolTip.SetTip(view, tip);
        };
        return view;
    }

    private void Apply(Func<TargetEditModel, EditResult> edit)
    {
        if (Model is not { } model)
            return;

        SyncSelection();
        Status = edit(model).Message;
    }

    // The editor's caret and selection say where an edit happens, including edits from outside (a source chip click),
    // so the model follows every move. While rendering, the editor follows the model instead.
    private void SyncSelection()
    {
        if (_rendering || Model is not { } model)
            return;

        var area = Editor.TextArea;
        var caret = area.Caret.Offset;
        var anchor = area.Selection.IsEmpty
            ? caret
            : area.Selection.SurroundingSegment.Offset == caret ? area.Selection.SurroundingSegment.EndOffset : area.Selection.SurroundingSegment.Offset;
        model.Select(anchor, caret);
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
        _rendering = true;
        try
        {
            Editor.Document.Text = text.ToString();
            if (Model is { } model)
            {
                area.Caret.Offset = model.Caret;
                var anchor = model.Caret == model.SelectionStart ? model.SelectionEnd : model.SelectionStart;
                area.Selection = model.HasSelection ? Selection.Create(area, anchor, model.Caret) : Selection.Create(area, model.Caret, model.Caret);
            }
        }
        finally
        {
            _rendering = false;
        }

        area.TextView.Redraw();
    }

    /// <summary>
    /// A picker under the editor: a filter box with type-ahead over the choices, Up/Down, Enter or a tap to choose,
    /// Escape to close. Closing gives the keyboard back to the editor.
    /// </summary>
    private sealed class Picker<T>
        where T : class
    {
        private readonly TextBox _filter;
        private readonly ListBox _list;
        private readonly Func<T, string, bool> _matches;
        private readonly Func<IReadOnlyList<T>, T?> _preselect;
        private readonly Action<T> _choose;
        private IReadOnlyList<T> _choices = [];

        public Picker(
            TargetEditor owner, string filterName, string listName, string placeholder, Func<Control> view,
            Func<T, string, bool> matches, Func<IReadOnlyList<T>, T?> preselect, Action<T> choose)
        {
            _matches = matches;
            _preselect = preselect;
            _choose = choose;
            _filter = new TextBox { Name = filterName, PlaceholderText = placeholder };
            _filter.TextChanged += (_, _) => Filter();
            _list = new ListBox { Name = listName, MaxHeight = 240, ItemTemplate = new FuncDataTemplate<T>((_, _) => view()) };
            _list.Tapped += (_, _) => Choose();
            var content = new StackPanel { Spacing = 4, Width = 280, Children = { _filter, _list } };
            content.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            Popup = new Popup
            {
                PlacementTarget = owner,
                Placement = PlacementMode.BottomEdgeAlignedLeft,
                IsLightDismissEnabled = true,
                Child = new Border { Classes = { "codePicker" }, Padding = new Thickness(6), Child = content },
            };
            Popup.Closed += (_, _) => owner.Editor.TextArea.Focus();
        }

        public Popup Popup { get; }

        public void Open(IReadOnlyList<T> choices)
        {
            _choices = choices;
            _filter.Text = "";
            Filter();
            Popup.IsOpen = true;
            _filter.Focus();
        }

        private void Filter()
        {
            var shown = _choices.Where(c => _matches(c, _filter.Text ?? "")).ToList();
            _list.ItemsSource = shown;
            _list.SelectedItem = _preselect(shown);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            var count = _list.ItemCount;
            switch (e.Key)
            {
                case Key.Down when count > 0:
                    _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, count - 1);
                    break;
                case Key.Up when count > 0:
                    _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                    break;
                case Key.Enter:
                    Choose();
                    break;
                case Key.Escape:
                    Popup.IsOpen = false;
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        private void Choose()
        {
            if (_list.SelectedItem is not T choice)
                return;

            Popup.IsOpen = false;
            _choose(choice);
        }
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
                InlineCode code => Chips.Create(ContentRuns.Chip(code, model.Unit, model.Hints) with
                {
                    HasProblem = owner.ProblemCodes?.Contains(code.Id) == true,
                }),
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
