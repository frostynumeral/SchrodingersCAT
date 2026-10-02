using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.Core.Models;

namespace EditorSpike.Tests;

/// <summary>
/// The automated half of the spike (step 3 of docs/design/inline-code-editing.md): AvaloniaEdit renders the chips,
/// treats them as single characters, and routes every edit through TargetEditModel. IME, screen readers and RTL are
/// checked by hand (see the design doc's checklist).
/// </summary>
public class ChipEditorTests
{
    // The sample target: "Nyomd meg az ⟨b OK b⟩ gombot {0}🔒 ⟦fájl⟧ másolásához."
    private const int OpeningChip = 13;
    private const int ClosingChip = 16;
    private const int ProtectedChip = 25;

    [AvaloniaFact]
    public void RendersEachCodeAndAnnotationHalfAsAChip()
    {
        var window = Show();

        var chips = window.ChipEditor.Editor.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")).ToList();
        var labels = chips.Select(c => ((TextBlock)c.Child!).Text).ToList();

        Assert.Equal(["⟨b", "b⟩", "{0} 🔒", "⟦", "⟧"], labels);
        Assert.Equal(window.ChipEditor.Model.Length, window.ChipEditor.Editor.Document.TextLength);
    }

    [AvaloniaFact]
    public void TheDocumentHoldsOnePrivateUseCharacterPerChip()
    {
        var editor = Show().ChipEditor;
        var text = editor.Editor.Document.Text;

        Assert.Equal(5, text.Count(c => editor.ChipAt(c) is not null));
        Assert.IsType<InlineCode>(editor.ChipAt(text[OpeningChip]));
        Assert.Equal("2", ((InlineCode)editor.ChipAt(text[ProtectedChip])!).Id);
    }

    [AvaloniaFact]
    public void ArrowKeysStepOverAChipInOneMove()
    {
        var window = Show();
        var area = window.ChipEditor.Editor.TextArea;
        area.Caret.Offset = OpeningChip;

        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(OpeningChip + 1, area.Caret.Offset); // past the whole chip, not into it

        Press(window, PhysicalKey.ArrowLeft);
        Assert.Equal(OpeningChip, area.Caret.Offset);
    }

    [AvaloniaFact]
    public void ShiftArrowSelectsAWholeChip()
    {
        var window = Show();
        var area = window.ChipEditor.Editor.TextArea;
        area.Caret.Offset = OpeningChip;

        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);

        Assert.Equal((OpeningChip, 1), (area.Selection.SurroundingSegment.Offset, area.Selection.SurroundingSegment.Length));
    }

    [AvaloniaFact]
    public void BackspaceAfterAChip_RemovesTheWholeChipThroughTheModel()
    {
        var window = Show();
        var editor = window.ChipEditor;
        editor.Editor.TextArea.Caret.Offset = ClosingChip + 1;

        Press(window, PhysicalKey.Backspace);

        Assert.Equal(ClosingChip, editor.Editor.TextArea.Caret.Offset);
        var codes = editor.Model.ToContent().OfType<InlineCode>().ToList();
        Assert.DoesNotContain(codes, c => c is { Id: "1", Kind: InlineCodeKind.Closing });
        Assert.Contains(codes, c => c is { Id: "1", Kind: InlineCodeKind.Opening }); // decision 4: the other half stays
        Assert.Equal(editor.Model.Length, editor.Editor.Document.TextLength);
    }

    [AvaloniaFact]
    public void DeletingAProtectedChip_IsRefusedAndExplained()
    {
        var window = Show();
        var editor = window.ChipEditor;
        var before = editor.Editor.Document.Text;
        editor.Editor.TextArea.Caret.Offset = ProtectedChip;

        Press(window, PhysicalKey.Delete);

        Assert.Equal(before, editor.Editor.Document.Text);
        Assert.Equal("Code '2' ({0}) may not be deleted.", window.StatusText.Text);
    }

    [AvaloniaFact]
    public void TypingGoesThroughTheModelIntoTheSegment()
    {
        var unit = SampleUnit.Load();
        var window = Show(unit);
        window.ChipEditor.Editor.TextArea.Caret.Offset = 0;

        window.KeyTextInput("Kérlek, ");
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("Kérlek, Nyomd meg", unit.Segments[0].Target!.ToPlainText());
        Assert.Equal(8, window.ChipEditor.Editor.TextArea.Caret.Offset);
    }

    [AvaloniaFact]
    public void CtrlZ_UndoesThroughTheModel()
    {
        var unit = SampleUnit.Load();
        var window = Show(unit);
        var original = unit.Segments[0].Target;
        window.ChipEditor.Editor.TextArea.Caret.Offset = ClosingChip + 1;
        Press(window, PhysicalKey.Backspace);
        Assert.NotEqual(original, unit.Segments[0].Target);

        Press(window, PhysicalKey.Z, RawInputModifiers.Control);

        Assert.Equal(original, unit.Segments[0].Target);
    }

    [AvaloniaFact]
    public void AvaloniaEditCantChangeTheDocumentBehindTheModelsBack()
    {
        var editor = Show().ChipEditor;
        var before = editor.Editor.Document.Text;

        // Paste and drag-and-drop go through the read-only section provider, which refuses everything.
        Assert.False(editor.Editor.TextArea.ReadOnlySectionProvider.CanInsert(0));
        Assert.Empty(editor.Editor.TextArea.ReadOnlySectionProvider.GetDeletableSegments(new AvaloniaEdit.Document.TextSegment { StartOffset = 0, Length = 5 }));
        Assert.Equal(before, editor.Editor.Document.Text);
    }

    [AvaloniaFact]
    public void TheRightToLeftSample_LoadsWithTheSameChips()
    {
        var window = Show(SampleUnit.Load(rightToLeft: true));
        var editor = window.ChipEditor;

        Assert.Contains("לחץ", editor.Editor.Document.Text, StringComparison.Ordinal);
        Assert.Equal(5, editor.Editor.Document.Text.Count(c => editor.ChipAt(c) is not null));
    }

    private static SpikeWindow Show(TranslationUnit? unit = null)
    {
        var window = new SpikeWindow(unit ?? SampleUnit.Load());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.ChipEditor.Editor.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
}
