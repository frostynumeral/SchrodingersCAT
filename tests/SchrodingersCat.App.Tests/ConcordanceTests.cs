using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>The Concordance tab's view model: searching the open TM, its status, cancelling, and inserting a result.</summary>
public sealed class ConcordanceTests : IAsyncDisposable
{
    private readonly TestFiles _files = new();
    private readonly List<MainWindowViewModel> _viewModels = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _viewModels)
            await vm.ShutDownAsync();
        _files.Dispose();
    }

    [Fact]
    public async Task Search_FindsSourcesOrTranslations_AndSaysHowMany()
    {
        var vm = await OpenAsync(Memory(("Delete the file.", "Töröld a fájlt."), ("Save all files.", "Ments el minden fájlt."), ("Open it.", "Nyisd meg.")));

        await SearchAsync(vm, "file");
        Assert.Equal(["Delete the file.", "Save all files."], vm.Concordance.Results.Select(r => r.Hit.Entry.Source.ToPlainText()));
        Assert.Equal("2 entries.", vm.Concordance.Status);
        Assert.Equal("TM · 2026-09-28", vm.Concordance.Results[0].Details);

        vm.Concordance.SearchTargets = true;
        await SearchAsync(vm, "nyis");
        Assert.Equal("Nyisd", Assert.Single(Assert.Single(vm.Concordance.Results).TargetRuns.OfType<TextRun>(), r => r.IsHit).Text);
        Assert.Equal("1 entry.", vm.Concordance.Status);
    }

    [Fact]
    public async Task TheStatus_SaysWhyThereAreNoResults_OrHowManyAreShown()
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null));
        _viewModels.Add(vm);
        Assert.Equal("Type a word or phrase and press Enter, or select text and press Ctrl+K.", vm.Concordance.Status);

        await SearchAsync(vm, "file");
        Assert.Equal("Open a translation memory to search it.", vm.Concordance.Status);

        var tm = new InMemoryTranslationMemory();
        for (var i = 0; i < 120; i++)
            tm.Add(new TmEntry(Text($"File {i}."), Text($"Fájl {i}.")));
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(tm, "TM", "en", "hu"));

        await SearchAsync(vm, " ?! ");
        Assert.Equal("Type a word or phrase to search for.", vm.Concordance.Status);
        await SearchAsync(vm, "zebra");
        Assert.Equal("No hits for 'zebra'.", vm.Concordance.Status);
        await SearchAsync(vm, "file");
        Assert.Equal("120 entries; showing the best 100.", vm.Concordance.Status);
        Assert.Equal(100, vm.Concordance.Results.Count);
    }

    // A search still running when the next starts is dropped: only the latest results show.
    [Fact]
    public async Task ANewSearch_CancelsTheRunningOne()
    {
        using var gate = new ManualResetEventSlim();
        var vm = await OpenAsync(new GatedMemory(gate, Text("Delete the file."), Text("Save it.")));

        vm.Concordance.Query = "file";
        vm.Concordance.SearchCommand.Execute(null);
        var first = vm.Concordance.SearchTask;
        Assert.Equal("Searching…", vm.Concordance.Status);
        vm.Concordance.Query = "save";
        vm.Concordance.SearchCommand.Execute(null);
        gate.Set();
        await first;
        await vm.Concordance.SearchTask;

        Assert.Equal("Save it.", Assert.Single(vm.Concordance.Results).Hit.Entry.Source.ToPlainText());
    }

    [Fact]
    public async Task ChangingTheTm_ClearsTheResults()
    {
        var vm = await OpenAsync(Memory(("Delete the file.", "Töröld a fájlt.")));
        await SearchAsync(vm, "file");
        Assert.Single(vm.Concordance.Results);

        await vm.UseTranslationMemoryAsync(null);

        Assert.Empty(vm.Concordance.Results);
        Assert.Equal("Type a word or phrase and press Enter, or select text and press Ctrl+K.", vm.Concordance.Status);
    }

    [Fact]
    public async Task ADamagedTm_IsReportedInTheBanner()
    {
        var vm = await OpenAsync(new GatedMemory(null) { Fail = true });

        await SearchAsync(vm, "file");

        Assert.Equal("Could not search TM: Entry 7 is damaged.", vm.ErrorMessage);
        Assert.Null(vm.Concordance.Status);
    }

    // At the caret, keeping a selection (the term Ctrl+K searched for), as text: codes become their text or a space.
    [Fact]
    public async Task Insert_PutsTheTranslationAtTheCaret_AsText()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Text("One two three"), new InlineContent([
            new InlineText("Egy"), new InlineCode("1", InlineCodeKind.Standalone), new InlineText("kettő"),
            new InlineCode("2", InlineCodeKind.Standalone) { Equiv = "•" }, new InlineCode("3", InlineCodeKind.Opening), new InlineText("három"),
            new InlineCode("3", InlineCodeKind.Closing)])));
        var vm = await OpenAsync(tm);
        await SearchAsync(vm, "two");
        var result = Assert.Single(vm.Concordance.Results);
        Assert.False(vm.Concordance.InsertCommand.CanExecute(result)); // no segment selected

        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;
        var segment = vm.Segments[0];
        segment.TargetText = "Szia világ";
        segment.EditModel.Select(0, 4); // "Szia", caret after it
        vm.Concordance.InsertCommand.Execute(result);

        Assert.Equal("SziaEgy kettő•három világ", segment.TargetText);
        Assert.Equal("Inserted the translation from the concordance.", segment.EditStatus);
    }

    [Fact]
    public void TextOf_TurnsStandaloneCodesIntoTheirTextOrASpace() =>
        Assert.Equal("Line 1 Line 2 – done", ConcordanceViewModel.TextOf(new InlineContent([
            new InlineText("Line 1"), new InlineCode("1", InlineCodeKind.Standalone) { SubType = "xlf:lb" }, new InlineText("Line 2"),
            new InlineCode("2", InlineCodeKind.Standalone) { Equiv = " – " }, new InlineMarker("m1", InlineMarkerKind.Opening),
            new InlineText("done"), new InlineMarker("m1", InlineMarkerKind.Closing)])));

    private async Task<MainWindowViewModel> OpenAsync(ITranslationMemory memory)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null));
        _viewModels.Add(vm);
        await vm.LoadAsync(_files.Write("doc.xlf", TestFiles.Sample));
        await vm.UseTranslationMemoryAsync(new OpenTranslationMemory(memory, "TM", "en", "hu"));
        return vm;
    }

    private static async Task SearchAsync(MainWindowViewModel vm, string query)
    {
        vm.Concordance.Query = query;
        vm.Concordance.SearchCommand.Execute(null);
        await vm.Concordance.SearchTask;
    }

    private static InMemoryTranslationMemory Memory(params (string Source, string Target)[] pairs)
    {
        var tm = new InMemoryTranslationMemory();
        foreach (var (source, target) in pairs)
            tm.Add(new TmEntry(Text(source), Text(target)) { Changed = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero) });
        return tm;
    }

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    /// <summary>A TM whose entries are read only once the gate opens (from the queue's thread), or that fails.</summary>
    private sealed class GatedMemory(ManualResetEventSlim? gate, params InlineContent[] sources) : ITranslationMemory
    {
        public bool Fail { get; init; }

        public int Count => sources.Length;

        public IEnumerable<TmEntry> Entries()
        {
            gate?.Wait(TimeSpan.FromSeconds(10));
            if (Fail)
                throw new TmFormatException("Entry 7 is damaged.");
            return [.. sources.Select(s => new TmEntry(s, InlineContent.FromText("x")))];
        }

        public IReadOnlyList<TmMatch> Lookup(
            InlineContent source, string? previousSource = null, string? nextSource = null,
            int threshold = Ranking.DefaultThreshold, int maxMatches = Ranking.DefaultMaxMatches) => [];

        public void Add(TmEntry entry) => throw new NotSupportedException();

        public void AddRange(IEnumerable<TmEntry> entries) => throw new NotSupportedException();

        public bool Remove(TmEntry entry) => throw new NotSupportedException();

        public void Replace(TmEntry previous, TmEntry entry) => throw new NotSupportedException();
    }
}
