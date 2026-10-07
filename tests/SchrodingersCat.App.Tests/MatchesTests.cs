using SchrodingersCat.App.Services;
using SchrodingersCat.App.ViewModels;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Xliff;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.Tests;

/// <summary>The matches pane's view model: the file's and the TM's matches, background lookups, inserting (step 4).</summary>
public sealed class MatchesTests : IDisposable
{
    /// <summary>Three segments; the file carries matches for each (u2's points at an annotation, not a segment).</summary>
    public const string WithMatches = """
        <xliff xmlns="urn:oasis:names:tc:xliff:document:2.0" xmlns:mtc="urn:oasis:names:tc:xliff:matches:2.0" version="2.0" srcLang="en" trgLang="hu">
          <file id="f1">
            <unit id="u1">
              <mtc:matches>
                <mtc:match ref="#s2" matchQuality="80" origin="Client TM"><source>Goodbye now.</source><target>Viszlát most.</target></mtc:match>
                <mtc:match ref="#s1" matchQuality="75"><source>Hello there.</source><target>Szia ott.</target></mtc:match>
              </mtc:matches>
              <segment id="s1"><source>Hello world.</source></segment>
              <segment id="s2"><source>Goodbye.</source></segment>
            </unit>
            <unit id="u2">
              <mtc:matches>
                <mtc:match ref="#m1" matchQuality="90"><source>Press <pc id="9">OK</pc> now.</source><target>Most nyomd meg: <pc id="9">OK</pc>.</target></mtc:match>
              </mtc:matches>
              <segment><source>Press <pc id="1">OK</pc>.</source></segment>
            </unit>
          </file>
        </xliff>
        """;

    private static readonly DateTimeOffset Noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task TheFilesMatches_ShowForTheSelectedSegment()
    {
        var vm = await OpenAsync(WithMatches);
        Assert.Empty(vm.Matches);
        Assert.Equal("Select a segment to see its matches.", vm.MatchesStatus);

        vm.SelectedSegment = vm.Segments[0];
        var match = Assert.Single(vm.Matches);
        Assert.Equal((1, "Ctrl+1", "75%", "From the file"), (match.Number, match.Shortcut, match.Score, match.Details));
        Assert.Null(vm.MatchesStatus);

        vm.SelectedSegment = vm.Segments[1];
        Assert.Equal("From the file: Client TM", Assert.Single(vm.Matches).Details);

        // u2's match points at something other than a segment, but u2 has only the one segment to translate.
        vm.SelectedSegment = vm.Segments[2];
        Assert.Equal("90%", Assert.Single(vm.Matches).Score);

        vm.SelectedSegment = null;
        Assert.Empty(vm.Matches);
    }

    [Fact]
    public async Task TheTmsMatches_AreLookedUpAndMergedWithTheFilesBestFirst()
    {
        var vm = await OpenAsync(WithMatches);
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Text("Hello world."), Text("Helló világ.")) { NextSource = "Goodbye.", Changed = Noon, ChangedBy = "adam", Document = "/work/old.xlf" });
        tm.Add(new TmEntry(Text("hello world."), Text("helló világ.")) { Changed = Noon });
        vm.TranslationMemory = new OpenTranslationMemory(tm, "Project TM");

        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;

        Assert.Equal(
            [(1, "101%", "Helló világ."), (2, "99%", "helló világ."), (3, "75%", "Szia ott.")],
            vm.Matches.Select(m => (m.Number, m.Score, m.Target.Target.ToPlainText())));
        Assert.Equal($"Project TM · old.xlf · {Noon.ToLocalTime():yyyy-MM-dd} · adam", vm.Matches[0].Details);
        Assert.True(vm.Matches[0].IsExact);
        Assert.False(vm.Matches[1].IsExact);
    }

    [Fact]
    public async Task Lookups_PassTheNeighbouringSourcesAsContext()
    {
        var vm = await OpenAsync(TestFiles.Sample);
        var tm = new FakeMemory(_ => []);
        vm.TranslationMemory = new OpenTranslationMemory(tm, "TM");

        vm.SelectedSegment = vm.Segments[1];
        await vm.MatchLookup;
        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;

        Assert.Equal([("Goodbye.", "Hello world.", "Press OK."), ("Hello world.", null, "Goodbye.")], tm.Lookups);
    }

    [Fact]
    public async Task OpeningATm_LooksUpTheSelectedSegment()
    {
        var vm = await OpenAsync(TestFiles.Sample);
        vm.SelectedSegment = vm.Segments[0];
        Assert.Equal("No matches. No translation memory is open.", vm.MatchesStatus);

        vm.TranslationMemory = new OpenTranslationMemory(new InMemoryTranslationMemory(), "Empty");
        await vm.MatchLookup;
        Assert.Equal("No matches.", vm.MatchesStatus);

        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Text("Hello world."), Text("Helló világ.")));
        vm.TranslationMemory = new OpenTranslationMemory(tm, "Full");
        await vm.MatchLookup;
        Assert.Equal("Helló világ.", Assert.Single(vm.Matches).Target.Target.ToPlainText());
    }

    [Fact]
    public async Task InsertingAMatch_PutsItsTranslationWithTheCurrentCodesIntoTheTarget()
    {
        var vm = await OpenAsync(TestFiles.Sample);
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Content("Press ", Open("a"), "OK", Close("a"), "."), Content("Nyomd meg az ", Open("a"), "OK", Close("a"), " gombot.")));
        vm.TranslationMemory = new OpenTranslationMemory(tm, "TM");
        vm.SelectedSegment = vm.Segments[2];
        await vm.MatchLookup;
        var segment = vm.Segments[2];

        vm.InsertMatchCommand.Execute(1);

        Assert.Equal("Nyomd meg az ⟨1⟩OK⟨/1⟩ gombot.", segment.TargetText);
        Assert.Equal("Inserted the 100% match.", segment.EditStatus);
        Assert.False(segment.HasIssues);
        Assert.Equal("Translated", segment.State);
        Assert.True(vm.IsDirty);

        // One undo step brings back the old (empty) target; the state stays (decision 8).
        segment.EditModel.Undo();
        Assert.Equal("", segment.TargetText);
        Assert.Equal("Translated", segment.State);
    }

    [Fact]
    public async Task InsertingAMatchWhoseCodesDontAllFit_SaysSo()
    {
        var vm = await OpenAsync(WithMatches);
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Text("Press OK."), Text("Nyomd meg: OK.")));
        vm.TranslationMemory = new OpenTranslationMemory(tm, "TM");
        vm.SelectedSegment = vm.Segments[2];
        await vm.MatchLookup;

        // The file's 90% match with codes comes first, then the TM's, which has none.
        Assert.Equal(["90%", "96%"], vm.Matches.Select(m => m.Score).Order());
        vm.Matches.Single(m => m.Score == "96%").InsertCommand.Execute(null);

        var segment = vm.Segments[2];
        Assert.Equal("Nyomd meg: OK.", segment.TargetText);
        Assert.Equal("Inserted the 96% match. Not every code found its place: check the codes (F9 inserts the next missing one).", segment.EditStatus);
        Assert.True(segment.HasWarnings);

        // The file's match brings the current codes along, in place of its own.
        vm.InsertMatchCommand.Execute(vm.Matches.Single(m => m.Score == "90%").Number);
        Assert.Equal("Most nyomd meg: ⟨1⟩OK⟨/1⟩.", segment.TargetText);
        Assert.False(segment.HasIssues);
    }

    [Fact]
    public async Task InsertingAMatchThatIsntThere_SaysSo()
    {
        var vm = await OpenAsync(WithMatches);
        vm.SelectedSegment = vm.Segments[0];

        vm.InsertMatchCommand.Execute(2);
        Assert.Equal("There is no match 2.", vm.Segments[0].EditStatus);

        await vm.LoadAsync(_files.Write("plain.xlf", TestFiles.Sample));
        vm.SelectedSegment = vm.Segments[0];
        vm.InsertMatchCommand.Execute(1);
        Assert.Equal("There are no matches to insert.", vm.Segments[0].EditStatus);
        Assert.Equal("", vm.Segments[0].TargetText);
    }

    // A lookup still running when the translator moves on is dropped: the pane never shows another segment's matches.
    [Fact]
    public async Task MovingOn_DropsTheLookupForThePreviousSegment()
    {
        var vm = await OpenAsync(TestFiles.Sample);
        using var gate = new ManualResetEventSlim();
        var tm = new FakeMemory(source =>
        {
            if (source.ToPlainText() == "Hello world.")
                gate.Wait(TimeSpan.FromSeconds(10));
            return [new TmMatch(new TmEntry(source, Text(source.ToPlainText() + " (TM)")), 100)];
        });
        vm.TranslationMemory = new OpenTranslationMemory(tm, "Slow");

        vm.SelectedSegment = vm.Segments[0];
        Assert.True(vm.IsLookingUp);
        Assert.Equal("Looking up matches…", vm.MatchesStatus);
        var first = vm.MatchLookup;

        vm.SelectedSegment = vm.Segments[1];
        gate.Set();
        await first;
        await vm.MatchLookup;

        Assert.Equal("Goodbye. (TM)", Assert.Single(vm.Matches).Target.Target.ToPlainText());
        Assert.False(vm.IsLookingUp);
    }

    [Fact]
    public async Task ADamagedTm_ShowsTheErrorAndKeepsTheFilesMatches()
    {
        var vm = await OpenAsync(WithMatches);
        vm.TranslationMemory = new OpenTranslationMemory(new FakeMemory(_ => throw new TmFormatException("Entry 7 is damaged.")), "Broken");

        vm.SelectedSegment = vm.Segments[0];
        await vm.MatchLookup;

        Assert.Equal("Could not look up matches in Broken: Entry 7 is damaged.", vm.ErrorMessage);
        Assert.Equal("Szia ott.", Assert.Single(vm.Matches).Target.Target.ToPlainText());
        Assert.False(vm.IsLookingUp);
    }

    private async Task<MainWindowViewModel> OpenAsync(string content)
    {
        var vm = new MainWindowViewModel(new Xliff20Serializer(), new FakeDialogs(null));
        await vm.LoadAsync(_files.Write("doc.xlf", content));
        return vm;
    }

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    private static InlineContent Content(params object[] parts) =>
        new([.. parts.Select(p => p is string s ? new InlineText(s) : (InlineElement)p)]);

    private static InlineCode Open(string id) => new(id, InlineCodeKind.Opening);

    private static InlineCode Close(string id) => new(id, InlineCodeKind.Closing);

    /// <summary>A TM that answers with a function and records what it was asked (from any thread).</summary>
    private sealed class FakeMemory(Func<InlineContent, IReadOnlyList<TmMatch>> lookup) : ITranslationMemory
    {
        private readonly List<(string, string?, string?)> _lookups = [];

        public IReadOnlyList<(string Source, string? Previous, string? Next)> Lookups
        {
            get
            {
                lock (_lookups)
                    return [.. _lookups];
            }
        }

        public int Count => 0;

        public void Add(TmEntry entry) => throw new NotSupportedException();

        public IReadOnlyList<TmMatch> Lookup(
            InlineContent source, string? previousSource = null, string? nextSource = null,
            int threshold = Ranking.DefaultThreshold, int maxMatches = Ranking.DefaultMaxMatches)
        {
            lock (_lookups)
                _lookups.Add((source.ToPlainText(), previousSource, nextSource));
            return lookup(source);
        }
    }
}
