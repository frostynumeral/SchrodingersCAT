using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

public sealed class ConcordanceTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("schrodingerscat-concordance-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("fájl", "Töröld a fájlt.", "fájlt")] // a word beginning: Hungarian endings
    [InlineData("save file", "Saves files now.", "Saves files")] // every word a beginning, in order
    [InlineData("FAJL", "A Fájlok listája.", "Fájlok")] // case and accents
    [InlineData("lodz", "Łódź is a city.", "Łódź")] // a letter accents don't explain
    [InlineData("strasse", "Hauptstraße 1", null)] // ß → ss, but only at a word's beginning
    [InlineData("haupt", "Hauptstraße 1", "Hauptstraße")]
    [InlineData("dont", "Don’t save.", null)] // punctuation splits words: don ’ t
    [InlineData("don t", "Don’t save.", "Don’t")] // and is skipped between words, whatever the quote
    [InlineData("file save", "Save the file.", null)] // order matters
    [InlineData("save file", "Save the file.", null)] // and adjacency
    public void Words_MatchByTheirBeginnings(string query, string text, string? lit) =>
        Assert.Equal(lit is null ? [] : [lit], Lit(Search(Memory(text), query).Hits.SingleOrDefault()?.Source));

    [Fact]
    public void AHit_CanSpanCodes()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Content("Press ", Open("1"), "OK", Close("1"), " now."), Text("Nyomd meg.")));

        var hit = Assert.Single(Search(tm, "press ok").Hits);

        Assert.Equal(["Press", "OK"], Lit(hit.Source));
        Assert.Equal(tm.Entries().Single().Source, new InlineContent(hit.Source.Select(p => p.Element)));
    }

    [Fact]
    public void EveryOccurrence_IsLit()
    {
        var hit = Assert.Single(Search(Memory("Copy the file, then copy the files again."), "copy the").Hits);

        Assert.Equal(["Copy the", "copy the"], Lit(hit.Source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    [InlineData("́")] // a lone combining mark folds to nothing
    public void AQueryWithoutWords_IsEmpty_AndFindsNothing(string query)
    {
        Assert.True(ConcordanceQuery.Parse(query).IsEmpty);
        Assert.Equal((0, 0), Count(Search(Memory("Anything at all."), query)));
    }

    [Fact]
    public void TextNormalizeRejects_IsSearchedAsItIs()
    {
        var tm = Memory("Bad \uD800 text ￿ here.");

        Assert.Equal(["text"], Lit(Assert.Single(Search(tm, "text").Hits).Source));
        Assert.Equal(["here"], Lit(Assert.Single(Search(tm, "here").Hits).Source));
    }

    [Fact]
    public void Targets_CanBeSearched_TheSourceIsThenPlain()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Text("Delete the file."), Text("Töröld a fájlt.")));

        var hit = Assert.Single(Concordance.Search(tm, ConcordanceQuery.Parse("fájl"), ConcordanceSide.Target).Hits);

        Assert.Equal(["fájlt"], Lit(hit.Target));
        Assert.Empty(Lit(hit.Source));
        Assert.Empty(Search(tm, "fájl").Hits); // the source has no fájl
    }

    // Whole words first, then shorter, then newer, then the order stored.
    [Fact]
    public void Hits_AreRanked()
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(Entry("The files are here.", Monday.AddDays(5))); // only a beginning
        tm.Add(Entry("A file is a file.", Monday)); // whole, longer
        tm.Add(Entry("The file.", Monday)); // whole, short, old
        tm.Add(Entry("One file.", Monday.AddDays(1))); // whole, short, newer

        Assert.Equal(
            ["One file.", "The file.", "A file is a file.", "The files are here."],
            Search(tm, "file").Hits.Select(h => h.Entry.Source.ToPlainText()));
    }

    // An import can bring many equal entries; ties never make the best-N list drop any.
    [Fact]
    public void EqualEntries_AreAllKept_UpToTheMaximum_AndTheTotalCountsAll()
    {
        var tm = new InMemoryTranslationMemory();
        for (var i = 0; i < 30; i++)
            tm.Add(new TmEntry(Text("Save the file."), Text($"Mentés {i:00}.")) { Changed = Monday });

        var result = Concordance.Search(tm, ConcordanceQuery.Parse("file"), ConcordanceSide.Source, maxHits: 25);

        Assert.Equal(30, result.Total);
        Assert.Equal(Enumerable.Range(0, 25).Select(i => $"Mentés {i:00}."), result.Hits.Select(h => h.Entry.Target.ToPlainText()));
    }

    [Fact]
    public void ACancelledSearch_Throws()
    {
        var tm = new InMemoryTranslationMemory();
        for (var i = 0; i < Concordance.CheckEvery * 2; i++)
            tm.Add(new TmEntry(Text($"Entry {i}."), Text($"Bejegyzés {i}.")));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Concordance.Search(tm, ConcordanceQuery.Parse("entry"), ConcordanceSide.Source, cancellationToken: cancel.Token));
    }

    [Fact]
    public void AnEmptyTm_HasNoHits() =>
        Assert.Equal((0, 0), Count(Search(new InMemoryTranslationMemory(), "file")));

    // The SQLite TM reads only the searched column, then loads the hits: the same result as reading every entry.
    [Fact]
    public void TheSqliteTm_FindsWhatTheInMemoryTmFinds()
    {
        var memory = new InMemoryTranslationMemory();
        using var sqlite = SqliteTranslationMemory.Create(Path.Combine(_directory, "a.sctm"), "en", "hu");
        var entries = new[]
        {
            new TmEntry(Content("Save ", Open("1"), "the file", Close("1"), "."), Text("Mentsd a fájlt.")) { Changed = Monday, Document = "a.xlf", ChangedBy = "me" },
            Entry("Save all files.", Monday.AddDays(1)),
            Entry("Delete the file.", Monday.AddDays(2)),
        };
        memory.AddRange(entries);
        sqlite.AddRange(entries);

        foreach (var (query, side) in new[] { ("file", ConcordanceSide.Source), ("save", ConcordanceSide.Source), ("fájl", ConcordanceSide.Target) })
        {
            var expected = Concordance.Search(memory, ConcordanceQuery.Parse(query), side);
            var actual = Concordance.Search(sqlite, ConcordanceQuery.Parse(query), side);
            Assert.Equal(expected.Total, actual.Total);
            Assert.Equal(expected.Hits.Select(h => (h.Entry, Lit(h.Source), Lit(h.Target))), actual.Hits.Select(h => (h.Entry, Lit(h.Source), Lit(h.Target))));
        }
    }

    private static ConcordanceResult Search(ITranslationMemory tm, string query) =>
        Concordance.Search(tm, ConcordanceQuery.Parse(query), ConcordanceSide.Source);

    private static (int Hits, int Total) Count(ConcordanceResult result) => (result.Hits.Count, result.Total);

    private static InMemoryTranslationMemory Memory(string source)
    {
        var tm = new InMemoryTranslationMemory();
        tm.Add(new TmEntry(Text(source), Text("x")));
        return tm;
    }

    private static List<string> Lit(IReadOnlyList<HighlightPart>? parts) =>
        [.. (parts ?? []).Where(p => p.IsHit).Select(p => ((InlineText)p.Element).Text)];

    private static TmEntry Entry(string source, DateTimeOffset changed) => new(Text(source), Text("x")) { Changed = changed };

    private static InlineContent Text(string text) => InlineContent.FromText(text);

    private static InlineContent Content(params object[] parts) =>
        new([.. parts.Select(p => p is string s ? new InlineText(s) : (InlineElement)p)]);

    private static InlineCode Open(string id) => new(id, InlineCodeKind.Opening);

    private static InlineCode Close(string id) => new(id, InlineCodeKind.Closing);
}
