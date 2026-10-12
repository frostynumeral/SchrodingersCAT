using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SchrodingersCat.Memory;

/// <summary>
/// A termbase in a SQLite file (<c>*.sctb</c>, section 3 of <c>docs/design/termbase.md</c>). One file per termbase
/// and language pair; changes are written at once, in transactions, so a crash loses nothing that was stored.
/// </summary>
/// <remarks>
/// <para><b>Reading</b> is from memory: <see cref="Open"/> reads every entry into <see cref="Index"/> (200,000 entries
/// in about 1.7 s, see step 3 of the design), so call it on a background thread. After each write commits,
/// <see cref="Index"/> is replaced by a new snapshot; it can be read from any thread, while the writes run one at a
/// time.</para>
/// <para><b>Untrusted files:</b> a termbase can come from someone else. Opening one checks its format, version and
/// schema (exactly the expected tables, in exactly the expected SQL, so no triggers, views or indexes of its own run
/// on our writes), turns on <c>trusted_schema=OFF</c>, and checks every value it loads. Damage surfaces as
/// <see cref="TermbaseFormatException"/>.</para>
/// </remarks>
public sealed class SqliteTermbase : ITermbase
{
    /// <summary>The file format version this code reads and writes.</summary>
    public const int FormatVersion = 1;

    private const string Format = "SchrodingersCat.TB";

    // How often loading checks for cancellation, in entries.
    internal const int CheckEvery = 4_096;

    // A term's side, as stored.
    private const long SourceSide = 0;
    private const long TargetSide = 1;

    // Strings are raw UTF-16 BLOBs (TermbaseText), changed is UTC ticks, side is 0 or 1 (SourceSide, TargetSide),
    // position counts from 0 within an entry's side, status is (int)TermStatus. terms is stored in the order of its key,
    // so loading reads it in one pass beside entries, and an entry's terms are one range of it.
    private static readonly string[] Schema =
    [
        "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT, WITHOUT ROWID",
        "CREATE TABLE entries(id INTEGER PRIMARY KEY, note BLOB, changed INTEGER NOT NULL, changed_by BLOB) STRICT",
        "CREATE TABLE terms(entry INTEGER NOT NULL, side INTEGER NOT NULL, position INTEGER NOT NULL, text BLOB NOT NULL, "
            + "status INTEGER NOT NULL, note BLOB, PRIMARY KEY(entry, side, position)) STRICT, WITHOUT ROWID",
    ];

    // What sqlite_schema must hold, SQL text included: a table changed by ALTER TABLE, or created otherwise, isn't ours.
    private static readonly HashSet<(string Type, string Name, string Table, string? Sql)> SchemaRows =
    [
        ("table", "meta", "meta", Schema[0]), ("table", "entries", "entries", Schema[1]), ("table", "terms", "terms", Schema[2]),
    ];

    private static readonly HashSet<string> RequiredKeys = ["format", "version", "source_language", "target_language"];

    private static readonly HashSet<string> Keys = [.. RequiredKeys, "last_deleted_id", "deleted_since_compact"];

    private readonly SqliteConnection _connection;

    private TermIndex _index;

    private SqliteTermbase(SqliteConnection connection, string sourceLanguage, string targetLanguage, TermIndex index)
    {
        _connection = connection;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        _index = index;
    }

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    public TermIndex Index => Volatile.Read(ref _index);

    /// <summary>Creates a new, empty termbase file. Fails if the file exists.</summary>
    public static SqliteTermbase Create(string path, string sourceLanguage, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrEmpty(sourceLanguage);
        ArgumentException.ThrowIfNullOrEmpty(targetLanguage);
        if (File.Exists(path))
            throw new IOException($"'{path}' already exists.");

        var connection = Connect(path, SqliteOpenMode.ReadWriteCreate);
        try
        {
            using var transaction = connection.BeginTransaction();
            foreach (var statement in Schema)
                Execute(connection, statement);
            foreach (var (key, value) in new[]
            {
                ("format", Format), ("version", FormatVersion.ToString(CultureInfo.InvariantCulture)),
                ("source_language", sourceLanguage), ("target_language", targetLanguage),
            })
                Execute(connection, "INSERT INTO meta(key, value) VALUES($key, $value)", ("$key", key), ("$value", value));
            transaction.Commit();
            return new SqliteTermbase(connection, sourceLanguage, targetLanguage, TermIndex.Empty);
        }
        catch
        {
            connection.Dispose();
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// Opens an existing termbase file, checking that it is one this version can use, and reads every entry into
    /// <see cref="Index"/>: slow for a large termbase, so call it on a background thread.
    /// </summary>
    /// <exception cref="TermbaseFormatException">The file isn't a termbase, is damaged, or is from another format version.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the file is closed again.</exception>
    public static SqliteTermbase Open(string path, CancellationToken cancellationToken = default) => Open(path, checkpoint: null, cancellationToken);

    // checkpoint runs where loading checks for cancellation, before it checks: for tests, which cancel there.
    internal static SqliteTermbase Open(string path, Action? checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"'{path}' doesn't exist.", path);

        var name = Path.GetFileName(path);
        var connection = Connect(path, SqliteOpenMode.ReadWrite);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rows = new HashSet<(string, string, string, string?)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            if (!rows.SetEquals(SchemaRows))
                throw new TermbaseFormatException($"'{name}' isn't a termbase, or has objects of its own.");

            var meta = new Dictionary<string, string>();
            using (var command = connection.CreateCommand())
            {
                // Text only: GetString would turn a number or a BLOB into text, which the writes (MetaLong) wouldn't read.
                command.CommandText = "SELECT key, value, typeof(key) = 'text' AND typeof(value) = 'text' FROM meta";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetInt64(2) != 1)
                        throw new TermbaseFormatException($"'{name}' has a damaged setting.");
                    meta[reader.GetString(0)] = reader.GetString(1);
                }
            }

            if (meta.GetValueOrDefault("format") != Format)
                throw new TermbaseFormatException($"'{name}' isn't a termbase.");
            if (meta.GetValueOrDefault("version") != FormatVersion.ToString(CultureInfo.InvariantCulture))
                throw new TermbaseFormatException($"'{name}' is a termbase of another version ({meta.GetValueOrDefault("version")}).");
            if (meta.GetValueOrDefault("source_language") is not { Length: > 0 } source || meta.GetValueOrDefault("target_language") is not { Length: > 0 } target)
                throw new TermbaseFormatException($"'{name}' doesn't say its languages.");
            if (!meta.Keys.All(Keys.Contains))
                throw new TermbaseFormatException($"'{name}' isn't a termbase this version knows.");
            if (MetaNumber(meta.GetValueOrDefault("last_deleted_id")) == long.MaxValue)
                throw new TermbaseFormatException($"'{name}' has no ids left.");
            MetaNumber(meta.GetValueOrDefault("deleted_since_compact"));

            var index = TermIndex.Of(Load(connection, name, checkpoint, cancellationToken));
            return new SqliteTermbase(connection, source, target, index);
        }
        catch (Exception ex)
        {
            connection.Dispose();
            // InvalidOperationException: a NULL where the schema allows none, which only a file written otherwise has.
            if (ex is SqliteException or InvalidCastException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
                throw new TermbaseFormatException($"'{name}' isn't a termbase, or is damaged.", ex);
            throw;
        }
    }

    public TermEntryId Add(TermEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AddRange([entry])[0];
    }

    public IReadOnlyList<TermEntryId> AddRange(IEnumerable<TermEntry> entries)
    {
        var list = Normalized(entries);
        if (list.Count == 0)
            return [];

        using var transaction = _connection.BeginTransaction();
        var next = NextIds(list.Count);
        using var insertEntry = Command(
            "INSERT INTO entries(id, note, changed, changed_by) VALUES($id, $note, $changed, $changedBy)", "$id", "$note", "$changed", "$changedBy");
        using var insertTerm = TermCommand();
        var stored = new List<StoredTermEntry>(list.Count);
        foreach (var entry in list)
        {
            var id = new TermEntryId(next++);
            Run(insertEntry, id.Value, TermbaseText.WriteNullable(entry.Note), entry.Changed.UtcTicks, TermbaseText.WriteNullable(entry.ChangedBy));
            InsertTerms(insertTerm, id, entry);
            stored.Add(new StoredTermEntry(id, entry));
        }

        transaction.Commit();
        Volatile.Write(ref _index, _index.WithRange(stored));
        return [.. stored.Select(s => s.Id)];
    }

    public bool Update(TermEntryId id, TermEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = Normalized(entry);

        using var transaction = _connection.BeginTransaction();
        using (var update = Command("UPDATE entries SET note = $note, changed = $changed, changed_by = $changedBy WHERE id = $id", "$note", "$changed", "$changedBy", "$id"))
        {
            if (Run(update, TermbaseText.WriteNullable(entry.Note), entry.Changed.UtcTicks, TermbaseText.WriteNullable(entry.ChangedBy), id.Value) == 0)
                return false;
        }

        Execute(_connection, "DELETE FROM terms WHERE entry = $id", ("$id", id.Value));
        using (var insertTerm = TermCommand())
            InsertTerms(insertTerm, id, entry);
        transaction.Commit();
        Volatile.Write(ref _index, _index.With(id, entry));
        return true;
    }

    public int Delete(IReadOnlyCollection<TermEntryId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return 0;

        using var transaction = _connection.BeginTransaction();
        using var deleteEntry = Command("DELETE FROM entries WHERE id = $id", "$id");
        using var deleteTerms = Command("DELETE FROM terms WHERE entry = $id", "$id");
        var removed = new List<TermEntryId>();
        foreach (var id in ids.Distinct())
        {
            if (Run(deleteEntry, id.Value) == 0)
                continue;
            Run(deleteTerms, id.Value);
            removed.Add(id);
        }

        if (removed.Count == 0)
            return 0;

        RecordDeleted(removed.Max(id => id.Value), removed.Count);
        transaction.Commit();
        Volatile.Write(ref _index, _index.WithoutRange(removed));
        return removed.Count;
    }

    /// <summary>
    /// Rewrites the file (<c>VACUUM</c>) if more than a quarter of its entries were deleted since it was last rewritten,
    /// as the TM does (<see cref="SqliteTranslationMemory.Compact"/>). Ids stay: <c>VACUUM</c> keeps the row ids of a
    /// table with an integer primary key, and the highest id deleted stays in <c>meta</c>.
    /// </summary>
    public bool Compact()
    {
        var deleted = MetaLong("deleted_since_compact");
        if (deleted * 4 <= deleted + Index.Count)
            return false;

        Execute(_connection, "VACUUM");
        Execute(_connection, "DELETE FROM meta WHERE key = 'deleted_since_compact'");
        return true;
    }

    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- shared with the in-memory termbase

    // What both termbases store: Changed as UTC, so the snapshot after a write is the one reopening gives.
    internal static TermEntry Normalized(TermEntry entry) =>
        entry.Changed.Offset == TimeSpan.Zero ? entry : entry with { Changed = entry.Changed.ToUniversalTime() };

    // New entries, checked before anything is stored.
    internal static List<TermEntry> Normalized(IEnumerable<TermEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = new List<TermEntry>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));
            list.Add(Normalized(entry));
        }

        return list;
    }

    // ---------------------------------------------------------------- storing

    // The first of count new ids, the next after both the last one stored and the last one deleted, so an id is never
    // reused: a stale id in the app can't reach another entry. Fails before anything is written if the last of them
    // would pass long.MaxValue, as a wrapped id would be negative and the file could no longer be opened.
    private long NextIds(int count)
    {
        var highest = (long)Scalar("SELECT coalesce(max(id), 0) FROM entries")!;
        try
        {
            var first = checked(Math.Max(highest, MetaLong("last_deleted_id")) + 1);
            _ = checked(first + (count - 1));
            return first;
        }
        catch (OverflowException ex)
        {
            throw new TermbaseFormatException("The termbase has no ids left.", ex);
        }
    }

    private SqliteCommand TermCommand() => Command(
        "INSERT INTO terms(entry, side, position, text, status, note) VALUES($entry, $side, $position, $text, $status, $note)",
        "$entry", "$side", "$position", "$text", "$status", "$note");

    private static void InsertTerms(SqliteCommand insert, TermEntryId id, TermEntry entry)
    {
        foreach (var (side, terms) in new[] { (SourceSide, entry.Source), (TargetSide, entry.Target) })
        {
            for (var position = 0; position < terms.Count; position++)
            {
                var term = terms[position];
                Run(insert, id.Value, side, (long)position, TermbaseText.Write(term.Text), (long)term.Status, TermbaseText.WriteNullable(term.Note));
            }
        }
    }

    // Remembers the highest id deleted, so NextId never hands it out again, and how many entries went since the file
    // was last compacted. Both in meta, which takes new keys without a schema change.
    private void RecordDeleted(long highestId, int count)
    {
        long since;
        try
        {
            since = checked(MetaLong("deleted_since_compact") + count);
        }
        catch (OverflowException ex)
        {
            throw new TermbaseFormatException("The termbase's count of deleted entries is damaged.", ex);
        }

        foreach (var (key, value) in new[] { ("last_deleted_id", Math.Max(MetaLong("last_deleted_id"), highestId)), ("deleted_since_compact", since) })
        {
            Execute(_connection,
                "INSERT INTO meta(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("$key", key), ("$value", value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    // A number kept in meta; 0 if it isn't there.
    private long MetaLong(string key)
    {
        try
        {
            return Scalar("SELECT value FROM meta WHERE key = $key", ("$key", key)) switch
            {
                null => 0,
                string value => MetaNumber(value),
                var other => throw new FormatException($"A {other.GetType().Name} isn't a count."),
            };
        }
        catch (FormatException ex)
        {
            throw new TermbaseFormatException($"The termbase's {key} is damaged.", ex);
        }
    }

    // Digits only: no sign, no spaces, so damage is never read as some other number.
    private static long MetaNumber(string? value) =>
        value is null ? 0
        : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
        : throw new FormatException($"'{value}' isn't a count.");

    // ---------------------------------------------------------------- loading

    // Every entry, in the order of its id: entries and terms are read side by side, each in the order it is stored,
    // and every value is checked, so a damaged file can't give an entry the termbase couldn't have stored. Each row is
    // read in one call (GetValues), as reading a column at a time costs most of the time loading takes.
    private static IEnumerable<StoredTermEntry> Load(SqliteConnection connection, string name, Action? checkpoint, CancellationToken cancellationToken)
    {
        using var entriesCommand = connection.CreateCommand();
        entriesCommand.CommandText = "SELECT id, note, changed, changed_by FROM entries ORDER BY id";
        using var termsCommand = connection.CreateCommand();
        termsCommand.CommandText = "SELECT entry, side, position, text, status, note FROM terms ORDER BY entry, side, position";
        using var entries = entriesCommand.ExecuteReader();
        using var terms = termsCommand.ExecuteReader();

        var entry = new object[4];
        var term = new object[6];

        // Each user's name is kept once, not once per entry: a termbase has a few users and many entries.
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasTerm = Next(terms, term);
        long previous = 0;
        var count = 0;
        while (Next(entries, entry))
        {
            if (++count % CheckEvery == 0)
            {
                checkpoint?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var id = Integer(entry[0]);
            StoredTermEntry stored;
            try
            {
                if (id <= previous)
                    throw new TermbaseFormatException("Ids out of order.");
                previous = id;

                List<Term> source = [];
                List<Term> target = [];
                while (hasTerm && Integer(term[0]) is var termEntry && termEntry <= id)
                {
                    if (termEntry < id)
                        throw new TermbaseFormatException("A term without its entry.");

                    var side = Integer(term[1]) switch
                    {
                        SourceSide when target.Count == 0 => source,
                        TargetSide => target,
                        _ => throw new TermbaseFormatException("A term on no side."),
                    };
                    if (Integer(term[2]) != side.Count)
                        throw new TermbaseFormatException("Terms out of order.");
                    var status = Integer(term[4]);
                    if (status is < 0 or > (long)TermStatus.Forbidden || !Enum.IsDefined((TermStatus)status))
                        throw new TermbaseFormatException("A term's status is damaged.");
                    side.Add(new Term(TermbaseText.Decode(term[3]), (TermStatus)status) { Note = TermbaseText.DecodeNullable(term[5]) });
                    hasTerm = Next(terms, term);
                }

                var changedBy = TermbaseText.DecodeNullable(entry[3]);
                if (changedBy is not null && !users.TryAdd(changedBy, changedBy))
                    changedBy = users[changedBy];

                var ticks = Integer(entry[2]);
                if (ticks is < 0 || ticks > DateTime.MaxValue.Ticks)
                    throw new TermbaseFormatException("A date is damaged.");
                stored = new StoredTermEntry(
                    new TermEntryId(id),
                    new TermEntry(source, target, TermbaseText.DecodeNullable(entry[1]), new DateTimeOffset(ticks, TimeSpan.Zero), changedBy));
            }
            catch (Exception ex) when (ex is TermbaseFormatException or ArgumentException)
            {
                throw new TermbaseFormatException($"'{name}' has a damaged entry (id {id}).", ex);
            }

            yield return stored;
        }

        if (hasTerm)
            throw new TermbaseFormatException($"'{name}' is damaged: a term without its entry (id {term[0]}).");
    }

    // The next row's values, if there is one.
    private static bool Next(SqliteDataReader reader, object[] row)
    {
        if (!reader.Read())
            return false;

        reader.GetValues(row);
        return true;
    }

    // An INTEGER value, nothing SQLite would convert to one.
    private static long Integer(object value) =>
        value is long number ? number : throw new TermbaseFormatException("A stored number is damaged.");

    // ---------------------------------------------------------------- SQLite

    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        // No pooling, so disposing really closes the file.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA trusted_schema = OFF");
            Execute(connection, "PRAGMA foreign_keys = OFF");
            return connection;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new TermbaseFormatException($"'{Path.GetFileName(path)}' can't be opened as a termbase.", ex);
        }
    }

    // A command prepared once and run with new values per row.
    private SqliteCommand Command(string sql, params string[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var name in parameters)
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        return command;
    }

    private static int Run(SqliteCommand command, params object[] values)
    {
        for (var i = 0; i < values.Length; i++)
            command.Parameters[i].Value = values[i];
        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
