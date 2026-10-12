using CsCheck;
using Microsoft.Data.Sqlite;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Memory;

namespace SchrodingersCat.Memory.Tests;

/// <summary>
/// The SQLite termbase against the in-memory one, and against damaged files. Replay a failure with
/// <c>CsCheck_Seed=&lt;seed&gt; dotnet test --filter …</c>.
/// </summary>
public class TermbasePropertyTests
{
    // A random script of adds, imports, updates (of stale ids too), deletes, reopenings and compactions leaves both
    // termbases with the same entries under the same ids, the file with exactly them, and the snapshot each write
    // left equal to an index built from scratch.
    [Fact]
    public void EditScripts_KeepTheFileAndTheInMemoryTermbaseEqual() =>
        Gen.Select(
            TermGen.FullEntry(Gen.Frequency((3, TermGen.Term), (1, TermGen.AnyTerm))).Array[1, 12],
            TermGen.Content.Array[1, 4],
            Gen.Int[0, 1_000_000].Array[1, 30]).Sample((pool, texts, script) =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"schrodingerscat-{Guid.NewGuid():N}.sctb");
            var sqlite = SqliteTermbase.Create(path, "en", "hu");
            try
            {
                var memory = new InMemoryTermbase("en", "hu");
                var given = new List<TermEntryId>();
                var deleted = new List<TermEntryId>();
                var n = 0;
                foreach (var step in script)
                {
                    var entry = pool[step / 6 % pool.Length];
                    var ids = memory.Index.Entries().Select(e => e.Id).ToList();
                    switch (step % 6)
                    {
                        case 0:
                            var id = memory.Add(entry);
                            Assert.Equal(id, sqlite.Add(entry));
                            given.Add(id);
                            break;
                        case 1:
                            var batch = Enumerable.Range(0, step / 7 % 5).Select(i => pool[(step + i) % pool.Length]).ToList();
                            var added = memory.AddRange(batch);
                            Assert.Equal(added, sqlite.AddRange(batch));
                            given.AddRange(added);
                            break;
                        case 2:
                            // Every fifth update is of a stale id: a deleted one, or one never given.
                            var target = ++n % 5 == 0 || ids.Count == 0
                                ? (deleted.Count > 0 && step % 2 == 0 ? deleted[step / 11 % deleted.Count] : new TermEntryId((given.Count == 0 ? 0 : given.Max(i => i.Value)) + 1))
                                : ids[step / 11 % ids.Count];
                            Assert.Equal(memory.Update(target, entry), sqlite.Update(target, entry));
                            break;
                        case 3:
                            List<TermEntryId> doomed = [.. ids.Where((_, i) => (i + step) % (1 + (step / 5 % 4)) == 0)];
                            doomed.Add(new TermEntryId(10_000 + step));
                            if (doomed.Count > 1)
                                doomed.Add(doomed[0]);
                            Assert.Equal(memory.Delete(doomed), sqlite.Delete(doomed));
                            deleted.AddRange(doomed.Where(d => ids.Contains(d)));
                            break;
                        case 4:
                            sqlite.Dispose();
                            sqlite = SqliteTermbase.Open(path);
                            break;
                        default:
                            sqlite.Compact();
                            break;
                    }

                    AssertSame(memory.Index, sqlite.Index);
                }

                // Ids only ever increase.
                Assert.Equal(given.Select(i => i.Value).Order(), given.Select(i => i.Value));
                Assert.Equal(given.Count, given.Distinct().Count());

                sqlite.Dispose();
                sqlite = SqliteTermbase.Open(path);
                AssertSame(memory.Index, sqlite.Index);
                var rebuilt = TermIndex.Of(memory.Index.Entries());
                foreach (var text in texts)
                {
                    var source = QaText.Of(text);
                    Assert.Equal(rebuilt.Find(source), memory.Index.Find(source));
                    Assert.Equal(rebuilt.Find(source), sqlite.Index.Find(source));
                }

                Assert.Equal(0L, Count(path, "SELECT count(*) FROM terms WHERE entry NOT IN (SELECT id FROM entries)"));
                Assert.Equal(0L, Count(path, "SELECT count(*) FROM terms t WHERE position <> (SELECT count(*) FROM terms u WHERE u.entry = t.entry AND u.side = t.side AND u.position < t.position)"));
            }
            finally
            {
                sqlite.Dispose();
                File.Delete(path);
            }
        });

    // Any damage, to the bytes or to a value, either opens to entries the termbase could have stored or fails with
    // TermbaseFormatException; and the file is closed either way.
    [Fact]
    public void DamagedFiles_FailOnlyWithTermbaseFormatException() =>
        Gen.Select(
            TermGen.FullEntry(TermGen.AnyTerm).Array[1, 20],
            Gen.Int[0, 2],
            Gen.Int[0, int.MaxValue].Array[1, 8],
            Gen.Byte.Array[1, 8]).Sample((entries, kind, places, bytes) =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"schrodingerscat-{Guid.NewGuid():N}.sctb");
            try
            {
                using (var termbase = SqliteTermbase.Create(path, "en", "hu"))
                    termbase.AddRange(entries);

                if (!Damage(path, kind, places, bytes))
                    return;

                try
                {
                    using var opened = SqliteTermbase.Open(path);
                    var loaded = opened.Index.Entries().ToList();
                    Assert.Equal(loaded, TermIndex.Of(loaded).Entries());
                    Assert.All(loaded, e => Assert.NotEmpty(e.Entry.Source));
                    Assert.All(loaded, e => Assert.Equal(TimeSpan.Zero, e.Entry.Changed.Offset));
                }
                catch (TermbaseFormatException)
                {
                }
            }
            finally
            {
                File.Delete(path);
            }
        });

    private static readonly string[] Columns =
        ["entries.note", "entries.changed", "entries.changed_by", "entries.id", "terms.entry", "terms.side", "terms.position", "terms.text", "terms.status", "terms.note"];

    // Damages the file: overwrites bytes, cuts it short, or sets a column to some value. False if SQLite refused the
    // value (a STRICT column's type, a key repeated).
    private static bool Damage(string path, int kind, int[] places, byte[] bytes)
    {
        switch (kind)
        {
            case 0:
                using (var file = File.Open(path, FileMode.Open))
                {
                    for (var i = 0; i < places.Length; i++)
                    {
                        file.Position = places[i] % file.Length;
                        file.WriteByte(bytes[i % bytes.Length]);
                    }
                }

                return true;
            case 1:
                using (var file = File.Open(path, FileMode.Open))
                    file.SetLength(places[0] % file.Length);
                return true;
            default:
                var (table, column) = (Columns[places[0] % Columns.Length].Split('.')[0], Columns[places[0] % Columns.Length].Split('.')[1]);
                var which = (places.Length > 1 ? places[1] : 0) % 4;
                object value = which switch
                {
                    0 => bytes,
                    1 => (long)(sbyte)bytes[0],
                    2 => places[0] % 7 == 0 ? long.MaxValue : (long)places[0] - 100,
                    _ => "text",
                };
                try
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = table == "entries"
                        ? $"UPDATE entries SET {column} = $value WHERE id = (SELECT id FROM entries LIMIT 1 OFFSET $row)"
                        : $"UPDATE terms SET {column} = $value WHERE (entry, side, position) = (SELECT entry, side, position FROM terms LIMIT 1 OFFSET $row)";
                    command.Parameters.AddWithValue("$value", value);
                    command.Parameters.AddWithValue("$row", (places.Length > 2 ? places[2] : 0) % 5);
                    command.ExecuteNonQuery();
                    return true;
                }
                catch (SqliteException)
                {
                    return false;
                }
        }
    }

    private static void AssertSame(TermIndex expected, TermIndex actual)
    {
        var entries = expected.Entries().ToList();
        Assert.Equal(entries, actual.Entries());
        Assert.All(actual.Entries(), e => Assert.Equal(TimeSpan.Zero, e.Entry.Changed.Offset));
        Assert.Equal(entries.Count, actual.Count);
    }

    private static long Count(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
