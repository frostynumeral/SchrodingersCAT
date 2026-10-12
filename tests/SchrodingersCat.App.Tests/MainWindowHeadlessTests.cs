using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using SchrodingersCat.App.Controls;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.App.Views;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;
using static SchrodingersCat.App.Tests.Headless;

namespace SchrodingersCat.App.Tests;

/// <summary>Drives the real window on Avalonia's headless platform: bindings, templates, key bindings and all.</summary>
public sealed class MainWindowHeadlessTests : IDisposable
{
    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    // ---------------------------------------------------------------- opening, editing, saving

    [AvaloniaFact]
    public async Task OpenEditSave_ThroughTheWindow()
    {
        var path = _files.Write("sample.xlf", TestFiles.Sample);
        var (window, vm) = Show(new FakeDialogs(path));

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

        Type(window, "Helló világ.");
        Assert.Equal("Helló világ.", editor.Editor.Document.Text);
        Assert.True(vm.IsDirty);
        Assert.Equal("Translated", vm.Segments[0].State);
        Assert.Equal("sample.xlf • — SchrödingersCAT", window.Title);

        // Ctrl+S reaches the window from inside the editor.
        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await WaitFor(() => !vm.IsDirty);

        var saved = await ReadAsync(path);
        Assert.Equal("Helló világ.", saved.Units.First().Segments[0].Target!.ToPlainText());
        Assert.False(window.FindControl<Border>("ErrorBanner")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task SelectingAnotherSegment_MovesTheEditorAndKeepsTheEdit()
    {
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample);
        Click(window, TargetFrames(window)[0]);
        Type(window, "Szia");

        Click(window, TargetViews(window)[1], onText: true); // on the text itself, not the frame around it
        var editor = Assert.Single(VisibleEditors(window));
        Assert.Same(vm.Segments[1].EditModel, editor.Model);
        Assert.Equal("Szia", PlainText(TargetViews(window)[0]));

        // The caret starts at the end, so typing adds to the target.
        Type(window, " Szia.");
        Assert.Equal("Viszlát. Szia.", vm.Segments[1].TargetText);

        // Each segment keeps its own undo history.
        Click(window, TargetFrames(window)[0]);
        Press(window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("", vm.Segments[0].TargetText);
        Assert.Equal("Viszlát. Szia.", vm.Segments[1].TargetText);
    }

    [AvaloniaFact]
    public async Task TheStatusLine_ShowsTheActiveSegmentsEditMessage()
    {
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample);
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
        var (window, vm) = Show(new FakeDialogs(_files.Write("broken.xlf", "<xliff")));

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
        var dialogs = new FakeDialogs(null) { Choice = choice };
        var (window, vm, path) = await OpenAsync("sample.xlf", TestFiles.Sample, dialogs);
        vm.Segments[0].TargetText = "Helló világ.";
        var closed = TrackClosed(window);

        window.Close();
        await WaitFor(() => dialogs.Asked.Count > 0);
        await WaitFor(() => closed() == closes);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.Equal(closes, closed());
        var saved = await ReadAsync(path);
        Assert.Equal(savedTarget, saved.Units.First().Segments[0].Target?.ToPlainText());
    }

    [AvaloniaFact]
    public async Task ClosingWithoutChanges_DoesNotAsk()
    {
        var dialogs = new FakeDialogs(null);
        var (window, _, _) = await OpenAsync("sample.xlf", TestFiles.Sample, dialogs);
        var closed = TrackClosed(window);

        window.Close();
        await WaitFor(closed);

        Assert.Empty(dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task CtrlO_WithUnsavedChanges_Cancel_ShowsNoPicker()
    {
        var dialogs = new FakeDialogs(null) { Choice = UnsavedChangesChoice.Cancel };
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample, dialogs);
        vm.Segments[0].TargetText = "Helló világ.";

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => dialogs.Asked.Count > 0);

        Assert.Equal(["save changes to sample.xlf?"], dialogs.Asked);
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task CtrlShiftS_SavesAs()
    {
        var copy = _files.PathOf("copy.xlf");
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample, new FakeDialogs(null) { SavePath = copy });

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitFor(() => vm.FilePath == copy);

        Assert.True(File.Exists(copy));
        Assert.Equal("copy.xlf — SchrödingersCAT", window.Title);
    }

    // ---------------------------------------------------------------- inline codes and issues

    [AvaloniaFact]
    public async Task InlineCodes_AreShownAsChips()
    {
        var path = _files.Write("tagged.xlf", Xliff("""
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
            """));
        var (window, vm) = Show(new FakeDialogs(path));

        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control);
        await WaitFor(() => vm.Segments.Count == 1);

        var source = One<InlineContentBlock>(window, "source");
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], Chips(source).Select(Label));
        Assert.Equal("Hello {0}, press ⟨bOKb⟩.", PlainText(source));

        var (placeholder, opening, closing) = (Chips(source)[0], Chips(source)[1], Chips(source)[2]);
        Assert.Equal(["chip", "standalone", "protected"], placeholder.Classes);
        Assert.Equal(["chip", "opening"], opening.Classes);
        Assert.Equal(["chip", "closing"], closing.Classes);
        Assert.Equal("Code 1\nReads as: Ann\nCan't be deleted.", ToolTip.GetTip(placeholder));
        Assert.Equal("Start of code 2\n<b class=\"warn\">", ToolTip.GetTip(opening));
        Assert.Equal("End of code 2\n</b>", ToolTip.GetTip(closing));

        // Not selected, the target shows the same chips, read-only; selected, the editor shows them.
        var target = One<InlineContentBlock>(window, "target");
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], Chips(target).Select(Label));
        Assert.Equal("Szia {0}, nyomd meg: ⟨bOKb⟩.", PlainText(target));

        Click(window, TargetFrames(window)[0]);
        Assert.Same(vm.Segments[0], vm.SelectedSegment);
        var editor = Assert.Single(VisibleEditors(window));
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], EditorChips(editor).Select(Label));
    }

    [AvaloniaFact]
    public async Task OpenEditTaggedSegmentSave_KeepsTheCodes()
    {
        var (window, vm, path) = await OpenAsync("tagged.xlf", Xliff("""
            <unit id="u1">
              <segment><source>Hello <ph id="1" disp="{0}" canDelete="no"/>, press <pc id="2" subType="xlf:b" type="fmt">OK</pc>.</source></segment>
            </unit>
            """));
        Assert.False(IssueBadge(window).IsVisible); // not translated yet

        Click(window, TargetFrames(window)[0]);
        var editor = Assert.Single(VisibleEditors(window));
        Type(window, "Szia ");

        // Both codes are missing: the protected one is an error, and the source chips are outlined. The quality checks
        // add two notes: a trailing space, and no full stop.
        var badge = IssueBadge(window);
        Assert.True(badge.IsVisible);
        Assert.Contains("error", badge.Classes);
        Assert.Equal("4", Label(badge));
        Assert.All(SourceChips(window), c => Assert.Contains("problem", c.Classes));

        // Clicking a source chip inserts it at the caret, and the editor keeps the keyboard.
        Click(window, SourceChips(window)[0]);
        window.KeyTextInput(", nyomd meg: ");
        Click(window, SourceChips(window)[1]); // a pair: the caret goes between its halves
        window.KeyTextInput("OK");
        Press(window, PhysicalKey.End);
        Type(window, ".");
        Assert.Equal(["{0} 🔒", "⟨b", "b⟩"], EditorChips(editor).Select(Label));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);
        Assert.False(IssueBadge(window).IsVisible);
        Assert.All(SourceChips(window), c => Assert.DoesNotContain("problem", c.Classes));

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        await WaitFor(() => !vm.IsDirty);

        var segment = (await ReadAsync(path)).Units.Single().Segments.Single();
        Assert.Equal(new InlineContent(
        [
            new InlineText("Szia "),
            new InlineCode("1", InlineCodeKind.Standalone) { Disp = "{0}", CanDelete = false },
            new InlineText(", nyomd meg: "),
            new InlineCode("2", InlineCodeKind.Opening) { SubType = "xlf:b", Type = "fmt", CanOverlap = false },
            new InlineText("OK"),
            new InlineCode("2", InlineCodeKind.Closing) { SubType = "xlf:b", Type = "fmt", CanOverlap = false },
            new InlineText("."),
        ]), segment.Target);
        Assert.Equal(SegmentState.Translated, segment.State);
    }

    // Regression: the active row's read-only target is hidden but still got its chips rebuilt after each edit, and
    // Avalonia's next render of the hidden block threw "Visual was invalidated during the render pass".
    [AvaloniaFact]
    public async Task EditingATaggedSegment_RendersWithTheReadOnlyTargetHidden()
    {
        var (window, vm, _) = await OpenAsync("tagged.xlf", TestFiles.Sample);
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
        var (window, _, _) = await OpenAsync("unknown.xlf", Xliff("""
            <unit id="u1">
              <segment><source>A<ph id="1"/></source><target>A<ph id="1"/><ph id="9"/></target></segment>
            </unit>
            """));

        Assert.Equal([false, true], Chips(One<InlineContentBlock>(window, "target")).Select(IsProblem));

        Click(window, TargetFrames(window)[0]);
        Assert.Equal([false, true], EditorChips(Assert.Single(VisibleEditors(window))).Select(IsProblem));
    }

    // ---------------------------------------------------------------- keyboard workflow (step 7)

    [AvaloniaFact]
    public async Task CtrlDownAndCtrlUp_MoveBetweenSegments_AndTheEditorFollows()
    {
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample);

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
        var (window, vm, _) = await OpenAsync("pretranslated.xlf", Xliff("""
            <unit id="u1"><segment><source>Hello.</source><target>Szia.</target></segment></unit>
            <unit id="u2"><segment><source>Bye.</source></segment></unit>
            """));
        Click(window, TargetFrames(window)[0]);

        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);

        Assert.Equal("Translated", vm.Segments[0].State);
        Assert.True(vm.IsDirty);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);

        // Nothing to confirm yet: it stays, and says why.
        Press(window, PhysicalKey.Enter, RawInputModifiers.Control);
        Assert.Same(vm.Segments[1], vm.SelectedSegment);
        Assert.Equal("Initial", vm.Segments[1].State);
        Assert.Equal("Translate the segment before confirming it.", StatusText(window));
    }

    [AvaloniaFact]
    public async Task CtrlShiftDown_GoesToTheNextSegmentWithCodeIssues()
    {
        var (window, vm, _) = await OpenAsync("issues.xlf", Xliff("""
            <unit id="u1"><segment><source>A<ph id="1"/></source><target>A<ph id="1"/></target></segment></unit>
            <unit id="u2"><segment><source>B<ph id="1"/><ph id="2"/></source><target>B<ph id="2"/><ph id="1"/></target></segment></unit>
            <unit id="u3"><segment><source>C<ph id="1"/></source><target>C</target></segment></unit>
            """));
        Click(window, TargetFrames(window)[0]);

        // u2 only has codes reordered (info), so it's skipped; u3 is missing a code (a warning).
        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Same(vm.Segments[2], vm.SelectedSegment);

        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Same(vm.Segments[2], vm.SelectedSegment);
        Assert.Equal("No segments with errors or warnings below this one.", vm.Segments[2].EditStatus);
    }

    [AvaloniaFact]
    public async Task CtrlInsert_CopiesTheSource_AskingFirstIfThereIsATranslation()
    {
        var dialogs = new FakeDialogs(null);
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample, dialogs);
        Click(window, TargetFrames(window)[2]);
        var tagged = vm.Segments[2];

        // No translation yet: copied without asking.
        Press(window, PhysicalKey.Insert, RawInputModifiers.Control);
        Assert.Equal("Press ⟨1⟩OK⟨/1⟩.", tagged.TargetText);
        Assert.Empty(dialogs.Asked);

        Type(window, "!");
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
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample);
        Click(window, TargetFrames(window)[2]);

        var source = All<InlineContentBlock>(window, "source")[2];
        source.Focus();
        source.SelectAll();
        Press(window, PhysicalKey.C, RawInputModifiers.Control);
        Assert.Equal("Press ⟨1⟩OK⟨/1⟩.", await window.Clipboard!.TryGetTextAsync());

        Click(window, Assert.Single(VisibleEditors(window)), onText: true);
        Press(window, PhysicalKey.V, RawInputModifiers.Control);
        await WaitFor(() => vm.Segments[2].TargetText == "Press ⟨1⟩OK⟨/1⟩.");
        Assert.DoesNotContain(vm.Segments[2].Issues, i => i.Code is not null); // only "same as source"
    }

    // ---------------------------------------------------------------- matches, translation memory, concordance

    [AvaloniaFact]
    public async Task ThePane_ShowsTheMatches_AndCtrlNumberOrItsButtonInsertsOne()
    {
        var (window, vm, _) = await OpenAsync("matches.xlf", MatchesTests.WithMatches);
        await UseMemoryAsync(vm, "Project TM", ("Press OK.", "Nyomd meg: OK."));
        var status = window.FindControl<TextBlock>("MatchesStatus")!;
        Assert.Equal("Select a segment to see its matches.", status.Text);

        Click(window, TargetFrames(window)[2]);
        await WaitFor(() => MatchRows(window).Count == 2);
        Assert.False(status.IsVisible);
        Assert.Equal("Project TM (en → hu)", window.FindControl<TextBlock>("MemoryName")!.Text);
        Assert.Equal(["Ctrl+1 96%", "Ctrl+2 90%"], MatchRows(window).Select(r =>
            $"{One<Button>(r, "insertMatch").Content} {One<Border>(r, "matchScore").GetLogicalDescendants().OfType<TextBlock>().Single().Text}"));

        // The file's match has a word the current source doesn't: struck through.
        var fileMatchSource = One<InlineContentBlock>(MatchRows(window)[1], "matchSource");
        var removed = Assert.Single(fileMatchSource.Inlines!.OfType<Run>(), r => r.TextDecorations is not null);
        Assert.Equal(" now", removed.Text);

        // Ctrl+2 from inside the editor: the match goes in with the current codes, and the editor keeps the keyboard.
        var editor = Assert.Single(VisibleEditors(window));
        Press(window, PhysicalKey.Digit2, RawInputModifiers.Control);
        Assert.Equal("Most nyomd meg: ⟨1⟩OK⟨/1⟩.", vm.Segments[2].TargetText);
        Assert.Equal(2, EditorChips(editor).Count);
        Assert.Equal("Inserted the 90% match.", StatusText(window));
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        // The button does the same, without taking the keyboard from the editor.
        Click(window, One<Button>(MatchRows(window)[0], "insertMatch"));
        Assert.Equal("Nyomd meg: OK.", vm.Segments[2].TargetText);
        Assert.StartsWith("Inserted the 96% match.", StatusText(window), StringComparison.Ordinal);
        Assert.True(editor.Editor.TextArea.IsKeyboardFocusWithin);

        // Ctrl+Z undoes the insertion.
        Press(window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("Most nyomd meg: ⟨1⟩OK⟨/1⟩.", vm.Segments[2].TargetText);
    }

    [AvaloniaFact]
    public async Task TheMenu_OpensATm_AndCtrlEnterFeedsTheRepetitionBelow()
    {
        var dialogs = new FakeDialogs(null) { MemoryToCreate = _files.PathOf("project.sctm") };
        var (window, vm, _) = await OpenAsync("repetitions.xlf", Xliff("""
            <unit id="u1"><segment><source>Hello world.</source></segment></unit>
            <unit id="u2"><segment><source>Hello world.</source></segment></unit>
            """), dialogs);
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
        Assert.Equal("Inserted the 100% match automatically. Confirm it with Ctrl+Enter.", StatusText(window));
        await vm.ShutDownAsync();
    }

    [AvaloniaFact]
    public async Task TheNoticeLine_SaysWhenASegmentWasntStored()
    {
        var (window, vm, _) = await OpenAsync("errors.xlf", Xliff("""
            <unit id="u1"><segment><source>Open <ph id="1" canDelete="no"/>.</source><target>Megnyitás.</target></segment></unit>
            <unit id="u2"><segment><source>Next.</source></segment></unit>
            """));
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
        var (window, vm) = Show(new FakeDialogs(null) { MemoryToOpen = tmPath }, settings);
        SqliteTranslationMemory.Create(tmPath, "en", "hu").Dispose();
        await vm.OpenTranslationMemoryCommand.ExecuteAsync(null);
        var closed = TrackClosed(window);

        window.Close();
        await WaitFor(closed);

        Assert.Null(vm.TranslationMemory);
        Assert.Equal(tmPath, settings.Load().TranslationMemoryPath);
    }

    [AvaloniaFact]
    public async Task CtrlK_SearchesTheSelection_AndInsertKeepsTheKeyboardInTheEditor()
    {
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample);
        await UseMemoryAsync(vm, "TM", ("Hello there.", "Szia, helló."), ("Goodbye, world.", "Viszlát, világ."));

        // A selection in the source column: its sources are searched.
        Click(window, TargetFrames(window)[0]);
        var source = All<InlineContentBlock>(window, "source")[0];
        source.SelectionStart = 0;
        source.SelectionEnd = 5;
        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        await WaitFor(() => vm.Concordance.SearchTask.IsCompleted);
        Assert.Equal(1, window.FindControl<TabControl>("PaneTabs")!.SelectedIndex);
        Assert.Equal(("Hello", false), (vm.Concordance.Query, vm.Concordance.SearchTargets));
        var row = Assert.Single(ConcordanceRows(window));
        var hit = Assert.Single(One<InlineContentBlock>(row, "resultSource").Inlines!.OfType<Run>(), r => r.FontWeight == FontWeight.Bold);
        Assert.Equal("Hello", hit.Text);

        // A selection in the editor: the translations are searched.
        source.SelectionEnd = 0;
        var editor = Assert.Single(VisibleEditors(window));
        Type(window, "Szia világ");
        editor.Editor.Select(5, 5);
        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        await WaitFor(() => vm.Concordance.SearchTask.IsCompleted);
        Assert.Equal(("világ", true), (vm.Concordance.Query, vm.Concordance.SearchTargets));

        // Insert goes in at the caret, the selected term stays, and the editor keeps the keyboard.
        Click(window, One<Button>(Assert.Single(ConcordanceRows(window)), "insertResult"));
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
        var (window, vm, _) = await OpenAsync("sample.xlf", TestFiles.Sample);
        await UseMemoryAsync(vm, "TM", ("Save the file.", "Mentsd a fájlt."));
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

    // ---------------------------------------------------------------- help

    [AvaloniaFact]
    public async Task HelpOpenLogFolder_SaysWhereTheLogsAre_WhenTheFolderCantBeShown()
    {
        var (window, vm) = Show(new FakeDialogs(null) { FolderOpens = false });

        window.FindControl<MenuItem>("OpenLogFolderItem")!.Command!.Execute(null);
        await WaitFor(() => vm.Notice is not null);

        Assert.Equal("_Help", window.FindControl<MenuItem>("HelpMenu")!.Header);
        Assert.EndsWith(MainWindowViewModel.LogFolder, window.FindControl<SelectableTextBlock>("NoticeText")!.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A document with one file, <c>en</c> to <c>hu</c>, holding <paramref name="units"/>.</summary>
    private static string Xliff(string units) => $"""
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
        {units}
          </file>
        </xliff>
        """;

    /// <summary>Writes the file, shows the window and loads the file into it.</summary>
    private async Task<(MainWindow Window, MainWindowViewModel ViewModel, string Path)> OpenAsync(
        string name, string content, FakeDialogs? dialogs = null)
    {
        var path = _files.Write(name, content);
        var (window, vm) = Show(dialogs ?? new FakeDialogs(path));
        await vm.LoadAsync(path);
        return (window, vm, path);
    }

    private static async Task<TranslationDocument> ReadAsync(string path) =>
        await new Xliff20Serializer().ReadFileAsync(path, TestContext.Current.CancellationToken);

    private static async Task UseMemoryAsync(MainWindowViewModel vm, string name, params (string Source, string Target)[] entries)
    {
        var tm = new InMemoryTranslationMemory();
        foreach (var (source, target) in entries)
            tm.Add(new TmEntry(InlineContent.FromText(source), InlineContent.FromText(target)));
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(tm, name, "en", "hu"));
    }

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private static string? StatusText(Window window) => window.FindControl<TextBlock>("StatusText")!.Text;

    private static Border IssueBadge(Window window) => One<Border>(window, "issueBadge");

    private static List<Border> SourceChips(Window window) => Chips(One<InlineContentBlock>(window, "source"));

    private static bool IsProblem(Border chip) => chip.Classes.Contains("problem");

    private static List<Border> MatchRows(Window window) => All<Border>(window.FindControl<ItemsControl>("MatchList")!, "match");

    private static List<Border> ConcordanceRows(Window window) => All<Border>(window.FindControl<ItemsControl>("ConcordanceList")!, "match");
}
