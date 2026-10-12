using Microsoft.Data.Sqlite;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

/// <summary>The SQLite termbase's file (step 3 of <c>docs/design/termbase.md</c>), and the files it refuses.</summary>
public sealed class SqliteTermbaseTests : IDisposable
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(2));

    private readonly string _directory = Directory.CreateTempSubdirectory("schrodingerscat-tb-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void EntriesAndLanguages_SurviveReopening()
    {
        var path = PathOf("a.sctb");
        var entry = new TermEntry(
            [new Term("file manager", TermStatus.Preferred) { Note = "UI" }, new Term("file browser", TermStatus.Forbidden) { Note = "" }],
            [new Term("fájlkezelő", TermStatus.Admitted), new Term("", TermStatus.Unspecified), new Term("\uD800\0￿", TermStatus.Forbidden) { Note = "old" }],
            "The program that shows files \uD800\0￿.",
            Monday,
            "anna");
        TermEntryId id;
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
            id = termbase.Add(entry);

        using var reopened = SqliteTermbase.Open(path);

        Assert.Equal(("en", "hu", 1), (reopened.SourceLanguage, reopened.TargetLanguage, reopened.Index.Count));
        var stored = reopened.Index.Find(id)!;
        Assert.Equal(entry, stored);
        Assert.Equal((TimeSpan.Zero, Monday.UtcTicks), (stored.Changed.Offset, stored.Changed.UtcTicks));
        Assert.Equal("", stored.Source[1].Note);
        Assert.Null(stored.Target[1].Note);
        Assert.Equal(id, Assert.Single(reopened.Index.Find(QaText.Of(InlineContent.FromText("Open the File Manager.")))).Id);
    }

    // The numbers a file stores: an enum reordered must not change what old files mean.
    [Fact]
    public void StorageNumbers_ArePinned()
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
        {
            termbase.Add(new TermEntry(
                [new Term("a"), new Term("b", TermStatus.Preferred)],
                [new Term("c", TermStatus.Admitted), new Term("d", TermStatus.Forbidden), new Term("e")],
                null, Monday, null));
        }

        Assert.Equal(
            [(0L, 0L, 0L), (0, 1, 1), (1, 0, 2), (1, 1, 3), (1, 2, 0)],
            Rows(path, "SELECT side, position, status FROM terms ORDER BY side, position").Select(r => ((long)r[0]!, (long)r[1]!, (long)r[2]!)));
        Assert.Equal([Monday.UtcTicks], Rows(path, "SELECT changed FROM entries").Select(r => (long)r[0]!));
    }

    // The empty string is an empty BLOB, null is NULL, and both come back as they went in.
    [Fact]
    public void EmptyStringsAndNulls_AreStoredApart()
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
            termbase.Add(new TermEntry([new Term("") { Note = "" }], [], "", Monday, null));

        Assert.Equal(
            [("blob", 0L, "blob", "null")],
            Rows(path, "SELECT typeof(t.text), length(t.text), typeof(e.note), typeof(e.changed_by) FROM terms t JOIN entries e ON e.id = t.entry")
                .Select(r => ((string)r[0]!, (long)r[1]!, (string)r[2]!, (string)r[3]!)));
        using var reopened = SqliteTermbase.Open(path);
        var entry = reopened.Index.Entries().Single().Entry;
        Assert.Equal(("", "", "", null), (entry.Source[0].Text, entry.Source[0].Note, entry.Note, entry.ChangedBy));
    }

    [Fact]
    public void Create_RefusesAnExistingFile()
    {
        var path = PathOf("a.sctb");
        File.WriteAllText(path, "keep me");

        Assert.Throws<IOException>(() => SqliteTermbase.Create(path, "en", "hu"));
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public void Create_WithoutLanguages_LeavesNoFile()
    {
        Assert.Throws<ArgumentException>(() => SqliteTermbase.Create(PathOf("a.sctb"), "", "hu"));
        Assert.Throws<ArgumentException>(() => SqliteTermbase.Create(PathOf("a.sctb"), "en", ""));
        Assert.False(File.Exists(PathOf("a.sctb")));
    }

    [Fact]
    public void Open_AMissingFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() => SqliteTermbase.Open(PathOf("missing.sctb")));

    [Fact]
    public void Open_CanBeCancelled_AndClosesTheFile()
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
            termbase.Add(Entry("file"));

        Assert.Throws<OperationCanceledException>(() => SqliteTermbase.Open(path, new CancellationToken(canceled: true)));
        AssertClosed(path);
    }

    // Cancelled while the entries load (on other threads, through TermIndex's Parallel.ForEach): the same exception, not
    // an AggregateException, and the file is closed.
    [Fact]
    public void Open_CanBeCancelledWhileLoading_AndClosesTheFile()
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
            termbase.AddRange([.. Enumerable.Range(0, SqliteTermbase.CheckEvery * 2 + 10).Select(i => Entry($"term {i}"))]);

        using var cancel = new CancellationTokenSource();
        var checks = 0;
        Assert.Throws<OperationCanceledException>(() => SqliteTermbase.Open(
            path,
            () =>
            {
                if (++checks == 1)
                    cancel.Cancel();
            },
            cancel.Token));
        Assert.Equal(1, checks);
        AssertClosed(path);
    }

    // The ids of a batch are checked as a whole before anything is written: one past long.MaxValue would wrap to a
    // negative id, and the file could no longer be opened.
    [Theory]
    [InlineData("UPDATE terms SET entry = 9223372036854775806 WHERE entry = 1; UPDATE entries SET id = 9223372036854775806 WHERE id = 1")]
    [InlineData("DELETE FROM terms; DELETE FROM entries; INSERT INTO meta VALUES('last_deleted_id', '9223372036854775806')")]
    public void AddRange_PastTheLastId_FailsBeforeWriting(string sql)
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
            termbase.Add(Entry("first"));
        Sql(path, sql);

        using (var termbase = SqliteTermbase.Open(path))
        {
            var before = termbase.Index.Entries().ToList();
            Assert.Throws<TermbaseFormatException>(() => termbase.AddRange([Entry("a"), Entry("b"), Entry("c")]));
            Assert.Equal(before, termbase.Index.Entries());
            Assert.Throws<TermbaseFormatException>(() => termbase.AddRange([Entry("a"), Entry("b")]));
            Assert.Equal(long.MaxValue, termbase.Add(Entry("last")).Value);
            Assert.Throws<TermbaseFormatException>(() => termbase.Add(Entry("one more")));
        }

        using var reopened = SqliteTermbase.Open(path);
        Assert.Equal(long.MaxValue, reopened.Index.Entries().Last().Id.Value);
        Assert.All(reopened.Index.Entries(), e => Assert.True(e.Id.Value > 0));
    }

    // A setting stored as a number or a BLOB, which only a file written otherwise has (meta is STRICT): GetString would
    // read it as text, and the writes wouldn't, so last_deleted_id = 100 would count as 0 and deleted ids come again.
    [Theory]
    [InlineData("INSERT INTO meta VALUES('last_deleted_id', 100)")]
    [InlineData("INSERT INTO meta VALUES('last_deleted_id', x'313030')")]
    [InlineData("INSERT INTO meta VALUES(x'6c6173745f64656c657465645f6964', '100')")]
    public void Open_ASettingThatIsntText_IsRefused(string insert)
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
            termbase.Add(Entry("file"));

        // meta's SQL without STRICT while the value goes in, then as it was, so the schema check still passes.
        const string Strict = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT, WITHOUT ROWID";
        const string Loose = "CREATE TABLE meta(key BLOB PRIMARY KEY, value BLOB NOT NULL) WITHOUT ROWID";
        Sql(path, $"PRAGMA writable_schema = ON; UPDATE sqlite_schema SET sql = '{Loose}' WHERE name = 'meta'");
        Sql(path, insert);
        Sql(path, $"PRAGMA writable_schema = ON; UPDATE sqlite_schema SET sql = '{Strict}' WHERE name = 'meta'");
        Assert.Contains(Rows(path, "SELECT typeof(key), typeof(value) FROM meta"), r => (string)r[0]! != "text" || (string)r[1]! != "text");

        Assert.Throws<TermbaseFormatException>(() => SqliteTermbase.Open(path));
        AssertClosed(path);
    }

    // AssertClosed sees an open termbase, so its checks aren't vacuous.
    [Fact]
    public void AssertClosed_SeesAnOpenTermbase()
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
            Assert.ThrowsAny<Exception>(() => AssertClosed(path, delete: false));
        AssertClosed(path);
    }

    public static TheoryData<string> Untrusted =>
    [
        "text", "half", "other database", "translation memory",
        "CREATE TRIGGER t AFTER INSERT ON entries BEGIN DELETE FROM meta; END",
        "CREATE TRIGGER sqliteXautoindexXevil AFTER INSERT ON entries BEGIN DELETE FROM meta; END",
        "CREATE VIEW v AS SELECT * FROM terms",
        "CREATE INDEX terms_text ON terms(text)",
        "CREATE TABLE more(x)",
        "ALTER TABLE entries ADD COLUMN x INTEGER",
        "DROP TABLE terms; CREATE TABLE terms(entry INTEGER NOT NULL, side INTEGER NOT NULL, position INTEGER NOT NULL, text BLOB NOT NULL, "
            + "status INTEGER NOT NULL, note BLOB, PRIMARY KEY(entry, side, position)) WITHOUT ROWID",
        "ANALYZE",
        "UPDATE meta SET value = '2' WHERE key = 'version'",
        "UPDATE meta SET value = 'SchrodingersCat.TM' WHERE key = 'format'",
        "DELETE FROM meta WHERE key = 'target_language'",
        "UPDATE meta SET value = '' WHERE key = 'source_language'",
        "INSERT INTO meta VALUES('surprise', '1')",
        "INSERT INTO meta VALUES('last_deleted_id', 'abc')",
        "INSERT INTO meta VALUES('last_deleted_id', '-1')",
        "INSERT INTO meta VALUES('last_deleted_id', ' 5')",
        "INSERT INTO meta VALUES('last_deleted_id', '9223372036854775807')",
        "INSERT INTO meta VALUES('deleted_since_compact', '5.0')",
    ];

    // A file that isn't a termbase this version made, or holds SQL of its own, is refused before anything is read
    // from it, and left closed, so it can be deleted at once.
    [Theory]
    [MemberData(nameof(Untrusted))]
    public void Open_AFileThatIsntOurs_IsRefused(string damage)
    {
        var path = PathOf("a.sctb");
        switch (damage)
        {
            case "text":
                File.WriteAllText(path, "<martif/>");
                break;
            case "half":
                using (var termbase = SqliteTermbase.Create(PathOf("whole.sctb"), "en", "hu"))
                    termbase.AddRange([.. Enumerable.Range(0, 200).Select(i => Entry($"term {i}"))]);
                var bytes = File.ReadAllBytes(PathOf("whole.sctb"));
                File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
                break;
            case "other database":
                Sql(path, "CREATE TABLE people(name TEXT)");
                break;
            case "translation memory":
                SqliteTranslationMemory.Create(path, "en", "hu").Dispose();
                break;
            default:
                SqliteTermbase.Create(path, "en", "hu").Dispose();
                Sql(path, damage);
                break;
        }

        var error = Assert.Throws<TermbaseFormatException>(() => SqliteTermbase.Open(path));
        if (damage.Contains("'2'", StringComparison.Ordinal))
            Assert.Contains("another version (2)", error.Message, StringComparison.Ordinal);
        AssertClosed(path);
    }

    public static TheoryData<string> Damage =>
    [
        "UPDATE terms SET text = x'616263' WHERE entry = 1",
        "UPDATE terms SET status = 9 WHERE entry = 1",
        "UPDATE terms SET status = -1 WHERE entry = 2",
        "UPDATE terms SET side = 2 WHERE entry = 2 AND side = 1",
        "UPDATE terms SET position = 5 WHERE entry = 1 AND side = 0 AND position = 1",
        "DELETE FROM terms WHERE entry = 1 AND side = 0",
        "INSERT INTO terms VALUES(999, 0, 0, x'', 0, NULL)",
        "INSERT INTO entries VALUES(3, NULL, 0, NULL); INSERT INTO entries VALUES(5, NULL, 0, NULL); "
            + "INSERT INTO terms VALUES(3, 0, 0, x'', 0, NULL); INSERT INTO terms VALUES(4, 0, 0, x'', 0, NULL); INSERT INTO terms VALUES(5, 0, 0, x'', 0, NULL)",
        "UPDATE entries SET changed = 9223372036854775807 WHERE id = 2",
        "UPDATE entries SET changed = -1 WHERE id = 2",
        "UPDATE entries SET note = x'00' WHERE id = 1",
        "UPDATE terms SET note = x'000000' WHERE entry = 2",
        "UPDATE terms SET entry = 0 WHERE entry = 1; UPDATE entries SET id = 0 WHERE id = 1",
        "UPDATE terms SET entry = -3 WHERE entry = 1; UPDATE entries SET id = -3 WHERE id = 1",
    ];

    // Each value is checked as it is loaded: damage never gives another exception, nor an entry it couldn't have stored.
    [Theory]
    [MemberData(nameof(Damage))]
    public void ADamagedEntry_FailsWithTermbaseFormatException(string damage)
    {
        var path = PathOf("a.sctb");
        using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
        {
            termbase.AddRange([
                new TermEntry([new Term("file"), new Term("document")], [new Term("fájl")], "note", Monday, null),
                new TermEntry([new Term("folder")], [new Term("mappa"), new Term("könyvtár")], null, Monday, "béla"),
            ]);
        }

        Sql(path, damage);

        var error = Assert.Throws<TermbaseFormatException>(() => SqliteTermbase.Open(path));
        Assert.Contains("damaged", error.Message, StringComparison.Ordinal);
        AssertClosed(path);
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static TermEntry Entry(string term) => new([new Term(term)], [], null, Monday, null);

    /// <summary>
    /// Fails if this process still has the file open, then deletes it. On Linux, where deleting an open file succeeds,
    /// by the process's open files (<c>/proc/self/fd</c>); elsewhere by opening it unshared, which an open SQLite
    /// connection prevents on Windows.
    /// </summary>
    internal static void AssertClosed(string path, bool delete = true)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsLinux())
        {
            var open = new List<string>();
            foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
            {
                try
                {
                    if (new FileInfo(fd).LinkTarget is { } target)
                        open.Add(target);
                }
                catch (IOException)
                {
                    // Closed meanwhile.
                }
            }

            Assert.DoesNotContain(full, open);
        }
        else
        {
            using (File.Open(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }

        if (delete)
            File.Delete(full);
    }

    internal static List<object?[]> Rows(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    internal static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
