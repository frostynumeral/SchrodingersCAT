using Avalonia.Headless.XUnit;
using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;
using static SchrodingersCat.App.Tests.TermbaseFiles;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 6 of <c>docs/design/termbase.md</c> in the view model: Ctrl+E and the entry dialog, adding a translation to an
/// entry, editing and deleting from the terms list, re-checking only the affected units, and the write queue. Real
/// termbase files.
/// </summary>
public sealed class TermEditingTests : IAsyncDisposable
{
    /// <summary>
    /// u1 has "file" with an allowed translation, u2 is untranslated with two terms, u3 and u4 have "folder" (u3 with
    /// "mappa", u4 without), u5 has "file" with a translation the termbase doesn't have, u6 no terms.
    /// </summary>
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Open the file.</source><target>Nyisd meg az állományt.</target></segment></unit>
            <unit id="u2"><segment><source>Save the file manager settings.</source></segment></unit>
            <unit id="u3"><segment><source>Close the folder.</source><target>Zárd be a mappát.</target></segment></unit>
            <unit id="u4"><segment><source>Print the folder list.</source><target>Nyomtasd ki a listát.</target></segment></unit>
            <unit id="u5"><segment><source>Delete the file.</source><target>Töröld a dossziét.</target></segment></unit>
            <unit id="u6"><segment><source>Nothing to see here.</source><target>Semmi.</target></segment></unit>
          </file>
        </xliff>
        """;

    private readonly TestFiles _files = new();
    private readonly List<MainWindowViewModel> _viewModels = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            await vm.ShutDownAsync();
        _files.Dispose();
    }

    // ---------------------------------------------------------------- adding a term

    [Fact]
    public async Task CtrlE_PrefillsTheDialog_AndAddsANewEntry_ShownAtOnce()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        vm.SelectedSegment = vm.Segments[2];
        Assert.Empty(vm.Terms);
        dialogs.EditTermEntry = editor =>
        {
            Assert.True(editor.IsAdding);
            Assert.Equal("Termbase “client” (en → hu)", editor.Header);
            Assert.Equal(["folder"], editor.SourceTerms.Select(r => r.Text));
            Assert.Equal(["mappa"], editor.TargetTerms.Select(r => r.Text));
            Assert.Equal(TermStatus.Unspecified, editor.TargetTerms[0].Status); // not exported as preferred
            Assert.False(editor.HasExisting);
            editor.TargetTerms[0].Status = TermStatus.Preferred;
            editor.EntryNote = "  A container for files. ";
            return editor.TrySave();
        };

        // Codes in the selection were spaces, and line breaks are whitespace too.
        await vm.AddTermCommand.ExecuteAsync(new TermSelection("  folder ", "mappa\n"));

        Assert.Equal("Added “folder” → “mappa” to client.", vm.Notice);
        var stored = vm.Termbase!.Index.Entries().Single(e => e.Entry.Source[0].Text == "folder");
        Assert.Equal((TermStatus.Preferred, "A container for files.", Environment.UserName), (stored.Entry.Target[0].Status, stored.Entry.Note, stored.Entry.ChangedBy));
        Assert.True(DateTimeOffset.UtcNow - stored.Entry.Changed < TimeSpan.FromMinutes(1));
        Assert.Equal(["folder"], vm.Terms.Select(t => t.SourceText));
        Assert.Contains(vm.Segments[2].SourceRuns.OfType<TextRun>(), r => r.Text == "folder" && r.Annotations.HasFlag(AnnotationStyle.TermbaseTerm));

        // In the file, not only in memory.
        await vm.ShutDownAsync();
        using var reopened = SqliteTermbase.Open(_files.PathOf("client.sctb"), TestContext.Current.CancellationToken);
        Assert.Equal(stored.Entry, reopened.Index.Find(stored.Id));
    }

    // Section 5: adding a term re-checks only the units whose source has it.
    [Fact]
    public async Task AddingATerm_RechecksOnlyTheUnitsWhoseSourceHasIt()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        var versions = vm.Segments.Select(s => s.Check.Version).ToList();
        var before = vm.Segments.Select(s => s.TermHits).ToList();
        dialogs.EditTermEntry = editor => editor.TrySave();
        vm.SelectedSegment = vm.Segments[3];

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("folder", "mappa"));

        Assert.Equal([false, false, true, true, false, false], vm.Segments.Select((s, i) => s.Check.Version != versions[i]));
        Assert.Empty(TermIssues(vm, 2));
        Assert.Equal("The termbase “client” translates “folder” as “mappa”, which isn't in the target.", Assert.Single(TermIssues(vm, 3)).Message);
        // The other segments' hits followed without a lookup, and equal one.
        Assert.Same(before[0], vm.Segments[0].TermHits);
        Assert.All(vm.Segments, s => Assert.Equal(vm.Termbase!.Index.Find(QaText.Of(s.Source)), s.TermHits));
        Assert.Equal(["folder"], vm.Terms.Select(t => t.SourceText));
    }

    // A term in more units than the UI thread re-checks at once: the whole document is checked again in the background.
    [Fact]
    public async Task ATermInManyUnits_IsCheckedAgainInTheBackground()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        vm.InlineRecheckLimit = 1;
        var fullCheck = vm.TermCheck;
        dialogs.EditTermEntry = editor => editor.TrySave();

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("folder", "mappa")); // u3 and u4

        Assert.NotSame(fullCheck, vm.TermCheck);
        Assert.All(vm.Segments, s => Assert.Equal(vm.Termbase!.Index.Find(QaText.Of(s.Source)), s.TermHits)); // the marks at once
        await vm.TermCheck;
        AssertUpToDate(vm);
        Assert.Single(TermIssues(vm, 3));
    }

    // Section 6: the dialog lists the entries with that source term, and offers to add the translation to one of them.
    [Fact]
    public async Task AddingATranslationToAnExistingEntry_KeepsItsIdAndStatuses_AndSatisfiesTheCheck()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        Assert.Single(TermIssues(vm, 4)); // "dosszié" isn't an allowed translation of "file" yet
        var count = vm.Termbase!.Index.Count;
        dialogs.EditTermEntry = editor =>
        {
            Assert.Equal([null, 1L], editor.Existing.Select(e => e.Stored?.Id.Value));
            Assert.Equal("Add to “file” → fájl, állomány", editor.Existing[1].Label);
            Assert.Same(editor.Existing[1], editor.SelectedExisting); // the entry, not a homonym, by default
            Assert.Equal("Adds “dosszié” to this entry. The terms it has keep their status and note.", editor.MergeSummary);
            return editor.TrySave();
        };
        vm.SelectedSegment = vm.Segments[4];

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("File", "dosszié"));

        var entry = vm.Termbase.Index.Find(new TermEntryId(1))!;
        Assert.Equal(count, vm.Termbase.Index.Count);
        Assert.Equal(
            [("állomány", TermStatus.Admitted), ("fájl", TermStatus.Preferred), ("akta", TermStatus.Forbidden), ("dosszié", TermStatus.Unspecified)],
            entry.Target.Select(t => (t.Text, t.Status)));
        Assert.Equal(["file"], entry.Source.Select(t => t.Text)); // "File" is the same term
        Assert.Equal("Computer files only.", entry.Note);
        Assert.Equal("Added “dosszié” to the entry “file” in client.", vm.Notice);
        Assert.Empty(TermIssues(vm, 4));
        Assert.Contains("dosszié", vm.Terms.Single(t => t.SourceText == "file").Translations.Select(t => t.Text));
    }

    [Fact]
    public async Task AddingToAnEntry_ThatHasEverything_IsRefusedInTheDialog_AndANewEntryCanBeChosen()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        dialogs.EditTermEntry = editor =>
        {
            Assert.Equal("This entry has these terms already.", editor.MergeSummary);
            Assert.Null(editor.TrySave());
            Assert.Equal("This entry has these terms already. Choose another entry, or make a new one.", editor.Problem);
            editor.SelectedExisting = editor.Existing[0];
            Assert.Null(editor.Problem); // a change clears it
            Assert.Equal("Makes a new entry beside those that have this term (a homonym).", editor.MergeSummary);
            return editor.TrySave();
        };

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("file", "fájl"));

        Assert.Equal(3, vm.Termbase!.Index.Count);
        Assert.Equal(2, vm.Termbase.Index.WithSourceTerm("file").Count);
    }

    // Ctrl+E without a selection opens the dialog empty.
    [Fact]
    public async Task CtrlE_WithoutASelection_OpensEmpty_AndCancellingWritesNothing()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        var index = vm.Termbase!.Index;

        await vm.AddTermCommand.ExecuteAsync(new TermSelection(null, null));

        var editor = Assert.Single(dialogs.TermEditors);
        Assert.Equal([""], editor.SourceTerms.Select(r => r.Text));
        Assert.Equal([""], editor.TargetTerms.Select(r => r.Text));
        Assert.Same(index, vm.Termbase.Index);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task AddingATerm_IsRefused_WithoutATermbase_WhileItLoads_OrWhenItDoesntServeTheDocument()
    {
        var (vm, dialogs) = await OpenAsync();
        vm.SelectedSegment = vm.Segments[0];

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("file", null));
        Assert.Equal("No termbase is open. Create one with Termbase → New… or open one with Termbase → Open…", vm.SelectedSegment.EditStatus);

        var gate = new TaskCompletionSource();
        vm.OpenTermbaseFile = async (p, ct) =>
        {
            await gate.Task;
            return await OpenTermbase.OpenAsync(p, ct);
        };
        dialogs.TermbaseToOpen = Write(_files.PathOf("client.sctb"), "en", "hu", Client);
        var opening = vm.OpenTermbaseCommand.ExecuteAsync(null);
        await vm.AddTermCommand.ExecuteAsync(new TermSelection("file", null));
        Assert.Equal("The termbase is still loading.", vm.SelectedSegment.EditStatus);
        gate.SetResult();
        await opening;

        await vm.UseTermbaseAsync(OpenTermbase.Create(_files.PathOf("de.sctb"), "en", "de"));
        await vm.AddTermCommand.ExecuteAsync(new TermSelection("file", null));
        Assert.Equal("The termbase “de” doesn't serve this document's languages.", vm.SelectedSegment.EditStatus);

        vm.SelectedSegment = null;
        await vm.AddTermCommand.ExecuteAsync(new TermSelection("file", null));
        Assert.Equal("The termbase “de” doesn't serve this document's languages.", vm.Notice);
        Assert.Empty(dialogs.TermEditors);
    }

    // Without a document, a term can still be added.
    [Fact]
    public async Task AddingATerm_WithoutADocument_StillWorks()
    {
        var dialogs = new FakeDialogs(null) { EditTermEntry = editor => editor.TrySave() };
        var vm = Create(dialogs);
        await vm.UseTermbaseAsync(CreateTermbase(Client));

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("printer", "nyomtató"));

        Assert.Equal("Added “printer” → “nyomtató” to client.", vm.Notice);
        Assert.Equal(3, vm.Termbase!.Index.Count);
    }

    // ---------------------------------------------------------------- editing and deleting

    [Fact]
    public async Task EditingAnEntry_ThatRenamesItsTerm_TakesTheMarksAndIssuesWhereOnlyTheOldTermWas()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        Assert.Single(TermIssues(vm, 4));
        var versions = vm.Segments.Select(s => s.Check.Version).ToList();
        dialogs.EditTermEntry = editor =>
        {
            Assert.False(editor.IsAdding);
            Assert.Equal(["file"], editor.SourceTerms.Select(r => r.Text));
            Assert.Equal([TermStatus.Admitted, TermStatus.Preferred, TermStatus.Forbidden], editor.TargetTerms.Select(r => r.Status));
            Assert.Equal("Computer files only.", editor.EntryNote);
            editor.SourceTerms[0].Text = "document";
            return editor.TrySave();
        };
        vm.SelectedSegment = vm.Segments[4];

        await vm.EditTermEntryCommand.ExecuteAsync(vm.Terms.Single().Hit);

        Assert.Equal("Updated the entry “document” in client.", vm.Notice);
        Assert.Empty(TermIssues(vm, 4));
        Assert.Empty(vm.Terms);
        Assert.DoesNotContain(vm.Segments[4].SourceRuns.OfType<TextRun>(), r => r.Annotations.HasFlag(AnnotationStyle.TermbaseTerm));
        Assert.Equal(["file manager"], vm.Segments[1].TermHits.Select(h => h.Entry.Source[0].Text));
        // u1, u2 and u5 had "file"; u3, u4 and u6 weren't checked again.
        Assert.Equal([true, true, false, false, true, false], vm.Segments.Select((s, i) => s.Check.Version != versions[i]));
        Assert.Equal(Environment.UserName, vm.Termbase!.Index.Find(new TermEntryId(1))!.ChangedBy);
    }

    [Fact]
    public async Task EditingAnEntry_WithoutAChange_WritesNothing()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        var index = vm.Termbase!.Index;
        dialogs.EditTermEntry = editor => editor.TrySave();
        vm.SelectedSegment = vm.Segments[0];

        await vm.EditTermEntryCommand.ExecuteAsync(vm.Terms.Single().Hit);

        Assert.Same(index, vm.Termbase.Index);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task DeletingAnEntry_AsksFirst_ThenTakesItsMarksIssuesAndRow()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        vm.SelectedSegment = vm.Segments[4];
        var hit = vm.Terms.Single().Hit;

        await vm.DeleteTermEntryCommand.ExecuteAsync(hit); // declined
        Assert.Equal("Delete “file” → “fájl, állomány, akta” from client? This can't be undone.", Assert.Single(dialogs.Messages));
        Assert.Equal(2, vm.Termbase!.Index.Count);

        dialogs.Confirm = true;
        await vm.DeleteTermEntryCommand.ExecuteAsync(hit);

        Assert.Equal("Deleted the entry “file” from client.", vm.Notice);
        Assert.Null(vm.Termbase.Index.Find(hit.Id));
        Assert.Empty(vm.Terms);
        Assert.Empty(TermIssues(vm, 4));
        Assert.Equal(["file manager"], vm.Segments[1].TermHits.Select(h => h.Entry.Source[0].Text));
    }

    [Fact]
    public void EditingAndDeleting_NeedATermbase()
    {
        var vm = Create(new FakeDialogs(null));
        var hit = new TermHit(new TermEntryId(1), Client[0], [0], []);

        Assert.False(vm.EditTermEntryCommand.CanExecute(hit));
        Assert.False(vm.DeleteTermEntryCommand.CanExecute(hit));
        Assert.False(vm.ImportTermsCommand.CanExecute(null));
        Assert.False(vm.ExportTermsCommand.CanExecute(null));
    }

    // A stale id never throws: the entry went (here outside the app) while the dialog was open, or before.
    [Fact]
    public async Task AnEntryThatWentMeanwhile_GivesANotice_NeverAnException()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        vm.SelectedSegment = vm.Segments[4];
        var hit = vm.Terms.Single().Hit;
        var termbase = vm.Termbase!;

        dialogs.EditTermEntry = editor =>
        {
            termbase.Termbase.Delete([hit.Id]);
            return editor.TrySave();
        };
        await vm.AddTermCommand.ExecuteAsync(new TermSelection("file", "dosszié"));
        Assert.Equal("The entry “file” is no longer in client, so nothing was added.", vm.Notice);
        await vm.TermCheck;
        Assert.Empty(vm.Terms); // the document caught up with the termbase

        dialogs.EditTermEntry = editor =>
        {
            editor.SourceTerms[0].Text = "document";
            return editor.TrySave();
        };
        await vm.EditTermEntryCommand.ExecuteAsync(hit);
        Assert.Equal("The entry “file” is no longer in client.", vm.Notice);

        dialogs.Confirm = true;
        await vm.DeleteTermEntryCommand.ExecuteAsync(hit);
        Assert.Equal("The entry “file” is no longer in client.", vm.Notice);

        // Edited away while the dialog was open.
        var other = vm.Termbase!.Index.Entries().Single();
        dialogs.EditTermEntry = editor =>
        {
            termbase.Termbase.Delete([other.Id]);
            editor.SourceTerms[0].Text = "browser";
            return editor.TrySave();
        };
        await vm.EditTermEntryCommand.ExecuteAsync(new TermHit(other.Id, other.Entry, [0], []));
        Assert.Equal("The entry “file manager” is no longer in client, so it wasn't updated.", vm.Notice);
        Assert.Null(vm.ErrorMessage);
    }

    // ITermbase: a write that fails leaves the termbase as it was, so nothing is applied.
    [Fact]
    public async Task AWriteThatFails_GoesToTheBanner_AndChangesNothing()
    {
        var (vm, dialogs) = await OpenAsync();
        var inner = new Hooked(Client) { Fail = true };
        await vm.UseTermbaseAsync(new OpenTermbase(inner, "client"));
        await vm.TermCheck;
        var index = vm.Termbase!.Index;
        dialogs.EditTermEntry = editor => editor.TrySave();

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("folder", "mappa"));

        Assert.Equal("Could not add the term to client: The disk is full.", vm.ErrorMessage);
        Assert.Same(index, vm.Termbase.Index);
        Assert.Same(index, vm.DocumentTerms!.Index);
    }

    // ---------------------------------------------------------------- the queue

    // Writes queue one after the other; the UI applies each, or, if it missed a snapshot, checks everything again. On
    // the UI thread, so the writes' continuations run one at a time, as in the app.
    [AvaloniaFact]
    public async Task WritesThatPileUp_AreAllApplied()
    {
        var (vm, dialogs) = await OpenAsync();
        var inner = new Hooked(Client) { Gate = new ManualResetEventSlim() };
        await vm.UseTermbaseAsync(new OpenTermbase(inner, "client"));
        await vm.TermCheck;
        var words = new Queue<string>(["folder", "list"]);
        dialogs.EditTermEntry = editor =>
        {
            editor.SourceTerms[0].Text = words.Dequeue();
            return editor.TrySave();
        };

        var first = vm.AddTermCommand.ExecuteAsync(new TermSelection(null, "x"));
        var second = vm.AddTermCommand.ExecuteAsync(new TermSelection(null, "y"));
        Assert.False(first.IsCompleted);
        inner.Gate.Set();
        await Task.WhenAll(first, second);
        await vm.TermCheck;

        AssertUpToDate(vm);
        Assert.Contains(vm.Segments[3].TermHits, h => h.Entry.Source[0].Text == "list");
    }

    // An edit opened on an entry while a Ctrl+E addition to it waits in the queue is made from the older version: it is
    // refused rather than written over the addition (it replaces the whole entry).
    [AvaloniaFact]
    public async Task AnEdit_OpenedWhileAnAdditionToTheEntryWaits_DoesntUndoIt()
    {
        var (vm, dialogs) = await OpenAsync();
        var inner = new Hooked(Client) { Gate = new ManualResetEventSlim() };
        await vm.UseTermbaseAsync(new OpenTermbase(inner, "client"));
        await vm.TermCheck;
        vm.SelectedSegment = vm.Segments[4];
        var hit = vm.Terms.Single().Hit;
        var edits = new Queue<Func<TermEntryEditorViewModel, TermEntryEdit?>>(
        [
            editor => editor.TrySave(), // into the existing "file" entry
            editor =>
            {
                editor.EntryNote = "Edited.";
                return editor.TrySave();
            },
        ]);
        dialogs.EditTermEntry = editor => edits.Dequeue()(editor);

        var adding = vm.AddTermCommand.ExecuteAsync(new TermSelection("file", "dosszié"));
        var editing = vm.EditTermEntryCommand.ExecuteAsync(hit);
        inner.Gate.Set();
        await Task.WhenAll(adding, editing);
        await vm.TermCheck;

        var stored = inner.Index.Find(hit.Id)!;
        Assert.Contains(stored.Target, t => t.Text == "dosszié");
        Assert.Equal("Computer files only.", stored.Note);
        Assert.Equal("The entry “file” in client changed while it was being edited, so it wasn't updated. Edit it again to start from the change.", vm.Notice);
        Assert.Null(vm.ErrorMessage);
        AssertUpToDate(vm);
    }

    // The document didn't have the snapshot the write started from: it takes the latest, and checks every unit again.
    [Fact]
    public async Task AWriteAfterASnapshotTheDocumentMissed_RechecksEverything()
    {
        var (vm, dialogs) = await OpenWithTermbaseAsync();
        vm.Termbase!.Termbase.Add(Entry("list", ("jegyzék", TermStatus.Preferred))); // outside the queue and the window
        dialogs.EditTermEntry = editor => editor.TrySave();

        await vm.AddTermCommand.ExecuteAsync(new TermSelection("folder", "mappa"));
        await vm.TermCheck;

        AssertUpToDate(vm);
        Assert.Equal(2, TermIssues(vm, 3).Count); // "folder" and "list"
    }

    // Closing the termbase while a write waits: the write still reaches the file; the document isn't touched by it.
    [AvaloniaFact]
    public async Task ClosingTheTermbase_WhileAWriteWaits_WritesIt_AndAppliesNothing()
    {
        var (vm, dialogs) = await OpenAsync();
        var inner = new Hooked(Client) { Gate = new ManualResetEventSlim() };
        await vm.UseTermbaseAsync(new OpenTermbase(inner, "client"));
        await vm.TermCheck;
        dialogs.EditTermEntry = editor => editor.TrySave();

        var adding = vm.AddTermCommand.ExecuteAsync(new TermSelection("folder", "mappa"));
        var closing = vm.CloseTermbaseCommand.ExecuteAsync(null);
        inner.Gate.Set();
        await Task.WhenAll(adding, closing);
        await vm.TermCheck;

        Assert.Equal(3, inner.Index.Count);
        Assert.Null(vm.Termbase);
        Assert.Empty(vm.Segments.SelectMany(s => s.TermHits));
        Assert.Null(vm.ErrorMessage);
    }

    // ---------------------------------------------------------------- the entry dialog's view model

    [Fact]
    public void TheDialog_TrimsTerms_LeavesBlankRowsOut_AndAllowsASourceOnlyEntry()
    {
        var editor = TermEntryEditorViewModel.ForAdd(TermIndex.Empty, "client", "en", "hu", "printer", "");
        editor.AddSynonymCommand.Execute(null);
        editor.SourceTerms[1].Text = "  print device ";
        editor.SourceTerms[1].Note = " ";
        editor.AddTranslationCommand.Execute(null);

        var edit = editor.TrySave()!;

        Assert.Null(edit.Id);
        Assert.False(edit.IsAddition);
        Assert.Equal([new Term("printer"), new Term("print device")], edit.Entry.Source);
        Assert.Empty(edit.Entry.Target);
        Assert.Null(edit.Entry.Note);
    }

    [Fact]
    public void TheDialog_RefusesNoSourceTerm_AndTheSameTermTwice_UntilChanged()
    {
        var editor = TermEntryEditorViewModel.ForAdd(TermIndex.Empty, "client", "en", "hu", " ", "fájl");
        Assert.Null(editor.TrySave());
        Assert.Equal("An entry needs a source term.", editor.Problem);
        editor.SourceTerms[0].Text = "file";
        Assert.Null(editor.Problem);

        editor.AddTranslationCommand.Execute(null);
        editor.TargetTerms[1].Text = "Fajl";
        Assert.Null(editor.TrySave());
        Assert.Equal("“fájl” and “Fajl” are the same term; keep one.", editor.Problem);
        editor.RemoveRowCommand.Execute(editor.TargetTerms[1]);
        Assert.Null(editor.Problem);
        Assert.NotNull(editor.TrySave());

        var edit = TermEntryEditorViewModel.ForEdit(new TermEntryId(3), Client[0], "client", "en", "hu");
        edit.SourceTerms[0].Text = "";
        Assert.Null(edit.TrySave());
        Assert.Equal("An entry needs a source term. To remove it, delete the entry instead.", edit.Problem);
    }

    [Fact]
    public void TheDialog_ListsTheEntriesWithTheFirstSourceTerm_AsItChanges()
    {
        var index = TermIndex.Of(Client.Select((e, i) => new StoredTermEntry(new TermEntryId(i + 1), e)));
        var editor = TermEntryEditorViewModel.ForAdd(index, "client", "en", "hu", "printer", "nyomtató");
        Assert.False(editor.HasExisting);
        Assert.Null(editor.MergeSummary);

        editor.SourceTerms[0].Text = "File  Manager";
        Assert.Equal([null, 2L], editor.Existing.Select(e => e.Stored?.Id.Value));
        Assert.Equal("Adds “nyomtató” to this entry. The terms it has keep their status and note.", editor.MergeSummary);
        editor.EntryNote = "Also the app.";
        Assert.Equal("Adds “nyomtató” to this entry, and the note. The terms it has keep their status and note.", editor.MergeSummary);

        editor.SourceTerms[0].Text = "files";
        Assert.False(editor.HasExisting);
        Assert.Null(editor.SelectedExisting);
    }

    [Fact]
    public void MergeInto_AppendsWhatTheEntryLacks_KeepingWhatItHas()
    {
        var entry = Client[0];
        var addition = new TermEntry(
            [new Term("FILE", TermStatus.Forbidden), new Term("data file")],
            [new Term("fájl", TermStatus.Forbidden) { Note = "ignored" }, new Term("dosszié", TermStatus.Admitted)],
            "Computer files only.", default, null);

        var merged = TermEntryEdit.MergeInto(entry, addition);

        Assert.Equal([new Term("file"), new Term("data file")], merged.Source);
        Assert.Equal([.. entry.Target, new Term("dosszié", TermStatus.Admitted)], merged.Target);
        Assert.Equal("Computer files only.", merged.Note); // contained already
        Assert.Equal("Computer files only.\nNot folders.", TermEntryEdit.MergeInto(entry, addition with { Note = "Not folders." }).Note);
        Assert.Same(entry, TermEntryEdit.MergeInto(entry, new TermEntry([new Term("file")], [new Term("állomány")], null, default, null)));
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertUpToDate(MainWindowViewModel vm)
    {
        var index = vm.Termbase!.Index;
        Assert.Same(index, vm.DocumentTerms!.Index);
        var source = index.ToTermSource(vm.Termbase.Name);
        foreach (var segment in vm.Segments)
        {
            Assert.Equal(index.Find(QaText.Of(segment.Source)), segment.TermHits);
            Assert.Equal(
                QaChecker.Check(segment.Unit, QaOptions.Default, source).Select(i => i.Message),
                segment.Check.QaIssues.Select(i => i.Message));
        }
    }

    internal static List<QaIssue> TermIssues(MainWindowViewModel vm, int segment) =>
        [.. vm.Segments[segment].Issues.Select(i => i.Qa).OfType<QaIssue>().Where(i => i.Kind == QaIssueKind.Terminology)];

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs)> OpenWithTermbaseAsync()
    {
        var (vm, dialogs) = await OpenAsync();
        await vm.UseTermbaseAsync(CreateTermbase(Client));
        await vm.TermCheck;
        return (vm, dialogs);
    }

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs)> OpenAsync()
    {
        var dialogs = new FakeDialogs(null);
        var vm = Create(dialogs);
        await vm.LoadAsync(_files.Write("doc.xlf", Document));
        Assert.Null(vm.ErrorMessage);
        return (vm, dialogs);
    }

    private MainWindowViewModel Create(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        _viewModels.Add(vm);
        return vm;
    }

    private OpenTermbase CreateTermbase(TermEntry[] entries)
    {
        var termbase = OpenTermbase.Create(_files.PathOf("client.sctb"), "en", "hu");
        termbase.Termbase.AddRange(entries);
        return termbase;
    }

    /// <summary>An in-memory termbase whose writes wait at a gate, or fail as a full disk would.</summary>
    private sealed class Hooked(params TermEntry[] entries) : ITermbase
    {
        private readonly InMemoryTermbase _inner = Filled(entries);

        public ManualResetEventSlim? Gate { get; init; }

        public bool Fail { get; init; }

        public string SourceLanguage => "en";

        public string TargetLanguage => "hu";

        public TermIndex Index => _inner.Index;

        public TermEntryId Add(TermEntry entry)
        {
            Before();
            return _inner.Add(entry);
        }

        public IReadOnlyList<TermEntryId> AddRange(IEnumerable<TermEntry> entries)
        {
            Before();
            return _inner.AddRange(entries);
        }

        public bool Update(TermEntryId id, TermEntry entry)
        {
            Before();
            return _inner.Update(id, entry);
        }

        public int Delete(IReadOnlyCollection<TermEntryId> ids)
        {
            Before();
            return _inner.Delete(ids);
        }

        public bool Compact() => false;

        public void Dispose()
        {
        }

        private void Before()
        {
            if (Gate is { } gate && !gate.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test never opened the gate.");
            if (Fail)
                throw new IOException("The disk is full.");
        }

        private static InMemoryTermbase Filled(TermEntry[] entries)
        {
            var termbase = new InMemoryTermbase("en", "hu");
            termbase.AddRange(entries);
            return termbase;
        }
    }
}
