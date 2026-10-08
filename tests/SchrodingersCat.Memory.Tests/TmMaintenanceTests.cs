using Microsoft.Data.Sqlite;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

/// <summary>The maintenance operations (<c>docs/design/tm-maintenance.md</c>), the same for both TMs.</summary>
public abstract class TmMaintenanceTests : IDisposable
{
    protected static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    protected abstract ITranslationMemory Memory { get; }

    public virtual void Dispose()
    {
        (Memory as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MatchesAndConcordanceHits_CarryTheirEntrysId()
    {
        Memory.AddRange([Entry("Save the file.", "Mentsd a fájlt."), Entry("Open the file.", "Nyisd meg a fájlt.")]);

        var match = Memory.Lookup(Text("Open the file."))[0];
        var hit = Concordance.Search(Memory, ConcordanceQuery.Parse("save"), ConcordanceSide.Source).Hits.Single();

        Assert.Equal(match.Entry, Memory.Find(match.EntryId!.Value)!.Entry);
        Assert.Equal(hit.Entry, Memory.Find(hit.EntryId!.Value)!.Entry);
        Assert.NotEqual(match.EntryId, hit.EntryId);
    }

    [Fact]
    public void Update_GivesANewTranslation_AndKeepsTheSourceDocumentAndContext()
    {
        Memory.Add(Entry("Save the file.", "Mentsd a fájt.") with { Document = "a.xlf", PreviousSource = "Before.", ChangedBy = "anna" });
        var id = IdOf("Save the file.");

        Assert.Equal(UpdateResult.Updated, Memory.Update(id, Text("Mentsd a fájlt."), Monday.AddDays(1), "béla"));

        var expected = Entry("Save the file.", "Mentsd a fájlt.") with
        {
            Document = "a.xlf",
            PreviousSource = "Before.",
            Changed = Monday.AddDays(1),
            ChangedBy = "béla",
        };
        Assert.Equal(new StoredEntry(id, expected), Memory.Find(id));
        Assert.Equal([expected], Memory.Lookup(Text("Save the file.")).Select(m => m.Entry));
        Assert.Equal(1, Memory.Count);
    }

    [Fact]
    public void Update_ToAnotherStoredTranslation_MergesThem_TheEditedEntryStays()
    {
        Memory.AddRange([Entry("Save", "Mentés"), Entry("Save", "Mentsd"), Entry("Open", "Megnyitás")]);
        var edited = Memory.Lookup(Text("Save")).Single(m => m.Entry.Target.ToPlainText() == "Mentsd").EntryId!.Value;

        Assert.Equal(UpdateResult.Merged, Memory.Update(edited, Text("Mentés"), Monday.AddDays(1), null));

        var match = Assert.Single(Memory.Lookup(Text("Save")));
        Assert.Equal((edited, "Mentés", Monday.AddDays(1)), (match.EntryId!.Value, match.Entry.Target.ToPlainText(), match.Entry.Changed));
        Assert.Equal(2, Memory.Count);
    }

    [Fact]
    public void Update_ThatChangesOnlyCodeIds_IsNoMerge()
    {
        Memory.Add(new TmEntry(Text("Bold"), new InlineContent([Open("1"), new InlineText("Félkövér"), Close("1")])) { Changed = Monday });
        var id = IdOf("Bold");

        Assert.Equal(UpdateResult.Updated, Memory.Update(id, new InlineContent([Open("2"), new InlineText("Félkövér"), Close("2")]), Monday, null));
        Assert.Equal("2", Assert.IsType<InlineCode>(Memory.Find(id)!.Entry.Target[0]).Id);

        // Still one pair: storing it again replaces it.
        Memory.Add(new TmEntry(Text("Bold"), new InlineContent([Open("3"), new InlineText("Félkövér"), Close("3")])) { Changed = Monday });
        Assert.Equal(1, Memory.Count);
    }

    [Fact]
    public void StaleIds_AreReported_NotThrown()
    {
        Memory.AddRange([Entry("One", "Egy"), Entry("Two", "Kettő")]);
        var id = IdOf("One");
        Assert.Equal(1, Memory.Delete([id]));

        Assert.Null(Memory.Find(id));
        Assert.Equal(UpdateResult.NotFound, Memory.Update(id, Text("Egy"), Monday, null));
        Assert.Equal(0, Memory.Delete([id]));
        Assert.Equal(1, Memory.Count);
    }

    [Fact]
    public void Ids_AreNeverReused_EvenAfterTheNewestIsDeleted()
    {
        Memory.AddRange([Entry("One", "Egy"), Entry("Two", "Kettő")]);
        var two = IdOf("Two");
        Memory.Delete([two]);
        Memory.Remove(Entry("One", "Egy"));

        Memory.Add(Entry("Three", "Három"));

        Assert.True(IdOf("Three").Value > two.Value);
    }

    [Fact]
    public void Delete_TakesOutTheEntriesAndOnlyThose()
    {
        Memory.AddRange([Entry("Save the file.", "Mentsd a fájlt."), Entry("Save the file now.", "Mentsd most."), Entry("Delete it.", "Töröld.")]);

        Assert.Equal(1, Memory.Delete([IdOf("Save the file."), IdOf("Save the file.")]));

        Assert.Equal(["Mentsd most."], Memory.Lookup(Text("Save the file.")).Select(m => m.Entry.Target.ToPlainText()));
        Assert.Equal(2, Memory.Count);
    }

    [Fact]
    public void Browse_PagesNewestFirst_AndDeletingBetweenPagesSkipsNothing()
    {
        Memory.AddRange([.. Enumerable.Range(1, 7).Select(i => Entry($"Source {i}", $"Forrás {i}"))]);

        var first = Memory.Browse(EntryFilter.All, pageSize: 3);
        Assert.Equal(7, first.Total);
        Assert.Equal(["Source 7", "Source 6", "Source 5"], Sources(first));

        Memory.Delete([first.Entries[0].Id, IdOf("Source 3")]);
        var second = Memory.Browse(EntryFilter.All, first.Entries[^1].Id, 3);

        Assert.Equal(5, second.Total);
        Assert.Equal(["Source 4", "Source 2", "Source 1"], Sources(second));
        Assert.Empty(Memory.Browse(EntryFilter.All, second.Entries[^1].Id, 3).Entries);
    }

    [Fact]
    public void Browse_FiltersByEveryCondition()
    {
        Memory.AddRange([
            Entry("Save the file.", "Mentsd a fájlt.") with { Document = "a.xlf", ChangedBy = "anna" },
            Entry("Save the file.", "Fájl mentése.") with { Document = "b.xlf", ChangedBy = "béla", Changed = Monday.AddDays(2) },
            Entry("Open the File.", "Nyisd meg a fájlt.") with { Document = "a.xlf", ChangedBy = "béla", Changed = Monday.AddDays(1) },
            Entry("Close it.", "Zárd be.") with { ChangedBy = "anna", Changed = Monday.AddDays(3) },
        ]);

        // Newest first means the order stored, backwards, whatever the dates say.
        Assert.Equal(["Close it.", "Open the File.", "Fájl mentése.", "Mentsd a fájlt."], Targets(EntryFilter.All));
        Assert.Equal(["Open the File.", "Mentsd a fájlt."], Targets(new EntryFilter { Document = "a.xlf" }));
        Assert.Equal(["Open the File.", "Fájl mentése."], Targets(new EntryFilter { User = "béla" }));
        Assert.Equal(["Open the File.", "Fájl mentése."], Targets(new EntryFilter { ChangedFrom = Monday.AddDays(1), ChangedBefore = Monday.AddDays(3) }));
        Assert.Equal(["Fájl mentése.", "Mentsd a fájlt."], Targets(new EntryFilter { SeveralTranslations = true }));
        Assert.Equal(["Open the File.", "Mentsd a fájlt."], Targets(new EntryFilter { Text = ConcordanceQuery.Parse("fájlt"), TextSide = ConcordanceSide.Target }));
        Assert.Equal(["Open the File.", "Fájl mentése.", "Mentsd a fájlt."], Targets(new EntryFilter { Text = ConcordanceQuery.Parse("the fil") }));
        Assert.Equal(["Mentsd a fájlt."], Targets(new EntryFilter { Text = ConcordanceQuery.Parse("file"), User = "anna", SeveralTranslations = true }));
        Assert.Equal(4, Sources(new EntryFilter { Text = ConcordanceQuery.Parse("!") }).Count); // no words: no text condition
    }

    [Fact]
    public void DeleteMatching_RemovesWhatTheFilterFinds()
    {
        Memory.AddRange([
            Entry("Save the file.", "Mentsd a fájlt.") with { Document = "old.xlf" },
            Entry("Open the file.", "Nyisd meg a fájlt.") with { Document = "new.xlf" },
            Entry("Close the file.", "Zárd be a fájlt.") with { Document = "old.xlf" },
        ]);

        Assert.Equal(2, Memory.DeleteMatching(new EntryFilter { Document = "old.xlf" }));

        Assert.Equal(["Open the file."], Sources(EntryFilter.All));
        Assert.Equal(["Nyisd meg a fájlt."], Memory.Lookup(Text("Close the file.")).Select(m => m.Entry.Target.ToPlainText()));
        Assert.Equal(1, Memory.DeleteMatching(EntryFilter.All));
        Assert.Equal(0, Memory.Count);
    }

    [Fact]
    public void DocumentsAndUsers_AreDistinctAndSorted()
    {
        Memory.AddRange([
            Entry("One", "Egy") with { Document = "b.xlf", ChangedBy = "béla" },
            Entry("Two", "Kettő") with { Document = "A.xlf", ChangedBy = "anna" },
            Entry("Three", "Három") with { Document = "b.xlf" },
            Entry("Four", "Négy") with { ChangedBy = "Anna" },
        ]);

        Assert.Equal(["A.xlf", "b.xlf"], Memory.Documents());
        Assert.Equal(["Anna", "anna", "béla"], Memory.Users());
    }

    protected TmEntryId IdOf(string source) =>
        Memory.Browse(EntryFilter.All, pageSize: int.MaxValue).Entries.Single(e => e.Entry.Source.ToPlainText() == source).Id;

    // The source, or for "Save the file." (stored twice) the translation, so the order shows which entry is which.
    private List<string> Targets(EntryFilter filter) =>
        [.. Memory.Browse(filter, pageSize: int.MaxValue).Entries.Select(e =>
            e.Entry.Source.ToPlainText() is "Save the file." ? e.Entry.Target.ToPlainText() : e.Entry.Source.ToPlainText())];

    private List<string> Sources(EntryFilter filter) => Sources(Memory.Browse(filter, pageSize: int.MaxValue));

    private static List<string> Sources(BrowseResult result) => [.. result.Entries.Select(e => e.Entry.Source.ToPlainText())];

    protected static TmEntry Entry(string source, string target) => new(Text(source), Text(target)) { Changed = Monday };

    protected static InlineContent Text(string text) => InlineContent.FromText(text);

    private static InlineCode Open(string id) => new(id, InlineCodeKind.Opening);

    private static InlineCode Close(string id) => new(id, InlineCodeKind.Closing);
}

public sealed class InMemoryTmMaintenanceTests : TmMaintenanceTests
{
    protected override ITranslationMemory Memory { get; } = new InMemoryTranslationMemory();

    [Fact]
    public void Compact_HasNothingToDo() => Assert.False(Memory.Compact());
}

public sealed class SqliteTmMaintenanceTests : TmMaintenanceTests
{
    private readonly string _directory = Directory.CreateTempSubdirectory("schrodingerscat-tm-").FullName;
    private readonly SqliteTranslationMemory _memory;

    public SqliteTmMaintenanceTests() => _memory = SqliteTranslationMemory.Create(PathOf("a.sctm"), "en", "hu");

    protected override ITranslationMemory Memory => _memory;

    public override void Dispose()
    {
        base.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Ids_AreNeverReused_AfterReopening()
    {
        Memory.AddRange([Entry("One", "Egy"), Entry("Two", "Kettő")]);
        var two = IdOf("Two");
        Memory.Delete([two]);
        _memory.Dispose();

        using var reopened = SqliteTranslationMemory.Open(PathOf("a.sctm"));
        reopened.Add(Entry("Three", "Három"));

        Assert.True(reopened.Browse(EntryFilter.All).Entries[0].Id.Value > two.Value);
    }

    // Deleting 2% of the TM or more takes the bulk path: the index is then what storing the rest would build.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(30)]
    public void Delete_OneByOneOrInBulk_LeavesTheIndexOfWhatsLeft(int deleted)
    {
        Memory.AddRange([.. Enumerable.Range(0, 300).Select(i => Entry($"Sentence {i} number {i % 7} with words {i % 3}.", $"Mondat {i}."))]);
        var ids = Memory.Browse(EntryFilter.All, pageSize: int.MaxValue).Entries.Select(e => e.Id).Where((_, i) => i % 4 != 1).Take(deleted).ToList();

        Assert.Equal(deleted, Memory.Delete(ids));

        var (postings, features) = Index(Memory.Browse(EntryFilter.All, pageSize: int.MaxValue).Entries);
        Assert.Equal(postings, Postings());
        Assert.Equal(features, Features());
        Assert.Equal(300 - deleted, Memory.Count);
    }

    [Fact]
    public void Compact_ShrinksTheFileOnlyWhenWorthIt_AndKeepsIds()
    {
        Memory.AddRange([.. Enumerable.Range(0, 2_000).Select(i => Entry($"A rather long sentence number {i}, to fill some pages.", $"Mondat {i}."))]);
        var ids = Memory.Browse(EntryFilter.All, pageSize: int.MaxValue).Entries.Select(e => e.Id).ToList();

        // Scattered deletes, so pages are left half empty rather than free: a fifth isn't worth it, half is.
        Memory.Delete([.. ids.Where((_, i) => i % 5 == 0)]);
        Assert.False(Memory.Compact());
        Memory.Delete([.. ids.Where((_, i) => i % 5 is 1 or 2 or 3)]);
        var size = new FileInfo(PathOf("a.sctm")).Length;
        var kept = Memory.Browse(EntryFilter.All, pageSize: int.MaxValue).Entries;

        Assert.True(Memory.Compact());
        Assert.False(Memory.Compact()); // counted again from here
        _memory.Dispose();

        Assert.True(new FileInfo(PathOf("a.sctm")).Length < size / 3);
        using var reopened = SqliteTranslationMemory.Open(PathOf("a.sctm"));
        Assert.Equal(kept, reopened.Browse(EntryFilter.All, pageSize: int.MaxValue).Entries);
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    // The postings and bigram counts storing these entries would give.
    private static (HashSet<(long, long)>, Dictionary<long, long>) Index(IEnumerable<StoredEntry> entries)
    {
        var postings = entries
            .SelectMany(e => SqliteTranslationMemory.Features(MatchScore.Normalized.Of(e.Entry.Source)).Distinct().Select(f => (f, e.Id.Value)))
            .ToHashSet();
        return (postings, postings.GroupBy(p => p.Item1).ToDictionary(g => g.Key, g => (long)g.Count()));
    }

    private HashSet<(long, long)> Postings() => [.. Rows("SELECT feature, entry FROM postings")];

    private Dictionary<long, long> Features() => Rows("SELECT feature, entries FROM features").ToDictionary(r => r.Item1, r => r.Item2);

    private List<(long, long)> Rows(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathOf("a.sctm"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<(long, long)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return rows;
    }
}
