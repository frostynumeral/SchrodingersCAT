using System.Globalization;
using Microsoft.Data.Sqlite;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>
/// A translation memory in a SQLite file (<c>*.sctm</c>, design section 4). One file per TM and language pair;
/// changes are written at once, in transactions, so a crash loses nothing that was stored.
/// </summary>
/// <remarks>
/// <para><b>Fuzzy lookups</b> don't score every entry. Each entry's normalised words, with sentence start and end
/// marks, form word bigrams (numbers count as one word, as in <see cref="MatchScore"/>); an index maps each bigram to
/// the entries that have it. A lookup takes the query's bigrams from the rarest up, until reading their entries would
/// exceed <see cref="PostingBudget"/> rows, and scores the <see cref="CandidateCount"/> entries sharing the most of
/// them. On a generated TM of 200,000 entries this found 99.4% of the matches a full scan finds, at a median of 4 ms
/// per lookup (the full scan: 300 ms); see step 2 of the design.</para>
/// <para><b>Untrusted files:</b> a TM file can come from someone else. Opening one checks its format, version and
/// schema (exactly the expected tables and indexes, so no triggers or views of its own run on our writes), and turns
/// on <c>trusted_schema=OFF</c>. Damage found later surfaces as <see cref="TmFormatException"/>.</para>
/// </remarks>
public sealed class SqliteTranslationMemory : ITranslationMemory, IDisposable
{
    /// <summary>The file format version this code reads and writes.</summary>
    public const int FormatVersion = 1;

    /// <summary>At most this many index rows are read per lookup.</summary>
    public const int PostingBudget = 80_000;

    /// <summary>At most this many entries are scored per lookup.</summary>
    public const int CandidateCount = 100;

    private const string Format = "SchrodingersCat.TM";

    private static readonly string[] Schema =
    [
        "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID",
        "CREATE TABLE entries(id INTEGER PRIMARY KEY, pair_hash INTEGER NOT NULL, source BLOB NOT NULL, target BLOB NOT NULL, "
            + "previous_source BLOB, next_source BLOB, document BLOB, changed INTEGER NOT NULL, changed_by BLOB)",
        "CREATE INDEX entries_pair ON entries(pair_hash)",
        "CREATE TABLE postings(feature INTEGER NOT NULL, entry INTEGER NOT NULL, PRIMARY KEY(feature, entry)) WITHOUT ROWID",
        "CREATE TABLE features(feature INTEGER PRIMARY KEY, entries INTEGER NOT NULL)",
    ];

    // What sqlite_schema must hold, beyond the automatic index of meta's primary key.
    private static readonly HashSet<(string Type, string Name)> SchemaObjects =
    [
        ("table", "meta"), ("table", "entries"), ("index", "entries_pair"), ("table", "postings"), ("table", "features"),
    ];

    private readonly SqliteConnection _connection;

    private SqliteTranslationMemory(SqliteConnection connection, string sourceLanguage, string targetLanguage)
    {
        _connection = connection;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
    }

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    public int Count => Convert.ToInt32(Scalar("SELECT count(*) FROM entries"), CultureInfo.InvariantCulture);

    /// <summary>Creates a new, empty TM file. Fails if the file exists.</summary>
    public static SqliteTranslationMemory Create(string path, string sourceLanguage, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);
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
            return new SqliteTranslationMemory(connection, sourceLanguage, targetLanguage);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens an existing TM file, checking that it is one this version can use.</summary>
    /// <exception cref="TmFormatException">The file isn't a TM, is damaged, or is from another format version.</exception>
    public static SqliteTranslationMemory Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"'{path}' doesn't exist.", path);

        var connection = Connect(path, SqliteOpenMode.ReadWrite);
        try
        {
            var objects = new HashSet<(string, string)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT type, name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_autoindex_%'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    objects.Add((reader.GetString(0), reader.GetString(1)));
            }

            if (!objects.SetEquals(SchemaObjects))
                throw new TmFormatException($"'{Path.GetFileName(path)}' isn't a translation memory, or has objects of its own.");

            var meta = new Dictionary<string, string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT key, value FROM meta";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    meta[reader.GetString(0)] = reader.GetString(1);
            }

            if (meta.GetValueOrDefault("format") != Format)
                throw new TmFormatException($"'{Path.GetFileName(path)}' isn't a translation memory.");
            if (meta.GetValueOrDefault("version") != FormatVersion.ToString(CultureInfo.InvariantCulture))
                throw new TmFormatException($"'{Path.GetFileName(path)}' is a translation memory of another version ({meta.GetValueOrDefault("version")}).");
            if (meta.GetValueOrDefault("source_language") is not { Length: > 0 } source || meta.GetValueOrDefault("target_language") is not { Length: > 0 } target)
                throw new TmFormatException($"'{Path.GetFileName(path)}' doesn't say its languages.");

            return new SqliteTranslationMemory(connection, source, target);
        }
        catch (Exception ex)
        {
            connection.Dispose();
            if (ex is SqliteException or InvalidCastException)
                throw new TmFormatException($"'{Path.GetFileName(path)}' isn't a translation memory, or is damaged.", ex);
            throw;
        }
    }

    public void Add(TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AddRange([entry]);
    }

    /// <summary>Stores several translations in one transaction (an import), with the rules of <see cref="Add"/>.</summary>
    public void AddRange(IEnumerable<TmEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var transaction = _connection.BeginTransaction();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));
            Store(entry);
        }

        transaction.Commit();
    }

    public IReadOnlyList<TmMatch> Lookup(
        InlineContent source,
        string? previousSource = null,
        string? nextSource = null,
        int threshold = Ranking.DefaultThreshold,
        int maxMatches = Ranking.DefaultMaxMatches)
    {
        ArgumentNullException.ThrowIfNull(source);

        var query = MatchScore.Normalized.Of(source);
        var features = Features(query).Distinct().ToList();

        // The rarest bigrams first, within the budget (always at least one).
        var chosen = new List<long>();
        long rows = 0;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"SELECT feature, entries FROM features WHERE feature IN ({string.Join(',', features)}) ORDER BY entries";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var count = reader.GetInt64(1);
                if (chosen.Count > 0 && rows + count > PostingBudget)
                    break;
                chosen.Add(reader.GetInt64(0));
                rows += count;
            }
        }

        if (chosen.Count == 0)
            return [];

        var candidates = new List<(TmEntry, MatchScore.Normalized)>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT e.id, e.source, e.target, e.previous_source, e.next_source, e.document, e.changed, e.changed_by FROM "
                + $"(SELECT entry, count(*) AS shared FROM postings WHERE feature IN ({string.Join(',', chosen)}) GROUP BY entry ORDER BY shared DESC LIMIT {CandidateCount}) p "
                + "JOIN entries e ON e.id = p.entry";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = ReadEntry(reader);
                candidates.Add((entry, MatchScore.Normalized.Of(entry.Source)));
            }
        }

        return Ranking.Rank(query, previousSource, nextSource, candidates, threshold, maxMatches);
    }

    /// <summary>Removes the stored pair, with its index rows; bigrams no entry has any more leave the index.</summary>
    public bool Remove(TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var transaction = _connection.BeginTransaction();
        var source = MatchScore.Normalized.Of(entry.Source);
        if (FindPair(source, entry) is not { Id: { } id })
            return false;

        Delete(id, source);
        transaction.Commit();
        return true;
    }

    public void Replace(TmEntry previous, TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(entry);

        using var transaction = _connection.BeginTransaction();
        var source = MatchScore.Normalized.Of(previous.Source);
        if (FindPair(source, previous) is { Id: { } id, Stored: var stored } && stored!.Document == previous.Document)
            Delete(id, source);
        Store(entry);
        transaction.Commit();
    }

    /// <summary>Every entry, oldest stored first (for export).</summary>
    public IEnumerable<TmEntry> Entries()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, source, target, previous_source, next_source, document, changed, changed_by FROM entries ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return ReadEntry(reader);
    }

    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- storing

    private void Store(TmEntry entry)
    {
        var source = MatchScore.Normalized.Of(entry.Source);
        var (pairHash, existing, _) = FindPair(source, entry);

        var values = new (string, object?)[]
        {
            ("$hash", pairHash), ("$source", TmContentFormat.Write(entry.Source)), ("$target", TmContentFormat.Write(entry.Target)),
            ("$previous", Text(entry.PreviousSource)), ("$next", Text(entry.NextSource)), ("$document", Text(entry.Document)),
            ("$changed", entry.Changed.UtcTicks), ("$changedBy", Text(entry.ChangedBy)),
        };

        if (existing is { } id)
        {
            Execute(_connection,
                "UPDATE entries SET source = $source, target = $target, previous_source = $previous, next_source = $next, "
                + "document = $document, changed = $changed, changed_by = $changedBy WHERE id = $id",
                [.. values, ("$id", id)]);
            return;
        }

        Execute(_connection,
            "INSERT INTO entries(pair_hash, source, target, previous_source, next_source, document, changed, changed_by) "
            + "VALUES($hash, $source, $target, $previous, $next, $document, $changed, $changedBy)",
            values);
        var entryId = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        foreach (var feature in Features(source).Distinct())
        {
            Execute(_connection, "INSERT INTO postings(feature, entry) VALUES($feature, $entry)", ("$feature", feature), ("$entry", entryId));
            Execute(_connection, "INSERT INTO features(feature, entries) VALUES($feature, 1) ON CONFLICT(feature) DO UPDATE SET entries = entries + 1", ("$feature", feature));
        }
    }

    // An entry and its index rows. Its postings are found by their primary key (feature, entry), from the source's
    // bigrams: equal sources have equal bigrams. Bigrams no entry has any more leave the index.
    private void Delete(long id, MatchScore.Normalized source)
    {
        Execute(_connection, "DELETE FROM entries WHERE id = $id", ("$id", id));
        foreach (var feature in Features(source).Distinct())
        {
            Execute(_connection, "DELETE FROM postings WHERE feature = $feature AND entry = $id", ("$feature", feature), ("$id", id));
            Execute(_connection, "UPDATE features SET entries = entries - 1 WHERE feature = $feature", ("$feature", feature));
            Execute(_connection, "DELETE FROM features WHERE feature = $feature AND entries <= 0", ("$feature", feature));
        }
    }

    // The same pair (ignoring code ids) as the entry's, if stored; the hash only narrows the search.
    private (long PairHash, long? Id, TmEntry? Stored) FindPair(MatchScore.Normalized source, TmEntry entry)
    {
        var targetKey = Ranking.TargetKey(entry);
        var pairHash = Hash($"{source.Key.Length}:{source.Key}{targetKey}");
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, source, target, previous_source, next_source, document, changed, changed_by FROM entries WHERE pair_hash = $hash";
        command.Parameters.AddWithValue("$hash", pairHash);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var stored = ReadEntry(reader);
            if (MatchScore.Normalized.Of(stored.Source).Key == source.Key && Ranking.TargetKey(stored) == targetKey)
                return (pairHash, reader.GetInt64(0), stored);
        }

        return (pairHash, null, null);
    }

    // ---------------------------------------------------------------- reading

    private static TmEntry ReadEntry(SqliteDataReader reader)
    {
        try
        {
            return new TmEntry(TmContentFormat.Read((byte[])reader[1]), TmContentFormat.Read((byte[])reader[2]))
            {
                PreviousSource = Text(reader, 3),
                NextSource = Text(reader, 4),
                Document = Text(reader, 5),
                Changed = new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
                ChangedBy = Text(reader, 7),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentOutOfRangeException or FormatException)
        {
            throw new TmFormatException("A stored entry is damaged.", ex);
        }
    }

    // Plain strings are stored like content, as raw UTF-16, so nothing in them is lost.
    private static byte[]? Text(string? text) => text is null ? null : TmContentFormat.Write(InlineContent.FromText(text));

    private static string? Text(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : TmContentFormat.Read((byte[])reader[column]).ToPlainText();

    // ---------------------------------------------------------------- the bigram index

    // Word bigrams of the normalised source, with start and end marks; numbers are one word, "#".
    internal static IEnumerable<long> Features(MatchScore.Normalized source)
    {
        var previous = "\u0002";
        foreach (var word in source.Words)
        {
            var current = word.IsNumber ? "#" : word.Text;
            yield return Hash($"{previous}\u0001{current}");
            previous = current;
        }

        yield return Hash($"{previous}\u0001\u0003");
    }

    // FNV-1a, 64 bits: stable across processes and versions, unlike string.GetHashCode.
    private static long Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return unchecked((long)hash);
    }

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
            throw new TmFormatException($"'{Path.GetFileName(path)}' can't be opened as a translation memory.", ex);
        }
    }

    private object? Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
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
