using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Editing;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// The editor control on the headless platform: chips, atomic caret moves, and every edit going through the
/// <see cref="TargetEditModel"/> (step 5 of docs/design/inline-code-editing.md).
/// </summary>
public sealed class TargetEditorTests
{
    // The target: "Nyomd ⟨b OK b⟩ {0}🔒 ⟦fájl⟧." Offsets in the document (and the model):
    private const int OpeningChip = 6;
    private const int ClosingChip = 9;
    private const int ProtectedChip = 11;
    private const int OpeningMarker = 13;
    private const int Length = 20;

    [AvaloniaFact]
    public void DrawsEachCodeAndAnnotationHalfAsAChip()
    {
        var (_, editor, _) = Show();

        var chips = editor.Editor.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(["⟨b", "b⟩", "{0} 🔒"], chips.Select(Label));
        Assert.Equal(["chip", "standalone", "protected"], chips[2].Classes);
        Assert.Equal("Code 2\nCan't be deleted.", ToolTip.GetTip(chips[2]));

        var markers = editor.Editor.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("marker")).ToList();
        Assert.Equal(["⟦", "⟧"], markers.Select(Label));
        Assert.Equal("Start of annotation m1 (term)", ToolTip.GetTip(markers[0]));
    }

    [AvaloniaFact]
    public void TheDocumentHoldsOneCharacterPerChip_AtTheModelsPositions()
    {
        var (_, editor, _) = Show();
        var text = editor.Editor.Document.Text;

        Assert.Equal(Length, text.Length);
        Assert.Equal(editor.Model!.Length, text.Length);
        Assert.Equal(5, text.Count(c => c == TargetEditor.ChipCharacter));
        Assert.Equal(new InlineCode("1", InlineCodeKind.Opening) { SubType = "xlf:b" }, editor.ChipAt(OpeningChip));
        Assert.IsType<InlineMarker>(editor.ChipAt(OpeningMarker));
        Assert.Null(editor.ChipAt(0));
    }

    [AvaloniaFact]
    public void TheChipCharacterInTheTranslationsText_StaysText()
    {
        var unit = SampleUnit();
        unit.Segments[0].Target = InlineContent.FromText($"a{TargetEditor.ChipCharacter}b");
        var (_, editor, _) = Show(unit);

        Assert.Null(editor.ChipAt(1));
        Assert.DoesNotContain(editor.Editor.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip"));
    }

    [AvaloniaFact]
    public void ArrowKeysStepOverAChip_AndShiftArrowSelectsAWholeChip()
    {
        var (window, editor, _) = Show();
        var area = editor.Editor.TextArea;
        area.Caret.Offset = OpeningChip;

        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(OpeningChip + 1, area.Caret.Offset);

        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
        Assert.Equal((OpeningChip, 1), (area.Selection.SurroundingSegment.Offset, area.Selection.SurroundingSegment.Length));
    }

    [AvaloniaFact]
    public void TypingBetweenChips_GoesThroughTheModelIntoTheSegment()
    {
        var (window, editor, unit) = Show();
        editor.Editor.TextArea.Caret.Offset = ClosingChip;

        window.KeyTextInput(" gombot");
        Dispatcher.UIThread.RunJobs();

        var target = unit.Segments[0].Target!;
        Assert.Equal("Nyomd OK gombot  fájl.", target.ToPlainText()); // the {0} chip sits between the two spaces
        Assert.Equal(new InlineCode("1", InlineCodeKind.Closing) { SubType = "xlf:b" }, target[3]); // the chip moved along
        Assert.Equal(ClosingChip + 7, editor.Editor.TextArea.Caret.Offset);
        Assert.Equal(editor.Model!.Length, editor.Editor.Document.TextLength);
        Assert.Null(editor.Status);
    }

    [AvaloniaFact]
    public void BackspaceAfterAChip_RemovesOnlyThatHalf()
    {
        var (window, editor, unit) = Show();
        editor.Editor.TextArea.Caret.Offset = ClosingChip + 1;

        Press(window, PhysicalKey.Backspace);

        var codes = unit.Segments[0].Target!.OfType<InlineCode>().ToList();
        Assert.DoesNotContain(codes, c => c is { Id: "1", Kind: InlineCodeKind.Closing });
        Assert.Contains(codes, c => c is { Id: "1", Kind: InlineCodeKind.Opening }); // decision 4: the other half stays
        Assert.Equal(ClosingChip, editor.Editor.TextArea.Caret.Offset);
        Assert.Equal(Length - 1, editor.Editor.Document.TextLength);
    }

    [AvaloniaFact]
    public void DeletingAProtectedChip_IsRefusedAndExplained()
    {
        var (window, editor, unit) = Show();
        var before = unit.Segments[0].Target;
        editor.Editor.TextArea.Caret.Offset = ProtectedChip;

        Press(window, PhysicalKey.Delete);

        Assert.Equal(before, unit.Segments[0].Target);
        Assert.Equal(Length, editor.Editor.Document.TextLength);
        Assert.Equal("Code '2' ({0}) may not be deleted.", editor.Status);

        // The next edit clears the message.
        Press(window, PhysicalKey.Backspace);
        Assert.Null(editor.Status);
    }

    [AvaloniaFact]
    public void DeletingASelection_KeepsTheProtectedChip()
    {
        var (window, editor, unit) = Show();
        editor.Editor.TextArea.Caret.Offset = ClosingChip + 1;
        for (var i = 0; i < 3; i++)
            Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift); // " {0} "

        Press(window, PhysicalKey.Backspace);

        Assert.Contains(unit.Segments[0].Target!, e => e is InlineCode { Id: "2" });
        Assert.Equal(Length - 2, editor.Editor.Document.TextLength);
        Assert.NotNull(editor.Status); // the model notes that it kept a code
    }

    [AvaloniaFact]
    public void CtrlZ_UndoesAndCtrlY_Redoes()
    {
        var (window, editor, unit) = Show();
        var original = unit.Segments[0].Target;
        editor.Editor.TextArea.Caret.Offset = ClosingChip + 1;
        Press(window, PhysicalKey.Backspace);
        var edited = unit.Segments[0].Target;

        Press(window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal(original, unit.Segments[0].Target);
        Assert.Equal(Length, editor.Editor.Document.TextLength);
        Assert.Equal(ClosingChip + 1, editor.Editor.TextArea.Caret.Offset);

        Press(window, PhysicalKey.Y, RawInputModifiers.Control);
        Assert.Equal(edited, unit.Segments[0].Target);
    }

    [AvaloniaFact]
    public void Enter_InsertsALineBreak()
    {
        var (window, editor, unit) = Show();
        editor.Editor.TextArea.Caret.Offset = 5;

        Press(window, PhysicalKey.Enter);

        Assert.StartsWith("Nyomd\n ", unit.Segments[0].Target!.ToPlainText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Paste_InsertsPlainTextWithoutChipCharacters()
    {
        var (window, editor, unit) = Show();
        await window.Clipboard!.SetTextAsync($"meg{TargetEditor.ChipCharacter}!");
        editor.Editor.TextArea.Caret.Offset = 5;

        Press(window, PhysicalKey.V, RawInputModifiers.Control);
        await WaitFor(() => unit.Segments[0].Target!.ToPlainText().StartsWith("Nyomdmeg!", StringComparison.Ordinal));

        Assert.Equal(Length + 4, editor.Editor.Document.TextLength);
    }

    [AvaloniaFact]
    public async Task Cut_CopiesAndDeletesThroughTheModel()
    {
        var (window, editor, unit) = Show();
        editor.Editor.TextArea.Caret.Offset = 0;
        for (var i = 0; i < 5; i++)
            Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);

        Press(window, PhysicalKey.X, RawInputModifiers.Control);

        Assert.Equal("Nyomd", await window.Clipboard!.TryGetTextAsync());
        Assert.StartsWith(" OK", unit.Segments[0].Target!.ToPlainText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CopyAndPaste_KeepsTheChips_AsACopy()
    {
        var (window, editor, unit) = Show();
        SelectTheBoldPair(window, editor);

        Press(window, PhysicalKey.C, RawInputModifiers.Control);
        Assert.Equal("⟨1⟩OK⟨/1⟩", await window.Clipboard!.TryGetTextAsync()); // other apps get placeholders
        editor.Editor.TextArea.Caret.Offset = Length;
        Press(window, PhysicalKey.V, RawInputModifiers.Control);
        await WaitFor(() => editor.Model!.Length == Length + 4);

        // Code 1 is already in the target, so the paste is a copy with a new id (design section 2).
        var codes = unit.Segments[0].Target!.OfType<InlineCode>().ToList();
        Assert.Equal([("3", InlineCodeKind.Opening), ("3", InlineCodeKind.Closing)], codes.Skip(3).Select(c => (c.Id, c.Kind)));
        Assert.Equal(7, editor.Editor.Document.Text.Count(c => c == TargetEditor.ChipCharacter));
    }

    [AvaloniaFact]
    public async Task CutAndPaste_MovesTheCode()
    {
        var (window, editor, unit) = Show();
        SelectTheBoldPair(window, editor);

        Press(window, PhysicalKey.X, RawInputModifiers.Control);
        editor.Editor.TextArea.Caret.Offset = editor.Model!.Length - 1;
        Press(window, PhysicalKey.V, RawInputModifiers.Control);
        await WaitFor(() => editor.Model!.Length == Length);

        var target = unit.Segments[0].Target!;
        Assert.Equal("Nyomd  ⟨2/⟩ fájl⟨1⟩OK⟨/1⟩.", InlineFormatterText(target)); // the same code, moved
    }

    [AvaloniaFact]
    public void F9_InsertsTheNextMissingCode()
    {
        var unit = SampleUnit();
        unit.Segments[0].Target = InlineContent.FromText("Nyomd ");
        var (window, editor, _) = Show(unit);

        Press(window, PhysicalKey.F9);
        window.KeyTextInput("OK");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Nyomd ⟨1⟩OK⟨/1⟩", InlineFormatterText(unit.Segments[0].Target!));
        Assert.Null(editor.Status);

        Press(window, PhysicalKey.F9);
        Press(window, PhysicalKey.F9);
        Assert.Equal("All codes in the source are already in the target.", editor.Status);
    }

    [AvaloniaFact]
    public void CtrlShiftF9_PicksACode_WithTypeAhead()
    {
        var unit = SampleUnit();
        unit.Segments[0].Target = InlineContent.FromText("Nyomd ");
        var (window, editor, _) = Show(unit);

        Press(window, PhysicalKey.F9, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(editor.CodePicker.IsOpen);
        var list = (ListBox)editor.CodePicker.Child!.GetVisualDescendants().Single(v => v is ListBox);
        Assert.Equal(["⟨b…b⟩", "{0}"], list.Items.Cast<CodeChoice>().Select(c => c.Text));
        Assert.Equal("1", ((CodeChoice)list.SelectedItem!).CodeId); // the first missing code

        window.KeyTextInput("{0");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["{0}"], list.Items.Cast<CodeChoice>().Select(c => c.Text));

        Press(window, PhysicalKey.Enter);
        Assert.False(editor.CodePicker.IsOpen);
        Assert.Equal("Nyomd ⟨2/⟩", InlineFormatterText(unit.Segments[0].Target!));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void TheCodePicker_ClosesOnEscape_WithoutInserting()
    {
        var (window, editor, unit) = Show();
        var before = unit.Segments[0].Target;

        Press(window, PhysicalKey.F9, RawInputModifiers.Control | RawInputModifiers.Shift);
        Press(window, PhysicalKey.Escape);

        Assert.False(editor.CodePicker.IsOpen);
        Assert.Equal(before, unit.Segments[0].Target);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void AvaloniaEditCantChangeTheDocumentBehindTheModelsBack()
    {
        var (_, editor, _) = Show();
        var provider = editor.Editor.TextArea.ReadOnlySectionProvider;

        // Drag and drop and AvaloniaEdit's own commands go through the read-only section provider.
        Assert.False(provider.CanInsert(0));
        Assert.Empty(provider.GetDeletableSegments(new AvaloniaEdit.Document.TextSegment { StartOffset = 0, Length = 5 }));
    }

    [AvaloniaFact]
    public void SwitchingModels_ShowsTheOtherTarget()
    {
        var (_, editor, _) = Show();
        var other = SampleUnit();
        other.Segments[0].Target = InlineContent.FromText("Más");

        editor.Model = new TargetEditModel(other, other.Segments[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Más", editor.Editor.Document.Text);

        editor.Model = null;
        Assert.Equal("", editor.Editor.Document.Text);
    }

    private static TranslationUnit SampleUnit()
    {
        var bold = new InlineCode("1", InlineCodeKind.Opening) { SubType = "xlf:b" };
        var boldEnd = new InlineCode("1", InlineCodeKind.Closing) { SubType = "xlf:b" };
        var placeholder = new InlineCode("2", InlineCodeKind.Standalone) { Disp = "{0}", CanDelete = false };
        var term = new InlineMarker("m1", InlineMarkerKind.Opening) { Type = "term" };
        var termEnd = new InlineMarker("m1", InlineMarkerKind.Closing);

        var unit = new TranslationUnit { Id = "u1" };
        unit.Segments.Add(new Segment
        {
            Source = new InlineContent([new InlineText("Press "), bold, new InlineText("OK"), boldEnd, new InlineText(" "), placeholder, new InlineText(" "), term, new InlineText("file"), termEnd, new InlineText(".")]),
            Target = new InlineContent([new InlineText("Nyomd "), bold, new InlineText("OK"), boldEnd, new InlineText(" "), placeholder, new InlineText(" "), term, new InlineText("fájl"), termEnd, new InlineText(".")]),
        });
        return unit;
    }

    private static (Window Window, TargetEditor Editor, TranslationUnit Unit) Show(TranslationUnit? unit = null)
    {
        unit ??= SampleUnit();
        var editor = new TargetEditor { Model = new TargetEditModel(unit, unit.Segments[0]) };
        var window = new Window { Content = editor, Width = 600, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);
        return (window, editor, unit);
    }

    // ⟨b OK b⟩, with the caret at its end, as a user would select it.
    private static void SelectTheBoldPair(Window window, TargetEditor editor)
    {
        editor.Editor.TextArea.Caret.Offset = OpeningChip;
        for (var i = 0; i < 4; i++)
            Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
    }

    private static string InlineFormatterText(InlineContent content) => InlineFormatter.Format(content);

    private static string Label(Border chip) =>
        string.Join(" ", chip.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The editor did not reach the expected state.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
