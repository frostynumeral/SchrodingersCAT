using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>
/// Step 2 of <c>docs/design/tm-maintenance.md</c> in the view model: editing and deleting the TM entries behind
/// matches and concordance hits. Real TM files in a temp directory.
/// </summary>
public sealed class EntryMaintenanceTests : IAsyncDisposable
{
    /// <summary>One segment to translate (with a match from the file), then one already translated.</summary>
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" xmlns:mtc="urn:oasis:names:tc:xliff:matches:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1">
              <mtc:matches>
                <mtc:match ref="#s1" matchQuality="75"><source>Save these files.</source><target>Mentsd ezeket.</target></mtc:match>
              </mtc:matches>
              <segment id="s1"><source>Save the file.</source><target>Mentsd a fájlt.</target></segment>
            </unit>
            <unit id="u2"><segment state="translated"><source>Open the file.</source><target>Nyisd meg a fájlt.</target></segment></unit>
          </file>
        </xliff>
        """;

    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly TestFiles _files = new();
    private readonly List<MainWindowViewModel> _viewModels = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            await vm.ShutDownAsync();
        _files.Dispose();
    }

    // ---------------------------------------------------------------- deleting

    [Fact]
    public async Task CtrlShiftN_DeletesTheMatchsEntry_AfterAsking()
    {
        var (vm, dialogs, tm) = await OpenAsync(Entry("Save the file.", "Mentsd a fájt."), Entry("Save the files.", "Mentsd a fájlokat."));
        await SelectAsync(vm, 0);
        Assert.Equal(["Mentsd a fájt.", "Mentsd a fájlokat.", "Mentsd ezeket."], vm.Matches.Select(m => m.Match.Entry.Target.ToPlainText()));

        await vm.DeleteMatchCommand.ExecuteAsync(1); // declined
        Assert.Equal(["confirm: Delete entry"], dialogs.Asked);
        Assert.Equal("Delete “Save the file.” → “Mentsd a fájt.” from project.sctm? This can't be undone.", Assert.Single(dialogs.Messages));
        Assert.Equal(2, tm.Count);

        dialogs.Confirm = true;
        await vm.DeleteMatchCommand.ExecuteAsync(1);
        await vm.MatchLookup;

        Assert.Equal(["Save the files."], tm.Entries().Select(e => e.Source.ToPlainText()));
        Assert.Equal(["Mentsd a fájlokat.", "Mentsd ezeket."], vm.Matches.Select(m => m.Match.Entry.Target.ToPlainText()));
        Assert.Equal("Deleted the entry from project.sctm.", vm.Notice);
    }

    [Fact]
    public async Task CtrlShiftN_ExplainsWhatItCantDelete()
    {
        var (vm, dialogs, _) = await OpenAsync(Entry("Save the files.", "Mentsd a fájlokat."));
        await SelectAsync(vm, 0);
        var segment = vm.Segments[0];

        await vm.DeleteMatchCommand.ExecuteAsync(2);
        Assert.Equal("Match 2 comes with the file, not from a translation memory, so there is no entry to delete.", segment.EditStatus);
        Assert.Null(vm.Matches[1].Stored);
        await vm.DeleteMatchCommand.ExecuteAsync(3);
        Assert.Equal("There is no match 3.", segment.EditStatus);
        Assert.Empty(dialogs.Asked);

        // With the concordance showing, the key only shows the matches.
        vm.PaneTab = 1;
        await vm.DeleteMatchCommand.ExecuteAsync(1);
        Assert.Equal(0, vm.PaneTab);
        Assert.Empty(dialogs.Asked);
    }

    [Fact]
    public async Task DeletingAConcordanceHit_SearchesAgain()
    {
        var (vm, dialogs, tm) = await OpenAsync(Entry("Save the file.", "Mentsd a fájt."), Entry("Save the files.", "Mentsd a fájlokat."));
        dialogs.Confirm = true;
        vm.Concordance.SearchFor("save", inTargets: false);
        await vm.Concordance.SearchTask;
        vm.Concordance.Query = "something else typed since";

        await vm.DeleteEntryCommand.ExecuteAsync(vm.Concordance.Results[0].Stored);
        await vm.Concordance.SearchTask;

        Assert.Equal(1, tm.Count);
        Assert.Equal(["Save the files."], vm.Concordance.Results.Select(r => r.Hit.Entry.Source.ToPlainText()));
        Assert.Equal("1 entry.", vm.Concordance.Status);
    }

    [Fact]
    public async Task AStaleEntry_IsReported()
    {
        var (vm, dialogs, tm) = await OpenAsync(Entry("Save the file.", "Mentsd a fájt."));
        await SelectAsync(vm, 0);
        var stored = vm.Matches[0].Stored!;
        dialogs.Confirm = true;
        dialogs.EditEntry = _ => InlineContent.FromText("Mentsd a fájlt.");
        tm.Delete([stored.Id]); // gone in the meantime

        await vm.EditEntryCommand.ExecuteAsync(stored);
        Assert.Equal("The entry is no longer in project.sctm, so it wasn't updated.", vm.Notice);

        await vm.DeleteEntryCommand.ExecuteAsync(stored);
        Assert.Equal("The entry is no longer in project.sctm.", vm.Notice);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task WithoutATm_EntriesCantBeChanged()
    {
        var vm = Create(new FakeDialogs(null));
        await vm.LoadAsync(_files.Write("doc.xlf", Document));
        var stored = new StoredEntry(new TmEntryId(1), Entry("Save the file.", "Mentsd."));

        Assert.False(vm.EditEntryCommand.CanExecute(stored));
        Assert.False(vm.DeleteEntryCommand.CanExecute(stored));
    }

    // ---------------------------------------------------------------- editing

    [Fact]
    public async Task EditingAMatch_UpdatesTheEntry_AndThePaneShowsIt()
    {
        var (vm, dialogs, tm) = await OpenAsync(Entry("Save the file.", "Mentsd a fájt.") with { Document = "other.xlf", PreviousSource = "Before." });
        await SelectAsync(vm, 0);
        EntryEditorViewModel? editor = null;
        dialogs.EditEntry = e =>
        {
            editor = e;
            e.Model.Select(0, e.Model.Length);
            e.Model.InsertText("Mentsd a fájlt.");
            return e.TrySave();
        };
        var before = DateTimeOffset.UtcNow;

        await vm.EditEntryCommand.ExecuteAsync(vm.Matches[0].Stored);
        await vm.MatchLookup;

        Assert.Equal(("Save the file.", "project.sctm · other.xlf · 2026-09-28"), (editor!.Entry.Source.ToPlainText(), editor.Details));
        var entry = Assert.Single(tm.Entries());
        Assert.Equal(("Mentsd a fájlt.", "other.xlf", "Before.", Environment.UserName),
            (entry.Target.ToPlainText(), entry.Document, entry.PreviousSource, entry.ChangedBy));
        Assert.InRange(entry.Changed, before, DateTimeOffset.UtcNow);
        Assert.Equal("Mentsd a fájlt.", vm.Matches[0].Match.Entry.Target.ToPlainText());
        Assert.Equal("Updated the entry in project.sctm.", vm.Notice);
    }

    [Fact]
    public async Task CancellingOrChangingNothing_LeavesTheEntry()
    {
        var (vm, dialogs, tm) = await OpenAsync(Entry("Save the file.", "Mentsd a fájt."));
        await SelectAsync(vm, 0);
        var stored = vm.Matches[0].Stored!;

        await vm.EditEntryCommand.ExecuteAsync(stored);
        dialogs.EditEntry = e => e.TrySave();
        await vm.EditEntryCommand.ExecuteAsync(stored);

        Assert.Equal(Monday, Assert.Single(tm.Entries()).Changed);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task EditingIntoAnotherTranslation_MergesThem()
    {
        var (vm, dialogs, tm) = await OpenAsync(Entry("Save the file.", "Mentsd a fájt."), Entry("Save the file.", "Mentsd a fájlt.") with { Changed = Monday.AddDays(-1) });
        await SelectAsync(vm, 0);
        dialogs.EditEntry = _ => InlineContent.FromText("Mentsd a fájlt.");

        await vm.EditEntryCommand.ExecuteAsync(vm.Matches.Single(m => m.Match.Entry.Target.ToPlainText() == "Mentsd a fájt.").Stored);

        Assert.Equal(["Mentsd a fájlt."], tm.Entries().Select(e => e.Target.ToPlainText()));
        Assert.Equal("Updated the entry in project.sctm. It now has the same translation as another entry of that source, which was removed.", vm.Notice);
    }

    // Correcting in the TM what a segment of this document stored: confirming the segment again replaces the
    // corrected entry, rather than adding a second translation beside it.
    [Fact]
    public async Task EditingAnEntryThisDocumentStored_KeepsReConfirmingItReplacingIt()
    {
        var (vm, dialogs, tm) = await OpenAsync();
        await SelectAsync(vm, 0);
        vm.Segments[0].TargetText = "Mentsd a fájt.";
        vm.ConfirmSegmentCommand.Execute(null);
        await Task.WhenAll(vm.StoreTask, vm.MatchLookup);

        dialogs.EditEntry = _ => InlineContent.FromText("Mentsd el a fájlt.");
        await SelectAsync(vm, 0);
        await vm.EditEntryCommand.ExecuteAsync(vm.Matches[0].Stored);
        Assert.Equal("Mentsd el a fájlt.", vm.Segments[0].StoredTarget!.ToPlainText());

        vm.Segments[0].TargetText = "Mentsd a fájlt.";
        vm.ConfirmSegmentCommand.Execute(null);
        await Task.WhenAll(vm.StoreTask, vm.MatchLookup);

        Assert.Equal(["Mentsd a fájlt."], tm.Entries().Select(e => e.Target.ToPlainText()));
    }

    [Fact]
    public async Task DeletingAnEntryThisDocumentStored_ForgetsIt()
    {
        var (vm, dialogs, _) = await OpenAsync();
        dialogs.Confirm = true;
        await SelectAsync(vm, 1);
        Assert.Equal("Nyisd meg a fájlt.", vm.Segments[1].StoredTarget!.ToPlainText()); // translated when opened
        vm.ConfirmSegmentCommand.Execute(null);
        await Task.WhenAll(vm.StoreTask, vm.MatchLookup);

        await SelectAsync(vm, 1);
        await vm.DeleteEntryCommand.ExecuteAsync(vm.Matches[0].Stored);

        Assert.Null(vm.Segments[1].StoredTarget);
    }

    // ---------------------------------------------------------------- the entry editor

    [Fact]
    public void TheEditor_RefusesAnEmptyTranslation()
    {
        var editor = new EntryEditorViewModel(Entry("Save the file.", "Mentsd."), "TM");

        editor.Model.Select(0, editor.Model.Length);
        editor.Model.Delete(Editing.DeleteDirection.Backward);

        Assert.Null(editor.TrySave());
        Assert.Equal("An entry needs a translation. To remove it from the TM, delete the entry instead.", editor.Problem);
        editor.Model.InsertText("Mentsd!");
        Assert.Null(editor.Problem);
        Assert.Equal("Mentsd!", editor.TrySave()!.ToPlainText());
    }

    [Fact]
    public void TheEditor_RefusesNewCodeErrors_ButNotTheEntrysOwn()
    {
        var pair = new TmEntry(
            new InlineContent([new InlineText("Press "), Open("1"), new InlineText("OK"), Close("1")]),
            new InlineContent([new InlineText("Nyomd: "), Open("1"), new InlineText("OK"), Close("1")]));
        var editor = new EntryEditorViewModel(pair, "TM");

        editor.Model.MoveCaret(editor.Model.Length);
        editor.Model.Delete(Editing.DeleteDirection.Backward); // the closing half
        Assert.Contains("1", editor.TargetProblemCodes);
        Assert.Null(editor.TrySave());
        Assert.StartsWith("The translation has code errors:", editor.Problem, StringComparison.Ordinal);

        editor.Model.Undo();
        Assert.NotNull(editor.TrySave());

        // Half a pair whose other half was in the next segment: always an error in a unit of one segment.
        var half = new TmEntry(new InlineContent([Open("1"), new InlineText("Press")]), new InlineContent([Open("1"), new InlineText("Nyomd")]));
        var other = new EntryEditorViewModel(half, "TM");
        other.Model.MoveCaret(other.Model.Length);
        other.Model.InsertText(" meg");
        Assert.Equal("Nyomd meg", other.TrySave()!.ToPlainText());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs, ITranslationMemory Memory)> OpenAsync(params TmEntry[] entries)
    {
        var dialogs = new FakeDialogs(null);
        var vm = Create(dialogs);
        await vm.LoadAsync(_files.Write("doc.xlf", Document));
        var open = OpenTranslationMemory.Create(_files.PathOf("project.sctm"), "en", "hu");
        open.Memory.AddRange(entries);
        await vm.UseTranslationMemoryAsync(open);
        return (vm, dialogs, open.Memory);
    }

    private static async Task SelectAsync(MainWindowViewModel vm, int index)
    {
        vm.SelectedSegment = null;
        vm.SelectedSegment = vm.Segments[index];
        await vm.MatchLookup;
    }

    private MainWindowViewModel Create(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        _viewModels.Add(vm);
        return vm;
    }

    private static TmEntry Entry(string source, string target) =>
        new(InlineContent.FromText(source), InlineContent.FromText(target)) { Changed = Monday };

    private static InlineCode Open(string id) => new(id, InlineCodeKind.Opening);

    private static InlineCode Close(string id) => new(id, InlineCodeKind.Closing);
}
