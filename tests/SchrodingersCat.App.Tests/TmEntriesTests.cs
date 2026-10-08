using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>Step 3 of <c>docs/design/tm-maintenance.md</c> in the view model: the entries window. Real TM files.</summary>
public sealed class TmEntriesTests : IAsyncDisposable
{
    private const string Document = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1"><segment><source>Save the file.</source></segment></unit>
          </file>
        </xliff>
        """;

    private static readonly DateTime Monday = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Local);

    private readonly TestFiles _files = new();
    private readonly List<MainWindowViewModel> _viewModels = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            await vm.ShutDownAsync();
        _files.Dispose();
    }

    [Fact]
    public async Task ManageEntries_ShowsTheWindow_WithTheFiltersListsAndTheNewestEntriesFirst()
    {
        var (vm, dialogs, _) = await OpenAsync(Sample());
        Assert.True(vm.ManageEntriesCommand.CanExecute(null));

        await vm.ManageEntriesCommand.ExecuteAsync(null);
        var entries = dialogs.ShownEntries!;

        Assert.Equal(["All documents", "a.xlf", "b.xlf"], entries.Documents.Select(d => d.Label));
        Assert.Equal(["All users", "anna", "béla"], entries.Users.Select(u => u.Label));
        Assert.Equal(["Close it.", "Open the file.", "Save the file."], Sources(entries));
        Assert.Equal("3 entries", entries.Header);
        Assert.Equal("a.xlf · 2026-09-28 · anna", entries.Entries[2].Details);
        Assert.Null(entries.Status);

        // The same window again while the TM stays open.
        await vm.ManageEntriesCommand.ExecuteAsync(null);
        Assert.Same(entries, dialogs.ShownEntries);
        Assert.Equal(["show entries", "show entries"], dialogs.Asked);
    }

    [Fact]
    public void ManageEntries_NeedsATm() =>
        Assert.False(Create(new FakeDialogs(null)).ManageEntriesCommand.CanExecute(null));

    [Fact]
    public async Task TheList_LoadsAPageAtATime()
    {
        var (vm, dialogs, _) = await OpenAsync([.. Enumerable.Range(0, 450).Select(i => Entry($"Sentence {i}.", $"Mondat {i}."))]);
        var entries = await ShowAsync(vm, dialogs);
        Assert.Equal(TmEntriesViewModel.PageSize, entries.Entries.Count);
        Assert.Equal("Sentence 449.", entries.Entries[0].Hit.Entry.Source.ToPlainText());

        entries.LoadMoreCommand.Execute(null);
        entries.LoadMoreCommand.Execute(null); // a page is on its way: nothing more
        await entries.Loading;
        Assert.Equal(400, entries.Entries.Count);
        entries.LoadMoreCommand.Execute(null);
        await entries.Loading;
        entries.LoadMoreCommand.Execute(null);
        await entries.Loading;

        Assert.Equal(450, entries.Entries.Count);
        Assert.Equal("Sentence 0.", entries.Entries[^1].Hit.Entry.Source.ToPlainText());
        Assert.Equal(450, entries.Entries.Select(e => e.Stored!.Id).Distinct().Count());
    }

    [Fact]
    public async Task TheFilters_ApplyAtOnce_ButTheTextOnlyOnEnter()
    {
        var (vm, dialogs, _) = await OpenAsync(Sample());
        var entries = await ShowAsync(vm, dialogs);

        entries.SearchText = "fil";
        Assert.Equal(3, entries.Entries.Count); // not yet
        entries.RefreshCommand.Execute(null);
        await entries.Loading;
        Assert.Equal(["Open the file.", "Save the file."], Sources(entries));
        Assert.Equal("2 of 3 entries", entries.Header);
        Assert.Equal(["file"], entries.Entries[0].SourceRuns.OfType<TextRun>().Where(r => r.IsHit).Select(r => r.Text));

        entries.SearchTranslations = true; // searches again: the text applies to the translations now
        await entries.Loading;
        Assert.Empty(entries.Entries);
        Assert.Equal("No entries match the filters.", entries.Status);

        entries.SearchText = "";
        entries.SelectedDocument = entries.Documents.Single(d => d.Value == "a.xlf");
        await entries.Loading;
        Assert.Equal(["Close it.", "Save the file."], Sources(entries));

        // One change at a time: without a UI thread, the refreshes' continuations could run side by side.
        entries.SelectedDocument = entries.Documents[0];
        await entries.Loading;
        entries.ChangedFrom = Monday.AddDays(1);
        await entries.Loading;
        entries.ChangedTo = Monday.AddDays(1); // the whole day
        await entries.Loading;
        Assert.Equal(["Open the file."], Sources(entries));
    }

    [Fact]
    public async Task DeletingTheSelected_AsksFirst_AndTheMainWindowFollows()
    {
        var (vm, dialogs, tm) = await OpenAsync(Sample());
        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;
        Assert.Equal(["Mentsd a fájlt.", "Nyisd meg a fájlt."], vm.Matches.Select(m => m.Match.Entry.Target.ToPlainText()));
        var entries = await ShowAsync(vm, dialogs);
        var selected = new List<object> { entries.Entries[0], entries.Entries[2] };

        await entries.DeleteSelectedCommand.ExecuteAsync(selected); // declined
        Assert.Equal("Delete 2 entries from project.sctm? This can't be undone.", dialogs.Messages[^1]);
        Assert.Equal(3, tm.Count);

        dialogs.Confirm = true;
        await entries.DeleteSelectedCommand.ExecuteAsync(selected);
        await vm.MatchLookup;

        Assert.Equal(["Open the file."], Sources(entries));
        Assert.Equal(("1 entry", 1), (entries.Header, tm.Count));
        Assert.StartsWith("Deleted 2 entries.", entries.Status, StringComparison.Ordinal);
        Assert.Equal(["Nyisd meg a fájlt."], vm.Matches.Select(m => m.Match.Entry.Target.ToPlainText()));
    }

    [Fact]
    public async Task DeleteAll_DeletesWhatTheListShows_ThenCompacts()
    {
        var (vm, dialogs, tm) = await OpenAsync(Sample());
        dialogs.Confirm = true;
        var entries = await ShowAsync(vm, dialogs);
        entries.SelectedUser = entries.Users.Single(u => u.Value == "anna");
        await entries.Loading;
        entries.SearchText = "something typed but not applied";

        await entries.DeleteAllCommand.ExecuteAsync(null);

        Assert.Equal(
            "Delete the 2 entries the filters find from project.sctm? This can't be undone. To keep a copy, export the TM first (Translation memory → Export TMX…).",
            dialogs.Messages[^1]);
        Assert.Equal(["Open the file."], tm.Entries().Select(e => e.Source.ToPlainText()));
        Assert.Equal("Deleted 2 entries. The file was compacted.", entries.Status);
        Assert.Empty(entries.Entries);
        Assert.False(entries.DeleteAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditingInTheWindow_UpdatesTheRowWhereItIs()
    {
        var (vm, dialogs, tm) = await OpenAsync(Sample());
        dialogs.EditEntry = _ => InlineContent.FromText("Nyisd meg!");
        var entries = await ShowAsync(vm, dialogs);

        await entries.EditCommand.ExecuteAsync(entries.Entries[1].Stored);
        await entries.Loading;

        Assert.Equal(["Zárd be.", "Nyisd meg!", "Mentsd a fájlt."], entries.Entries.Select(e => e.Hit.Entry.Target.ToPlainText()));
        Assert.Contains(tm.Entries(), e => e.Target.ToPlainText() == "Nyisd meg!");
    }

    [Fact]
    public async Task ADeleteInThePane_LeavesTheWindow()
    {
        var (vm, dialogs, _) = await OpenAsync(Sample());
        dialogs.Confirm = true;
        var entries = await ShowAsync(vm, dialogs);
        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;

        await vm.DeleteMatchCommand.ExecuteAsync(1);
        await entries.Loading;

        Assert.Equal(["Close it.", "Open the file."], Sources(entries));
        Assert.Equal("2 entries", entries.Header);
    }

    [Fact]
    public async Task ClosingTheTm_ClosesTheWindow()
    {
        var (vm, dialogs, _) = await OpenAsync(Sample());
        var entries = await ShowAsync(vm, dialogs);
        var closed = false;
        entries.CloseRequested += (_, _) => closed = true;

        await vm.CloseTranslationMemoryCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Null(vm.Entries);
    }

    // ---------------------------------------------------------------- helpers

    private static TmEntry[] Sample() =>
    [
        Entry("Save the file.", "Mentsd a fájlt.") with { Document = "a.xlf", ChangedBy = "anna" },
        Entry("Open the file.", "Nyisd meg a fájlt.") with { Document = "b.xlf", ChangedBy = "béla", Changed = Monday.AddDays(1) },
        Entry("Close it.", "Zárd be.") with { Document = "a.xlf", ChangedBy = "anna", Changed = Monday.AddDays(2) },
    ];

    private async Task<(MainWindowViewModel ViewModel, FakeDialogs Dialogs, ITranslationMemory Memory)> OpenAsync(TmEntry[] entries)
    {
        var dialogs = new FakeDialogs(null);
        var vm = Create(dialogs);
        await vm.LoadAsync(_files.Write("doc.xlf", Document));
        var open = OpenTranslationMemory.Create(_files.PathOf("project.sctm"), "en", "hu");
        open.Memory.AddRange(entries);
        await vm.UseTranslationMemoryAsync(open);
        return (vm, dialogs, open.Memory);
    }

    private static async Task<TmEntriesViewModel> ShowAsync(MainWindowViewModel vm, FakeDialogs dialogs)
    {
        await vm.ManageEntriesCommand.ExecuteAsync(null);
        return dialogs.ShownEntries!;
    }

    private MainWindowViewModel Create(FakeDialogs dialogs)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), dialogs);
        _viewModels.Add(vm);
        return vm;
    }

    private static List<string> Sources(TmEntriesViewModel entries) => [.. entries.Entries.Select(e => e.Hit.Entry.Source.ToPlainText())];

    private static TmEntry Entry(string source, string target) =>
        new(InlineContent.FromText(source), InlineContent.FromText(target)) { Changed = Monday };
}
