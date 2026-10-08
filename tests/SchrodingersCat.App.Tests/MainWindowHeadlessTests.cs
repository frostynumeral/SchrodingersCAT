using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>Drives the real window on Avalonia's headless platform: bindings, templates, key bindings and all.</summary>
public sealed class MainWindowHeadlessTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [AvaloniaFact]
    public async Task OpenEditSave_ThroughTheWindow()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);

        window.FindControl<Button>("OpenButton")!.Command!.Execute(null);
        await WaitFor(() => vm.Segments.Count == 3);

        // Nothing is selected yet: every target is shown read-only.
        Assert.Empty(VisibleEditors(window));
        Assert.Equal(["", "Viszlát.", ""], TargetViews(window).Select(PlainText));

        // Click the first target like a user would: the row becomes active and its editor takes the keyboard.
        Click(window, TargetFrames(window)[0]);
        Assert.Same(vm.Segments[0], vm.SelectedSegment);
        var editor = Assert.Single(VisibleEditors(window));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        window.KeyTextInput("Helló világ.");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Helló világ.", editor.Editor.Document.Text);
        Assert.True(vm.IsDirty);
        Assert.Equal("Translated", vm.Segments[0].State);
        Assert.Equal("sample.xlf • — SchrödingersCAT", window.Title);

        // Ctrl+S reaches the window from inside the editor.
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await WaitFor(() => !vm.IsDirty);

        var saved = await new Xliff20Serializer().ReadFileAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("Helló világ.", saved.Units.First().Segments[0].Target!.ToPlainText());
        Assert.False(window.FindControl<Border>("ErrorBanner")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task SelectingAnotherSegment_MovesTheEditorAndKeepsTheEdit()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        Click(window, TargetFrames(window)[0]);
        window.KeyTextInput("Szia");
        Dispatcher.UIThread.RunJobs();

        Click(window, TargetViews(window)[1], onText: true); // on the text itself, not the frame around it
        var editor = Assert.Single(VisibleEditors(window));
        Assert.Same(vm.Segments[1].EditModel, editor.Model);
        Assert.Equal("Szia", PlainText(TargetViews(window)[0]));

        // The caret starts at the end, so typing adds to the target.
        window.KeyTextInput(" Szia.");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Viszlát. Szia.", vm.Segments[1].TargetText);

        // Each segment keeps its own undo history.
        Click(window, TargetFrames(window)[0]);
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.Segments[0].TargetText);
        Assert.Equal("Viszlát. Szia.", vm.Segments[1].TargetText);
    }

    [AvaloniaFact]
    public async Task TheStatusLine_ShowsTheActiveSegmentsEditMessage()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        var status = window.FindControl<TextBlock>("StatusText")!;
        Assert.False(status.IsVisible);

        Click(window, TargetFrames(window)[0]);
        vm.SelectedSegment!.EditStatus = "Code 1 may not be deleted.";
        Dispatcher.UIThread.RunJobs();
        Assert.True(status.IsVisible);
        Assert.Equal("Code 1 may not be deleted.", status.Text);

        Click(window, TargetFrames(window)[1]);
        Assert.False(status.IsVisible);
    }

    [AvaloniaFact]
    public async Task OpenFailure_IsShownInTheWindow()
    {
        var (window, vm) = Show(_files.Write("broken.xlf", "<xliff"));

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => vm.ErrorMessage is not null);

        Assert.True(window.FindControl<Border>("ErrorBanner")!.IsVisible);
        Assert.StartsWith("Could not open broken.xlf:", window.FindControl<SelectableTextBlock>("ErrorText")!.Text);
        Assert.Empty(TargetFrames(window));
    }

    [AvaloniaTheory]
    [InlineData(UnsavedChangesChoice.Cancel, false, null)]
    [InlineData(UnsavedChangesChoice.DontSave, true, null)]
    [InlineData(UnsavedChangesChoice.Save, true, "Helló világ.")]
    public async Task ClosingWithUnsavedChanges_AsksFirst(UnsavedChangesChoice choice, bool closes, string? savedTarget)
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var dialogs = new FakeDialogs(path) { Choice = choice };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        vm.Segments[0].TargetText = "Helló világ.";
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        await WaitFor(() => dialogs.Asked.Count > 0);
        await WaitFor(() => closed == closes);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.Equal(closes, closed);
        var saved = await new Xliff20Serializer().ReadFileAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(savedTarget, saved.Units.First().Segments[0].Target?.ToPlainText());
    }

    [AvaloniaFact]
    public async Task ClosingWithoutChanges_DoesNotAsk()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var dialogs = new FakeDialogs(path);
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        await WaitFor(() => closed);

        Assert.Empty(dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task CtrlO_WithUnsavedChanges_Cancel_ShowsNoPicker()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var dialogs = new FakeDialogs(path) { Choice = UnsavedChangesChoice.Cancel };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        vm.Segments[0].TargetText = "Helló világ.";

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => dialogs.Asked.Count > 0);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task CtrlShiftS_SavesAs()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var copy = _files.PathOf("copy.xlf");
        var dialogs = new FakeDialogs(path) { SavePath = copy };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitFor(() => vm.FilePath == copy);

        Assert.True(File.Exists(copy));
        Assert.Equal("copy.xlf — SchrödingersCAT", window.Title);
    }

    // The real prompt window, through the real dialog service.
    [AvaloniaTheory]
    [InlineData("SaveButton", UnsavedChangesChoice.Save)]
    [InlineData("DontSaveButton", UnsavedChangesChoice.DontSave)]
    [InlineData("CancelButton", UnsavedChangesChoice.Cancel)]
    public async Task UnsavedChangesDialog_ReturnsTheClickedChoice(string button, UnsavedChangesChoice expected)
    {
        var (owner, prompt, dialog) = ShowPrompt();

        dialog.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(expected, await prompt);
        Assert.Contains("sample.xlf", dialog.FindControl<TextBlock>("Message")!.Text);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task UnsavedChangesDialog_ClosedWithoutAnswer_MeansCancel()
    {
        var (owner, prompt, dialog) = ShowPrompt();

        dialog.Close(); // title bar close button
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(UnsavedChangesChoice.Cancel, await prompt);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task UnsavedChangesDialog_Escape_MeansCancel()
    {
        var (owner, prompt, dialog) = ShowPrompt();

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitFor(() => prompt.IsCompleted);

        Assert.Equal(UnsavedChangesChoice.Cancel, await prompt);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task InlineCodes_AreShownAsChips()
    {
        var path = _files.Write("tagged.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1">
                  <originalData>
                    <data id="d1">&lt;b class="warn"&gt;</data>
                    <data id="d2">&lt;/b&gt;</data>
                  </originalData>
                  <segment>
                    <source>Hello <ph id="1" disp="{0}" equiv="Ann" canDelete="no"/>, press <pc id="2" subType="xlf:b" type="fmt" dataRefStart="d1" dataRefEnd="d2">OK</pc>.</source>
                    <target>Szia <ph id="1" disp="{0}" equiv="Ann" canDelete="no"/>, nyomd meg: <pc id="2" subType="xlf:b" type="fmt" dataRefStart="d1" dataRefEnd="d2">OK</pc>.</target>
                  </segment>
                </unit>
              </file>
            </xliff>
            """);
        var (window, vm) = Show(path);

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => vm.Segments.Count == 1);

        var source = ContentBlock(window, "source");
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], Chips(source).Select(Label));
        Assert.Equal("Hello {0}, press ⟨bOKb⟩.", string.Concat(source.Inlines!.Select(InlineText)));

        var (placeholder, opening, closing) = (Chips(source)[0], Chips(source)[1], Chips(source)[2]);
        Assert.Equal(["chip", "standalone", "protected"], placeholder.Classes);
        Assert.Equal(["chip", "opening"], opening.Classes);
        Assert.Equal(["chip", "closing"], closing.Classes);
        Assert.Equal("Code 1\nReads as: Ann\nCan't be deleted.", ToolTip.GetTip(placeholder));
        Assert.Equal("Start of code 2\n<b class=\"warn\">", ToolTip.GetTip(opening));
        Assert.Equal("End of code 2\n</b>", ToolTip.GetTip(closing));

        // Not selected, the target shows the same chips, read-only; selected, the editor shows them.
        var target = ContentBlock(window, "target");
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], Chips(target).Select(Label));
        Assert.Equal("Szia {0}, nyomd meg: ⟨bOKb⟩.", string.Concat(target.Inlines!.Select(InlineText)));

        Click(window, TargetFrames(window)[0]);
        Assert.Same(vm.Segments[0], vm.SelectedSegment);
        var editor = Assert.Single(VisibleEditors(window));
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], EditorChips(editor).Select(Label));
    }

    [AvaloniaFact]
    public async Task OpenEditTaggedSegmentSave_KeepsTheCodes()
    {
        var path = _files.Write("tagged.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1">
                  <segment><source>Hello <ph id="1" disp="{0}" canDelete="no"/>, press <pc id="2" subType="xlf:b" type="fmt">OK</pc>.</source></segment>
                </unit>
              </file>
            </xliff>
            """);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        Assert.False(IssueBadge(window).IsVisible); // not translated yet

        Click(window, TargetFrames(window)[0]);
        var editor = Assert.Single(VisibleEditors(window));
        window.KeyTextInput("Szia ");
        Dispatcher.UIThread.RunJobs();

        // Both codes are missing: the protected one is an error, and the source chips are outlined.
        var badge = IssueBadge(window);
        Assert.True(badge.IsVisible);
        Assert.Contains("error", badge.Classes);
        Assert.Equal("2", Label(badge));
        Assert.All(Chips(ContentBlock(window, "source")), c => Assert.Contains("problem", c.Classes));

        // Clicking a source chip inserts it at the caret, and the editor keeps the keyboard.
        Click(window, Chips(ContentBlock(window, "source"))[0]);
        window.KeyTextInput(", nyomd meg: ");
        Click(window, Chips(ContentBlock(window, "source"))[1]); // a pair: the caret goes between its halves
        window.KeyTextInput("OK");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], EditorChips(editor).Select(Label));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);
        Assert.False(IssueBadge(window).IsVisible);
        Assert.All(Chips(ContentBlock(window, "source")), c => Assert.DoesNotContain("problem", c.Classes));

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await WaitFor(() => !vm.IsDirty);

        var saved = await new Xliff20Serializer().ReadFileAsync(path, TestContext.Current.CancellationToken);
        var segment = saved.Units.Single().Segments.Single();
        Assert.Equal(new InlineContent(
        [
            new InlineText("Szia "),
            new InlineCode("1", InlineCodeKind.Standalone) { Disp = "{0}", CanDelete = false },
            new InlineText(", nyomd meg: "),
            new InlineCode("2", InlineCodeKind.Opening) { SubType = "xlf:b", Type = "fmt", CanOverlap = false },
            new InlineText("OK"),
            new InlineCode("2", InlineCodeKind.Closing) { SubType = "xlf:b", Type = "fmt", CanOverlap = false },
        ]), segment.Target);
        Assert.Equal(SegmentState.Translated, segment.State);
    }

    // Regression: the active row's read-only target is hidden but still got its chips rebuilt after each edit, and
    // Avalonia's next render of the hidden block threw "Visual was invalidated during the render pass".
    [AvaloniaFact]
    public async Task EditingATaggedSegment_RendersWithTheReadOnlyTargetHidden()
    {
        var path = _files.Write("tagged.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        var tagged = vm.Segments[2];
        vm.SelectedSegment = tagged;
        Dispatcher.UIThread.RunJobs();

        tagged.InsertCodeCommand.Execute(tagged.SourceRuns.OfType<ChipRun>().First());
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        Click(window, TargetFrames(window)[0]);
        Assert.Equal(["⟨1", "1⟩"], Chips(TargetViews(window)[2]).Select(Label)); // shown once visible again
    }

    [AvaloniaFact]
    public async Task AProblemCode_IsOutlinedInTheEditor()
    {
        var path = _files.Write("unknown.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1">
                  <segment><source>A<ph id="1"/></source><target>A<ph id="1"/><ph id="9"/></target></segment>
                </unit>
              </file>
            </xliff>
            """);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);

        Assert.Equal([[], ["problem"]], Chips(ContentBlock(window, "target")).Select(ProblemClass));

        Click(window, TargetFrames(window)[0]);
        Assert.Equal([[], ["problem"]], EditorChips(Assert.Single(VisibleEditors(window))).Select(ProblemClass));
    }

    private static Border IssueBadge(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("issueBadge"));
    }

    // ---------------------------------------------------------------- keyboard workflow (step 7)

    [AvaloniaFact]
    public async Task CtrlDownAndCtrlUp_MoveBetweenSegments_AndTheEditorFollows()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);

        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Control); // nothing selected: the first segment
        Assert.Same(vm.Segments[0], vm.SelectedSegment);

        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Control);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        var editor = Assert.Single(VisibleEditors(window));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        Press(window, PhysicalKey.ArrowUp, RawInputModifiers.Control); // from inside the editor
        Assert.Same(vm.Segments[0], vm.SelectedSegment);
        Press(window, PhysicalKey.ArrowUp, RawInputModifiers.Control); // at the top: stays
        Assert.Same(vm.Segments[0], vm.SelectedSegment);
    }

    [AvaloniaFact]
    public async Task CtrlEnter_ConfirmsTheSegmentAndMovesOn()
    {
        var path = _files.Write("pretranslated.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>Hello.</source><target>Szia.</target></segment></unit>
                <unit id="u2"><segment><source>Bye.</source></segment></unit>
              </file>
            </xliff>
            """);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        Click(window, TargetFrames(window)[0]);

        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);

        Assert.Equal("Translated", vm.Segments[0].State);
        Assert.True(vm.IsDirty);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);

        // Nothing to confirm yet: it stays, and says why.
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        Assert.Equal("Initial", vm.Segments[1].State);
        Assert.Equal("Translate the segment before confirming it.", window.FindControl<TextBlock>("StatusText")!.Text);
    }

    [AvaloniaFact]
    public async Task CtrlShiftDown_GoesToTheNextSegmentWithCodeIssues()
    {
        var path = _files.Write("issues.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>A<ph id="1"/></source><target>A<ph id="1"/></target></segment></unit>
                <unit id="u2"><segment><source>B<ph id="1"/><ph id="2"/></source><target>B<ph id="2"/><ph id="1"/></target></segment></unit>
                <unit id="u3"><segment><source>C<ph id="1"/></source><target>C</target></segment></unit>
              </file>
            </xliff>
            """);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        Click(window, TargetFrames(window)[0]);

        // u2 only has codes reordered (info), so it's skipped; u3 is missing a code (a warning).
        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Same(vm.Segments[2], vm.SelectedSegment);

        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Same(vm.Segments[2], vm.SelectedSegment);
        Assert.Equal("No segments with code errors or warnings below this one.", vm.Segments[2].EditStatus);
    }

    [AvaloniaFact]
    public async Task CtrlInsert_CopiesTheSource_AskingFirstIfThereIsATranslation()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var dialogs = new FakeDialogs(path);
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        Click(window, TargetFrames(window)[2]);
        var tagged = vm.Segments[2];

        // No translation yet: copied without asking.
        Press(window, PhysicalKey.Insert, RawInputModifiers.Control);
        Assert.Equal("Press ⟨1⟩OK⟨/1⟩.", tagged.TargetText);
        Assert.Empty(dialogs.Asked);

        window.KeyTextInput("!");
        Dispatcher.UIThread.RunJobs();
        Press(window, PhysicalKey.Insert, RawInputModifiers.Control); // the user cancels
        await WaitFor(() => dialogs.Asked.Count == 1);
        Assert.Equal(["confirm: Copy source"], dialogs.Asked);
        Assert.Equal("Press ⟨1⟩OK⟨/1⟩.!", tagged.TargetText);

        dialogs.Confirm = true;
        Press(window, PhysicalKey.Insert, RawInputModifiers.Control);
        await WaitFor(() => tagged.TargetText == "Press ⟨1⟩OK⟨/1⟩.");
    }

    [AvaloniaFact]
    public async Task CopyingFromTheSourceColumn_KeepsTheCodes()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        Click(window, TargetFrames(window)[2]);

        var source = window.GetVisualDescendants().OfType<InlineContentBlock>().Where(b => b.Classes.Contains("source")).ElementAt(2);
        source.Focus();
        source.SelectAll();
        Press(window, PhysicalKey.C, RawInputModifiers.Control);
        Assert.Equal("Press ⟨1⟩OK⟨/1⟩.", await window.Clipboard!.TryGetTextAsync());

        Click(window, Assert.Single(VisibleEditors(window)), onText: true);
        Press(window, PhysicalKey.V, RawInputModifiers.Control);
        await WaitFor(() => vm.Segments[2].TargetText == "Press ⟨1⟩OK⟨/1⟩.");
        Assert.False(vm.Segments[2].HasIssues);
    }

    [AvaloniaFact]
    public async Task ThePane_ShowsTheMatches_AndCtrlNumberOrItsButtonInsertsOne()
    {
        var path = _files.Write("matches.xlf", MatchesTests.WithMatches);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(InlineContent.FromText("Press OK."), InlineContent.FromText("Nyomd meg: OK.")));
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(tm, "Project TM", "en", "hu"));
        var status = window.FindControl<TextBlock>("MatchesStatus")!;
        Assert.Equal("Select a segment to see its matches.", status.Text);

        Click(window, TargetFrames(window)[2]);
        await WaitFor(() => MatchRows(window).Count == 2);
        Assert.False(status.IsVisible);
        Assert.Equal("Project TM (en → hu)", window.FindControl<TextBlock>("MemoryName")!.Text);
        Assert.Equal(["Ctrl+1 96%", "Ctrl+2 90%"], MatchRows(window).Select(r =>
            $"{r.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("insertMatch")).Content} {r.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("matchScore")).GetLogicalDescendants().OfType<TextBlock>().Single().Text}"));

        // The file's match has a word the current source doesn't: struck through.
        var fileMatchSource = MatchRows(window)[1].GetVisualDescendants().OfType<InlineContentBlock>().Single(b => b.Classes.Contains("matchSource"));
        var removed = Assert.Single(fileMatchSource.Inlines!.OfType<Run>(), r => r.TextDecorations is not null);
        Assert.Equal(" now", removed.Text);

        // Ctrl+2 from inside the editor: the match goes in with the current codes, and the editor keeps the keyboard.
        var editor = Assert.Single(VisibleEditors(window));
        Press(window, PhysicalKey.Digit2, RawInputModifiers.Control);
        Assert.Equal("Most nyomd meg: ⟨1⟩OK⟨/1⟩.", vm.Segments[2].TargetText);
        Assert.Equal(2, EditorChips(editor).Count);
        Assert.Equal("Inserted the 90% match.", window.FindControl<TextBlock>("StatusText")!.Text);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        // The button does the same, without taking the keyboard from the editor.
        Click(window, MatchRows(window)[0].GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("insertMatch")));
        Assert.Equal("Nyomd meg: OK.", vm.Segments[2].TargetText);
        Assert.StartsWith("Inserted the 96% match.", window.FindControl<TextBlock>("StatusText")!.Text, StringComparison.Ordinal);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        // Ctrl+Z undoes the insertion.
        Press(window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("Most nyomd meg: ⟨1⟩OK⟨/1⟩.", vm.Segments[2].TargetText);
    }

    [AvaloniaFact]
    public async Task TheMenu_OpensATm_AndCtrlEnterFeedsTheRepetitionBelow()
    {
        var path = _files.Write("repetitions.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>Hello world.</source></segment></unit>
                <unit id="u2"><segment><source>Hello world.</source></segment></unit>
              </file>
            </xliff>
            """);
        var dialogs = new FakeDialogs(path) { MemoryToCreate = _files.PathOf("project.sctm") };
        var (window, vm) = Show(dialogs);
        await vm.LoadAsync(path);
        // Items of a closed submenu aren't kept up to date, so their commands are asked.
        var closeItem = window.FindControl<MenuItem>("CloseMemoryItem")!;
        Assert.False(closeItem.Command!.CanExecute(null));

        window.FindControl<MenuItem>("NewMemoryItem")!.Command!.Execute(null);
        await WaitFor(() => vm.TranslationMemory is not null);
        Assert.Equal("project.sctm (en → hu)", window.FindControl<TextBlock>("MemoryName")!.Text);
        Assert.True(closeItem.Command.CanExecute(null));

        Click(window, TargetFrames(window)[0]);
        window.KeyTextInput("Helló világ.");
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitFor(() => vm.Segments[1].TargetText.Length > 0);

        // The repetition got the translation just stored, unconfirmed, in its editor.
        var editor = Assert.Single(VisibleEditors(window));
        Assert.Same(vm.Segments[1].EditModel, editor.Model);
        Assert.Equal("Helló világ.", editor.Editor.Document.Text);
        Assert.Equal("Initial", vm.Segments[1].State);
        Assert.Equal("Inserted the 100% match automatically. Confirm it with Ctrl+Enter.", window.FindControl<TextBlock>("StatusText")!.Text);
        await vm.ShutDownAsync();
    }

    [AvaloniaFact]
    public async Task TheNoticeLine_SaysWhenASegmentWasntStored()
    {
        var path = _files.Write("errors.xlf", """
            <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
              <file id="f1">
                <unit id="u1"><segment><source>Open <ph id="1" canDelete="no"/>.</source><target>Megnyitás.</target></segment></unit>
                <unit id="u2"><segment><source>Next.</source></segment></unit>
              </file>
            </xliff>
            """);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        await vm.UseTranslationMemoryAsync(OpenTranslationMemory.Create(_files.PathOf("project.sctm"), "en", "hu"));
        var notice = window.FindControl<SelectableTextBlock>("NoticeText")!;
        Assert.False(notice.IsVisible);

        Click(window, TargetFrames(window)[0]);
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);

        Assert.True(notice.IsVisible);
        Assert.Equal("u1 was confirmed but not stored in project.sctm: its target has code errors.", notice.Text);
        await vm.ShutDownAsync();
    }

    // Closing the window closes the TM after its queued work, and keeps it remembered for next time.
    [AvaloniaFact]
    public async Task ClosingTheWindow_ClosesTheTm()
    {
        var tmPath = _files.PathOf("project.sctm");
        var settings = new JsonSettingsStore(_files.PathOf("settings.json"));
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null) { MemoryToOpen = tmPath }, settings);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        SqliteTranslationMemory.Create(tmPath, "en", "hu").Dispose();
        await vm.OpenTranslationMemoryCommand.ExecuteAsync(null);
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        await WaitFor(() => closed);

        Assert.Null(vm.TranslationMemory);
        Assert.Equal(tmPath, settings.Load().TranslationMemoryPath);
    }

    [AvaloniaFact]
    public async Task CtrlK_SearchesTheSelection_AndInsertKeepsTheKeyboardInTheEditor()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(InlineContent.FromText("Hello there."), InlineContent.FromText("Szia, helló.")));
        tm.Add(new TmEntry(InlineContent.FromText("Goodbye, world."), InlineContent.FromText("Viszlát, világ.")));
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(tm, "TM", "en", "hu"));

        // A selection in the source column: its sources are searched.
        Click(window, TargetFrames(window)[0]);
        var source = ContentBlocks(window, "source")[0];
        source.SelectionStart = 0;
        source.SelectionEnd = 5;
        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        await WaitFor(() => vm.Concordance.SearchTask.IsCompleted);
        Assert.Equal(1, window.FindControl<TabControl>("PaneTabs")!.SelectedIndex);
        Assert.Equal(("Hello", false), (vm.Concordance.Query, vm.Concordance.SearchTargets));
        var row = Assert.Single(ConcordanceRows(window));
        var hit = Assert.Single(row.GetVisualDescendants().OfType<InlineContentBlock>().Single(b => b.Classes.Contains("resultSource")).Inlines!.OfType<Run>(), r => r.FontWeight == Avalonia.Media.FontWeight.Bold);
        Assert.Equal("Hello", hit.Text);

        // A selection in the editor: the translations are searched.
        source.SelectionEnd = 0;
        var editor = Assert.Single(VisibleEditors(window));
        window.KeyTextInput("Szia világ");
        Dispatcher.UIThread.RunJobs();
        editor.Editor.Select(5, 5);
        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        await WaitFor(() => vm.Concordance.SearchTask.IsCompleted);
        Assert.Equal(("világ", true), (vm.Concordance.Query, vm.Concordance.SearchTargets));

        // Insert goes in at the caret, the selected term stays, and the editor keeps the keyboard.
        Click(window, Assert.Single(ConcordanceRows(window)).GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("insertResult")));
        Assert.Equal("Szia világViszlát, világ.", vm.Segments[0].TargetText);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        // Ctrl+1 with the concordance showing only shows the matches.
        var target = vm.Segments[0].TargetText;
        Press(window, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.Equal(0, window.FindControl<TabControl>("PaneTabs")!.SelectedIndex);
        Assert.Equal(target, vm.Segments[0].TargetText);
    }

    [AvaloniaFact]
    public async Task CtrlK_WithoutASelection_OpensTheSearchBox_EnterSearches_EscapeReturns()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(path);
        await vm.LoadAsync(path);
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(InlineContent.FromText("Save the file."), InlineContent.FromText("Mentsd a fájlt.")));
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(tm, "TM", "en", "hu"));
        Click(window, TargetFrames(window)[0]);
        var editor = Assert.Single(VisibleEditors(window));

        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        var box = window.FindControl<TextBox>("ConcordanceBox")!;
        await WaitFor(() => box.IsFocused);

        window.KeyTextInput("fil");
        Press(window, PhysicalKey.Enter);
        await WaitFor(() => vm.Concordance.Results.Count == 1);
        Assert.Equal("1 entry.", window.FindControl<TextBlock>("ConcordanceStatus")!.Text);

        Press(window, PhysicalKey.Escape);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);
    }

    private static List<InlineContentBlock> ContentBlocks(Window window, string role)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<InlineContentBlock>().Where(b => b.Classes.Contains(role)).ToList();
    }

    private static List<Border> ConcordanceRows(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.FindControl<ItemsControl>("ConcordanceList")!.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("match")).ToList();
    }

    private static List<Border> MatchRows(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("match")).ToList();
    }

    // The real confirmation window, through the real dialog service.
    [AvaloniaTheory]
    [InlineData("ConfirmButton", true)]
    [InlineData("CancelButton", false)]
    public async Task ConfirmDialog_ReturnsTheClickedChoice(string button, bool expected)
    {
        var owner = new Window();
        owner.Show();
        var answer = new AvaloniaDialogService(owner).ConfirmAsync("Copy source", "Replace?", "Replace");
        Dispatcher.UIThread.RunJobs();
        var dialog = owner.OwnedWindows.OfType<ConfirmDialog>().Single();
        Assert.Equal("Replace", dialog.FindControl<Button>("ConfirmButton")!.Content);

        dialog.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => answer.IsCompleted);

        Assert.Equal(expected, await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TargetLanguageDialog_AcceptsOnlyAValidCode()
    {
        var owner = new Window();
        owner.Show();
        var answer = new AvaloniaDialogService(owner).AskTargetLanguageAsync("en", t => LanguageTag.Problem(t, "en"));
        Dispatcher.UIThread.RunJobs();
        var dialog = owner.OwnedWindows.OfType<TargetLanguageDialog>().Single();
        var box = dialog.FindControl<TextBox>("LanguageBox")!;
        var ok = dialog.FindControl<Button>("OkButton")!;
        Assert.False(ok.IsEnabled);
        Assert.True(box.IsFocused);

        dialog.KeyTextInput("EN");
        Dispatcher.UIThread.RunJobs();
        Assert.False(ok.IsEnabled);
        Assert.Equal("That's the source language (en).", dialog.FindControl<TextBlock>("Problem")!.Text);

        box.Text = "hu ";
        Dispatcher.UIThread.RunJobs();
        Assert.True(ok.IsEnabled);
        Press(dialog, PhysicalKey.Enter);
        await WaitFor(() => answer.IsCompleted);

        Assert.Equal("hu", await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TargetLanguageDialog_Escape_MeansCancel()
    {
        var owner = new Window();
        owner.Show();
        var answer = new AvaloniaDialogService(owner).AskTargetLanguageAsync("en", t => LanguageTag.Problem(t, "en"));
        Dispatcher.UIThread.RunJobs();

        Press(owner.OwnedWindows.OfType<TargetLanguageDialog>().Single(), PhysicalKey.Escape);
        await WaitFor(() => answer.IsCompleted);

        Assert.Null(await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task ConfirmDialog_ClosedWithoutAnswer_MeansNo()
    {
        var owner = new Window();
        owner.Show();
        var answer = new AvaloniaDialogService(owner).ConfirmAsync("Copy source", "Replace?", "Replace");
        Dispatcher.UIThread.RunJobs();

        owner.OwnedWindows.OfType<ConfirmDialog>().Single().Close();
        await WaitFor(() => answer.IsCompleted);

        Assert.False(await answer);
        owner.Close();
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] ProblemClass(Border chip) => [.. chip.Classes.Where(c => c == "problem")];

    private static List<Border> EditorChips(TargetEditor editor)
    {
        Dispatcher.UIThread.RunJobs();
        return editor.Editor.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")).ToList();
    }

    private static InlineContentBlock ContentBlock(Window window, string role)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<InlineContentBlock>().Single(b => b.Classes.Contains(role));
    }

    private static List<Border> Chips(InlineContentBlock block) =>
        block.Inlines!.OfType<InlineUIContainer>().Select(c => Assert.IsType<Border>(c.Child)).ToList();

    private static string Label(Border chip) =>
        string.Join(" ", chip.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text ?? "",
        InlineUIContainer { Child: Border chip } => chip.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("label")).Text ?? "",
        _ => throw new InvalidOperationException($"Unexpected inline {inline.GetType().Name}."),
    };

    private static (Window Owner, Task<UnsavedChangesChoice> Prompt, UnsavedChangesDialog Dialog) ShowPrompt()
    {
        var owner = new Window();
        owner.Show();
        var prompt = new AvaloniaDialogService(owner).AskToSaveChangesAsync("sample.xlf");
        Dispatcher.UIThread.RunJobs();
        return (owner, prompt, owner.OwnedWindows.OfType<UnsavedChangesDialog>().Single());
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(string path) => Show(new FakeDialogs(path));

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static List<Border> TargetFrames(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("targetFrame")).ToList();
    }

    private static List<InlineContentBlock> TargetViews(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<InlineContentBlock>().Where(b => b.Classes.Contains("target")).ToList();
    }

    private static List<TargetEditor> VisibleEditors(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<TargetEditor>().Where(e => e.IsEffectivelyVisible).ToList();
    }

    private static string PlainText(InlineContentBlock block) => string.Concat((block.Inlines ?? []).Select(InlineText));

    private static void Click(Window window, Control control, bool onText = false)
    {
        var point = control.TranslatePoint(onText ? new Point(8, control.Bounds.Height / 2) : new Point(4, 4), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // Commands run async file I/O; keep the dispatcher going until the effect is visible.
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The window did not reach the expected state.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
