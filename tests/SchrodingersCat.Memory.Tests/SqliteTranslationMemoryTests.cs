using CsCheck;
using Microsoft.Data.Sqlite;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;
using SchrodingersCat.TestSupport;

namespace SchrodingersCat.Memory.Tests;

public sealed class SqliteTranslationMemoryTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(2));

    private readonly string _directory = Directory.CreateTempSubdirectory("schrodingerscat-tm-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void EntriesAndLanguages_SurviveReopening()
    {
        var path = PathOf("a.sctm");
        var entry = new TmEntry(
            new InlineContent([new InlineText("Press "), new InlineCode("1", InlineCodeKind.Standalone) { Disp = "{0}", DataRef = "d1" }, new InlineText("\uD800")]),
            InlineContent.FromText("Nyomd"))
        {
            PreviousSource = "Before.",
            NextSource = null,
            Document = "strings.xlf",
            Changed = Monday,
            ChangedBy = "translator",
        };
        using (var tm = SqliteTranslationMemory.Create(path, "en", "hu"))
            tm.Add(entry);

        using var reopened = SqliteTranslationMemory.Open(path);

        Assert.Equal(("en", "hu", 1), (reopened.SourceLanguage, reopened.TargetLanguage, reopened.Count));
        var match = Assert.Single(reopened.Lookup(entry.Source));
        Assert.Equal(entry, match.Entry);
        Assert.Equal(100, match.Score);
    }

    [Fact]
    public void AddingTheSamePairAgain_ReplacesIt_AnotherTranslationIsKept()
    {
        using var tm = SqliteTranslationMemory.Create(PathOf("a.sctm"), "en", "hu");
        tm.Add(Entry("Save", "Mentés", Monday));
        tm.Add(Entry("Save", "Mentés", Monday.AddDays(1)));
        tm.Add(Entry("Save", "Mentsd", Monday));

        Assert.Equal(2, tm.Count);
        Assert.Equal([("Mentés", Monday.AddDays(1)), ("Mentsd", Monday)], tm.Lookup(Text("Save")).Select(m => (m.Entry.Target.ToPlainText(), m.Entry.Changed)));
    }

    [Fact]
    public void FuzzyMatches_ComeThroughTheIndex()
    {
        using var tm = SqliteTranslationMemory.Create(PathOf("a.sctm"), "en", "hu");
        tm.AddRange([
            Entry("Save the file now.", "Mentsd most a fájlt.", Monday),
            Entry("Copy 3 files.", "3 fájl másolása.", Monday),
            Entry("Delete everything.", "Törölj mindent.", Monday),
        ]);

        Assert.Equal([80], tm.Lookup(Text("Save the file.")).Select(m => m.Score));
        Assert.Equal([99], tm.Lookup(Text("Copy 12 files.")).Select(m => m.Score)); // numbers are one word in the index too
        Assert.Empty(tm.Lookup(Text("Something else entirely.")));
    }

    // The index only narrows the search: on small TMs the results are those of scoring every entry.
    [Fact]
    public void Lookups_AreThoseOfTheInMemoryTm() =>
        XliffGen.Document.Sample(document =>
        {
            var path = PathOf($"{Guid.NewGuid():N}.sctm");
            var segments = document.Units.SelectMany(u => u.Segments).Where(s => s.Target is not null).ToList();
            var entries = segments.Select((s, i) => new TmEntry(s.Source, s.Target!) { Changed = Monday.AddMinutes(i) }).ToList();
            var memory = new InMemoryTranslationMemory();
            entries.ForEach(memory.Add);
            using (var sqlite = SqliteTranslationMemory.Create(path, "en", "hu"))
            {
                sqlite.AddRange(entries);
                Assert.Equal(memory.Count, sqlite.Count);
                foreach (var segment in document.Units.SelectMany(u => u.Segments))
                    Assert.Equal(memory.Lookup(segment.Source), sqlite.Lookup(segment.Source));

                // Removing every other entry keeps them equal, index included.
                foreach (var entry in entries.Where((_, i) => i % 2 == 0))
                    Assert.Equal(memory.Remove(entry), sqlite.Remove(entry));
                Assert.Equal(memory.Count, sqlite.Count);
                foreach (var segment in document.Units.SelectMany(u => u.Segments))
                    Assert.Equal(memory.Lookup(segment.Source), sqlite.Lookup(segment.Source));
            }

            File.Delete(path);
        });

    [Fact]
    public void Remove_TakesOutThePairAndItsIndexRows_OtherTranslationsStay()
    {
        var path = PathOf("a.sctm");
        using (var tm = SqliteTranslationMemory.Create(path, "en", "hu"))
        {
            tm.AddRange([Entry("Save the file.", "Mentsd a fájlt.", Monday), Entry("Save the file.", "Fájl mentése.", Monday), Entry("Delete it.", "Töröld.", Monday)]);

            Assert.True(tm.Remove(Entry("Save the file.", "Mentsd a fájlt.", Monday.AddDays(1)) with { NextSource = "Next." })); // the pair, whatever its date and context
            Assert.False(tm.Remove(Entry("Save the file.", "Mentsd a fájlt.", Monday)));

            Assert.Equal(["Fájl mentése."], tm.Lookup(Text("Save the file.")).Select(m => m.Entry.Target.ToPlainText()));
            Assert.True(tm.Remove(Entry("Save the file.", "Fájl mentése.", Monday)));
            Assert.Empty(tm.Lookup(Text("Save the file.")));
            Assert.Equal(1, tm.Count);
        }

        // Only "Delete it." is left: its bigrams, and nothing of the removed entries.
        Assert.Equal(0L, Scalar(path, "SELECT count(*) FROM postings p LEFT JOIN entries e ON e.id = p.entry WHERE e.id IS NULL"));
        Assert.Equal(0L, Scalar(path, "SELECT count(*) FROM features WHERE entries <> (SELECT count(*) FROM postings p WHERE p.feature = features.feature)"));
        Assert.Equal(4L, Scalar(path, "SELECT count(*) FROM features")); // ^delete, delete it, it ., .$
        using var reopened = SqliteTranslationMemory.Open(path);
        Assert.Equal(1, reopened.Count);
    }

    [Fact]
    public void Replace_RemovesThePreviousPairOnlyIfStoredFromTheSameDocument()
    {
        using var tm = SqliteTranslationMemory.Create(PathOf("a.sctm"), "en", "hu");
        tm.Add(Entry("Save", "Mentéss", Monday) with { Document = "a.xlf" });
        tm.Add(Entry("Open", "Nyiss", Monday) with { Document = "other.xlf" });

        tm.Replace(Entry("Save", "Mentéss", Monday) with { Document = "a.xlf" }, Entry("Save", "Mentés", Monday) with { Document = "a.xlf" });
        tm.Replace(Entry("Open", "Nyiss", Monday) with { Document = "a.xlf" }, Entry("Open", "Megnyitás", Monday) with { Document = "a.xlf" });

        Assert.Equal(["Nyiss", "Mentés", "Megnyitás"], tm.Entries().Select(e => e.Target.ToPlainText()));
        Assert.Equal(["Mentés"], tm.Lookup(Text("Save")).Select(m => m.Entry.Target.ToPlainText()));
    }

    [Fact]
    public void Entries_ListsEverythingInTheOrderStored()
    {
        using var tm = SqliteTranslationMemory.Create(PathOf("a.sctm"), "en", "hu");
        var entries = new[] { Entry("One", "Egy", Monday), Entry("Two", "Kettő", Monday), Entry("Three", "Három", Monday) };
        tm.AddRange(entries);

        Assert.Equal(entries, tm.Entries());
    }

    [Fact]
    public void Create_RefusesAnExistingFile()
    {
        var path = PathOf("a.sctm");
        File.WriteAllText(path, "keep me");

        Assert.Throws<IOException>(() => SqliteTranslationMemory.Create(path, "en", "hu"));
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public void Open_AMissingFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() => SqliteTranslationMemory.Open(PathOf("missing.sctm")));

    [Fact]
    public void Open_AFileThatIsntSqlite_IsRefused()
    {
        var path = PathOf("a.sctm");
        File.WriteAllText(path, "<xliff/>");

        Assert.Throws<TmFormatException>(() => SqliteTranslationMemory.Open(path));
    }

    [Fact]
    public void Open_AnotherSqliteDatabase_IsRefused()
    {
        var path = PathOf("other.db");
        Sql(path, "CREATE TABLE people(name TEXT)");

        Assert.Throws<TmFormatException>(() => SqliteTranslationMemory.Open(path));
    }

    // A trigger would run its own SQL on our writes.
    [Fact]
    public void Open_ATmWithATriggerOfItsOwn_IsRefused()
    {
        var path = PathOf("a.sctm");
        SqliteTranslationMemory.Create(path, "en", "hu").Dispose();
        Sql(path, "CREATE TRIGGER t AFTER INSERT ON entries BEGIN DELETE FROM meta; END");

        Assert.Throws<TmFormatException>(() => SqliteTranslationMemory.Open(path));
    }

    // In LIKE, _ matches any character: the schema check must not take this for one of SQLite's automatic indexes.
    [Fact]
    public void Open_ATriggerNamedLikeAnAutomaticIndex_IsRefused()
    {
        var path = PathOf("a.sctm");
        SqliteTranslationMemory.Create(path, "en", "hu").Dispose();
        Sql(path, "CREATE TRIGGER sqliteXautoindexXevil AFTER INSERT ON entries BEGIN DELETE FROM meta; END");

        Assert.Throws<TmFormatException>(() => SqliteTranslationMemory.Open(path));
    }

    [Fact]
    public void Open_ATermbase_IsRefused()
    {
        var path = PathOf("a.sctb");
        SqliteTermbase.Create(path, "en", "hu").Dispose();

        Assert.Throws<TmFormatException>(() => SqliteTranslationMemory.Open(path));
    }

    [Fact]
    public void Open_AnotherFormatVersion_IsRefused()
    {
        var path = PathOf("a.sctm");
        SqliteTranslationMemory.Create(path, "en", "hu").Dispose();
        Sql(path, "UPDATE meta SET value = '2' WHERE key = 'version'");

        var error = Assert.Throws<TmFormatException>(() => SqliteTranslationMemory.Open(path));
        Assert.Contains("another version (2)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADamagedEntry_FailsWithTmFormatException()
    {
        var path = PathOf("a.sctm");
        using (var tm = SqliteTranslationMemory.Create(path, "en", "hu"))
            tm.Add(Entry("Save", "Mentés", Monday));
        Sql(path, "UPDATE entries SET target = x'0102'");

        using var reopened = SqliteTranslationMemory.Open(path);
        Assert.Throws<TmFormatException>(() => reopened.Lookup(Text("Save")));
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static TmEntry Entry(string source, string target, DateTimeOffset changed) =>
        new(Text(source), Text(target)) { Changed = changed };

    private static InlineContent Text(string text) => InlineContent.FromText(text);
}
