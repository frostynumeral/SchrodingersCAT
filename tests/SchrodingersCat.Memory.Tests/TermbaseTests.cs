using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

/// <summary>What a termbase does (step 3 of <c>docs/design/termbase.md</c>), the same for both termbases.</summary>
public abstract class TermbaseTests : IDisposable
{
    protected static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    protected abstract ITermbase Termbase { get; }

    public virtual void Dispose()
    {
        Termbase.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Add_GivesIncreasingIds_AndTheIndexFindsTheTerm()
    {
        var file = Termbase.Add(Entry("file", "fájl"));
        var folder = Termbase.Add(Entry("folder", "mappa"));

        Assert.True(folder.Value > file.Value);
        Assert.Equal(folder, Assert.Single(Termbase.Index.Find(Text("Open the folder."))).Id);
        Assert.Equal(Entry("file", "fájl"), Termbase.Index.Find(file));
    }

    [Fact]
    public void AddRange_GivesIdsInTheirOrder_InOneSnapshot()
    {
        var first = Termbase.Add(Entry("first"));
        var before = Termbase.Index;

        var ids = Termbase.AddRange([Entry("file"), Entry("folder"), Entry("file")]);

        Assert.Equal([first.Value + 1, first.Value + 2, first.Value + 3], ids.Select(id => id.Value));
        Assert.Equal(1, before.Count);
        Assert.Equal(4, Termbase.Index.Count);
        Assert.Equal([ids[0], ids[2]], Termbase.Index.Find(Text("the file")).Select(h => h.Id));
        Assert.Empty(Termbase.AddRange([]));
    }

    [Fact]
    public void AddRange_WithANullEntry_ChangesNothing()
    {
        var first = Termbase.Add(Entry("first"));
        var before = Termbase.Index;

        Assert.Throws<ArgumentNullException>(() => Termbase.AddRange([Entry("file"), null!]));

        Assert.Same(before, Termbase.Index);
        Assert.Equal(first.Value + 1, Termbase.Add(Entry("file")).Value);
    }

    [Fact]
    public void Update_ReplacesTheWholeEntry_AndKeepsItsId()
    {
        var id = Termbase.Add(new TermEntry([new Term("file")], [new Term("fájl")], "noun", Monday, "anna"));
        var updated = new TermEntry([new Term("folder"), new Term("directory", TermStatus.Admitted)], [], null, Monday.AddDays(1), null);

        Assert.True(Termbase.Update(id, updated));

        Assert.Equal(updated, Termbase.Index.Find(id));
        Assert.Empty(Termbase.Index.Find(Text("the file")));
        Assert.Equal(id, Assert.Single(Termbase.Index.Find(Text("the directory"))).Id);
        Assert.Equal(1, Termbase.Index.Count);
    }

    [Fact]
    public void Update_AStaleId_IsFalse_AndChangesNothing()
    {
        var id = Termbase.Add(Entry("file"));
        Termbase.Delete([id]);
        var before = Termbase.Index;

        Assert.False(Termbase.Update(id, Entry("folder")));
        Assert.False(Termbase.Update(new TermEntryId(id.Value + 10), Entry("folder")));

        Assert.Same(before, Termbase.Index);
        Assert.Equal(0, Termbase.Index.Count);
    }

    [Fact]
    public void Delete_SkipsUnknownAndRepeatedIds_AndSaysHowMany()
    {
        var ids = Termbase.AddRange([Entry("file"), Entry("folder"), Entry("manager")]);
        var before = Termbase.Index;

        Assert.Equal(2, Termbase.Delete([ids[0], ids[0], ids[2], new TermEntryId(99)]));
        Assert.Equal(0, Termbase.Delete([ids[0]]));
        Assert.Equal(0, Termbase.Delete([]));

        Assert.Equal([ids[1]], Termbase.Index.Entries().Select(e => e.Id));
        Assert.Equal(3, before.Count);
    }

    // Entries are concepts: two equal ones are homonyms, kept apart.
    [Fact]
    public void EqualEntries_AreKeptApart()
    {
        var ids = Termbase.AddRange([Entry("file", "fájl"), Entry("file", "fájl")]);

        Assert.Equal(ids, Termbase.Index.Find(Text("the file")).Select(h => h.Id));
    }

    [Fact]
    public void EachWrite_GivesANewSnapshot_TheOldOneStaysAsItWas()
    {
        var id = Termbase.Add(Entry("file"));
        var afterAdd = Termbase.Index;
        Termbase.Update(id, Entry("folder"));
        var afterUpdate = Termbase.Index;
        Termbase.Delete([id]);

        Assert.Equal(Entry("file"), afterAdd.Find(id));
        Assert.Equal(Entry("folder"), afterUpdate.Find(id));
        Assert.Equal(0, Termbase.Index.Count);
    }

    [Fact]
    public void TheHighestIdDeleted_IsntGivenAgain()
    {
        var ids = Termbase.AddRange([Entry("one"), Entry("two")]);
        Termbase.Delete(ids);

        Assert.Equal(ids[1].Value + 1, Termbase.Add(Entry("three")).Value);
    }

    [Fact]
    public void Changed_IsStoredAsUtc()
    {
        var local = new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.FromHours(2));
        var id = Termbase.Add(Entry("file") with { Changed = local });
        Termbase.Update(Termbase.Add(Entry("folder")), Entry("folder") with { Changed = local });

        Assert.All(Termbase.Index.Entries(), e => Assert.Equal((TimeSpan.Zero, local.UtcTicks), (e.Entry.Changed.Offset, e.Entry.Changed.UtcTicks)));
        Assert.Equal(TimeSpan.Zero, Termbase.Index.Find(id)!.Changed.Offset);
    }

    [Fact]
    public void Writes_RefuseNulls()
    {
        Assert.Throws<ArgumentNullException>(() => Termbase.Add(null!));
        Assert.Throws<ArgumentNullException>(() => Termbase.AddRange(null!));
        Assert.Throws<ArgumentNullException>(() => Termbase.Update(new TermEntryId(1), null!));
        Assert.Throws<ArgumentNullException>(() => Termbase.Delete(null!));
    }

    protected static TermEntry Entry(string term, params string[] translations) =>
        new([new Term(term)], [.. translations.Select(t => new Term(t))], null, Monday, null);

    protected static QaText Text(string text) => QaText.Of(InlineContent.FromText(text));
}

public sealed class InMemoryTermbaseTests : TermbaseTests
{
    protected override ITermbase Termbase { get; } = new InMemoryTermbase("en", "hu");

    [Fact]
    public void Compact_HasNothingToDo() => Assert.False(Termbase.Compact());
}

public sealed class SqliteTermbaseMaintenanceTests : TermbaseTests
{
    private readonly string _directory = Directory.CreateTempSubdirectory("schrodingerscat-tb-").FullName;
    private readonly SqliteTermbase _termbase;

    public SqliteTermbaseMaintenanceTests() => _termbase = SqliteTermbase.Create(PathOf("a.sctb"), "en", "hu");

    protected override ITermbase Termbase => _termbase;

    public override void Dispose()
    {
        base.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Ids_AreNeverReused_AfterReopening()
    {
        var ids = Termbase.AddRange([Entry("one"), Entry("two")]);
        Termbase.Delete([ids[1]]);
        _termbase.Dispose();

        using (var reopened = SqliteTermbase.Open(PathOf("a.sctb")))
        {
            Assert.Equal(ids[1].Value + 1, reopened.Add(Entry("three")).Value);
            Assert.Equal(2, reopened.Delete([.. reopened.Index.Entries().Select(e => e.Id)]));
        }

        using var empty = SqliteTermbase.Open(PathOf("a.sctb"));
        Assert.Equal(ids[1].Value + 2, empty.Add(Entry("four")).Value);
    }

    [Fact]
    public void Compact_ShrinksTheFileOnlyWhenWorthIt_AndKeepsIds()
    {
        var ids = Termbase.AddRange([.. Enumerable.Range(0, 2_000).Select(i => Entry($"a rather long term number {i}", $"egy elég hosszú szakkifejezés, a {i}."))]);

        // Scattered deletes, so pages are left half empty rather than free: a fifth isn't worth it, four fifths are.
        Termbase.Delete([.. ids.Where((_, i) => i % 5 == 0)]);
        Assert.False(Termbase.Compact());
        Termbase.Delete([.. ids.Where((_, i) => i % 5 is 1 or 2 or 3)]);
        var size = new FileInfo(PathOf("a.sctb")).Length;
        var kept = Termbase.Index.Entries().ToList();

        Assert.True(Termbase.Compact());
        Assert.False(Termbase.Compact()); // counted again from here
        _termbase.Dispose();

        Assert.True(new FileInfo(PathOf("a.sctb")).Length < size / 2);
        using var reopened = SqliteTermbase.Open(PathOf("a.sctb"));
        Assert.Equal(kept, reopened.Index.Entries());
        Assert.Equal(ids[^1].Value + 1, reopened.Add(Entry("next")).Value);
    }

    // A write SQLite refuses (here a term row in the way of the next entry's) leaves the file and the snapshot as they
    // were, and the next write goes ahead with the id the refused one would have had.
    [Fact]
    public void AFailedWrite_LeavesTheFileAndTheIndexAsTheyWere()
    {
        var first = Termbase.Add(Entry("file"));
        var before = Termbase.Index;
        SqliteTermbaseTests.Sql(PathOf("a.sctb"), $"INSERT INTO terms VALUES({first.Value + 1}, 0, 0, x'', 0, NULL)");

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Termbase.AddRange([Entry("folder"), Entry("manager")]));

        Assert.Same(before, Termbase.Index);
        Assert.Equal([1L], SqliteTermbaseTests.Rows(PathOf("a.sctb"), "SELECT count(*) FROM entries").Select(r => (long)r[0]!));
        SqliteTermbaseTests.Sql(PathOf("a.sctb"), "DELETE FROM terms WHERE entry <> 1");
        Assert.Equal(first.Value + 1, Termbase.Add(Entry("folder")).Value);
    }

    private string PathOf(string name) => Path.Combine(_directory, name);
}
