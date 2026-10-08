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

    /// <summary>
    /// Deleting at least this share of the TM at once recounts the index instead of updating it per entry: on 200,000
    /// entries, one by one costs about 0.3 ms an entry, at once about 0.6 s plus a little (step 1 of the maintenance
    /// design), so they cost the same at about 2%.
    /// </summary>
    public const double BulkDeleteShare = 0.02;

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

        var candidates = new List<(TmEntryId, TmEntry, MatchScore.Normalized)>();
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
                candidates.Add((new TmEntryId(reader.GetInt64(0)), entry, MatchScore.Normalized.Of(entry.Source)));
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

    // ---------------------------------------------------------------- maintenance

    public StoredEntry? Find(TmEntryId id) =>
        Query($"SELECT {Columns} FROM entries WHERE id = $id", ("$id", id.Value)).FirstOrDefault();

    public UpdateResult Update(TmEntryId id, InlineContent target, DateTimeOffset changed, string? changedBy)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var transaction = _connection.BeginTransaction();
        if (Find(id) is not { Entry: var stored })
            return UpdateResult.NotFound;

        var entry = stored with { Target = target, Changed = changed, ChangedBy = changedBy };
        var source = MatchScore.Normalized.Of(entry.Source);
        var (pairHash, other, _) = FindPair(source, entry);
        var result = UpdateResult.Updated;
        if (other is { } otherId && otherId != id.Value)
        {
            Delete(otherId, source);
            result = UpdateResult.Merged;
        }

        // The source stays, so its bigrams do.
        Execute(_connection,
            "UPDATE entries SET pair_hash = $hash, target = $target, changed = $changed, changed_by = $changedBy WHERE id = $id",
            ("$hash", pairHash), ("$target", TmContentFormat.Write(target)), ("$changed", changed.UtcTicks), ("$changedBy", Text(changedBy)),
            ("$id", id.Value));
        transaction.Commit();
        return result;
    }

    public int Delete(IReadOnlyCollection<TmEntryId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        using var transaction = _connection.BeginTransaction();
        var removed = DeleteIds(ids);
        transaction.Commit();
        return removed;
    }

    public BrowseResult Browse(EntryFilter filter, TmEntryId? after = null, int pageSize = ITranslationMemory.DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var (where, parameters) = Conditions(filter);
        if (!NeedsScan(filter))
        {
            // Nothing to check on content: SQLite counts and pages on its own.
            var total = Convert.ToInt32(Scalar($"SELECT count(*) FROM entries WHERE {where}", parameters), CultureInfo.InvariantCulture);
            var page = Query(
                $"SELECT {Columns} FROM entries WHERE {where} AND id < $after ORDER BY id DESC LIMIT $limit",
                [.. parameters, ("$after", after?.Value ?? long.MaxValue), ("$limit", pageSize)]).ToList();
            return new BrowseResult(page, total);
        }

        // Every matching id (newest first) for the total; whole entries only for the page.
        var ids = MatchingIds(filter, where, parameters);
        return new BrowseResult(
            [.. ids.Where(id => after is not { } last || id.Value < last.Value).Take(pageSize).Select(id => Find(id) ?? throw new TmFormatException($"Entry {id.Value} disappeared."))],
            ids.Count);
    }

    public int DeleteMatching(EntryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var transaction = _connection.BeginTransaction();
        var (where, parameters) = Conditions(filter);
        List<TmEntryId> ids = NeedsScan(filter)
            ? MatchingIds(filter, where, parameters)
            : [.. Ids($"SELECT id FROM entries WHERE {where}", parameters)];
        var removed = DeleteIds(ids);
        transaction.Commit();
        return removed;
    }

    public IReadOnlyList<string> Documents() => Maintenance.Sorted(DistinctTexts("document"));

    public IReadOnlyList<string> Users() => Maintenance.Sorted(DistinctTexts("changed_by"));

    /// <summary>
    /// Rewrites the file (<c>VACUUM</c>) if more than a quarter of its entries were deleted since it was last rewritten.
    /// (Counted, because SQLite's free pages don't show it: deleting scattered entries leaves pages half empty rather
    /// than free.) Ids, and so the order stored, stay: <c>VACUUM</c> keeps the row ids of a table with an integer
    /// primary key.
    /// </summary>
    public bool Compact()
    {
        var deleted = Convert.ToInt64(Scalar("SELECT coalesce((SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'deleted_since_compact'), 0)"), CultureInfo.InvariantCulture);
        if (deleted * 4 <= deleted + Count)
            return false;

        Execute(_connection, "VACUUM");
        Execute(_connection, "DELETE FROM meta WHERE key = 'deleted_since_compact'");
        return true;
    }

    // ---------------------------------------------------------------- concordance

    // One side of every entry, for a concordance scan: only the searched column is read and decoded.
    internal IEnumerable<(long Key, DateTimeOffset Changed, InlineContent Side)> ScanSide(ConcordanceSide side)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = side == ConcordanceSide.Source
            ? "SELECT id, changed, source FROM entries ORDER BY id"
            : "SELECT id, changed, target FROM entries ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            InlineContent content;
            try
            {
                content = TmContentFormat.Read((byte[])reader[2]);
            }
            catch (InvalidCastException ex)
            {
                throw new TmFormatException("A stored entry is damaged.", ex);
            }

            yield return (reader.GetInt64(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), content);
        }
    }

    // A whole entry by its id, for the hits a scan kept.
    internal TmEntry Load(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, source, target, previous_source, next_source, document, changed, changed_by FROM entries WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : throw new TmFormatException($"Entry {id} disappeared during a search.");
    }

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

        // The next id after both the last one stored and the last one deleted, so an id is never reused: a stale id
        // in the app can't reach another entry.
        Execute(_connection,
            "INSERT INTO entries(id, pair_hash, source, target, previous_source, next_source, document, changed, changed_by) "
            + "VALUES(max(coalesce((SELECT max(id) FROM entries), 0), "
            + "coalesce((SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'last_deleted_id'), 0)) + 1, "
            + "$hash, $source, $target, $previous, $next, $document, $changed, $changedBy)",
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
        RecordDeleted(id, 1);
        foreach (var feature in Features(source).Distinct())
        {
            Execute(_connection, "DELETE FROM postings WHERE feature = $feature AND entry = $id", ("$feature", feature), ("$id", id));
            Execute(_connection, "UPDATE features SET entries = entries - 1 WHERE feature = $feature", ("$feature", feature));
            Execute(_connection, "DELETE FROM features WHERE feature = $feature AND entries <= 0", ("$feature", feature));
        }
    }

    // Remembers the highest id deleted, so Store never hands it out again, and how many entries went since the file was
    // last compacted. Both in meta, which takes new keys without a schema change.
    private void RecordDeleted(long highestId, int count)
    {
        Execute(_connection,
            "INSERT INTO meta(key, value) VALUES('last_deleted_id', $id) "
            + "ON CONFLICT(key) DO UPDATE SET value = CAST(max(CAST(value AS INTEGER), CAST(excluded.value AS INTEGER)) AS TEXT)",
            ("$id", highestId.ToString(CultureInfo.InvariantCulture)));
        Execute(_connection,
            "INSERT INTO meta(key, value) VALUES('deleted_since_compact', $count) "
            + "ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + CAST(excluded.value AS INTEGER) AS TEXT)",
            ("$count", count.ToString(CultureInfo.InvariantCulture)));
    }

    // Deletes entries in the open transaction; the ids it doesn't have are skipped. A few go one by one, through their
    // bigrams' index rows. Many (BulkDeleteShare of the TM or more) go at once: their index rows in one pass over
    // the postings, then the bigram counts recounted, which costs about the same whatever the number deleted.
    private int DeleteIds(IReadOnlyCollection<TmEntryId> ids)
    {
        if (ids.Count == 0)
            return 0;

        if (ids.Count < Count * BulkDeleteShare)
        {
            var removed = 0;
            foreach (var id in ids.Distinct())
            {
                if (Scalar("SELECT source FROM entries WHERE id = $id", ("$id", id.Value)) is not byte[] blob)
                    continue;
                Delete(id.Value, MatchScore.Normalized.Of(ReadContent(blob)));
                removed++;
            }

            return removed;
        }

        Execute(_connection, "CREATE TEMP TABLE IF NOT EXISTS doomed(id INTEGER PRIMARY KEY)");
        Execute(_connection, "DELETE FROM doomed");
        foreach (var id in ids)
            Execute(_connection, "INSERT OR IGNORE INTO doomed(id) VALUES($id)", ("$id", id.Value));

        var highest = Scalar("SELECT max(id) FROM entries WHERE id IN (SELECT id FROM doomed)");
        var count = ExecuteCount("DELETE FROM entries WHERE id IN (SELECT id FROM doomed)");
        if (highest is long highestId)
            RecordDeleted(highestId, count);
        Execute(_connection, "DELETE FROM postings WHERE entry IN (SELECT id FROM doomed)");
        Execute(_connection, "DELETE FROM features");
        Execute(_connection, "INSERT INTO features(feature, entries) SELECT feature, count(*) FROM postings GROUP BY feature");
        Execute(_connection, "DELETE FROM doomed");
        return count;
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

    private const string Columns = "id, source, target, previous_source, next_source, document, changed, changed_by";

    private IEnumerable<StoredEntry> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new StoredEntry(new TmEntryId(reader.GetInt64(0)), ReadEntry(reader));
    }

    private IEnumerable<TmEntryId> Ids(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new TmEntryId(reader.GetInt64(0));
    }

    // The text and several translations need content decoded; the other conditions are SQL (Conditions).
    private static bool NeedsScan(EntryFilter filter) => filter.HasText || filter.SeveralTranslations;

    // The ids of the entries meeting the filter, newest first. Only the columns the content conditions need are read
    // and decoded, as for a concordance search; SharedSources reads every source.
    private List<TmEntryId> MatchingIds(EntryFilter filter, string where, (string, object?)[] parameters)
    {
        var shared = filter.SeveralTranslations ? Maintenance.SharedSources([.. ReadColumn("SELECT source FROM entries")]) : null;
        var needsSource = filter.SeveralTranslations || (filter.HasText && filter.TextSide == ConcordanceSide.Source);
        var needsTarget = filter.HasText && filter.TextSide == ConcordanceSide.Target;

        using var command = Command(
            $"SELECT id, {(needsSource ? "source" : "NULL")}, {(needsTarget ? "target" : "NULL")} FROM entries WHERE {where} ORDER BY id DESC",
            parameters);
        using var reader = command.ExecuteReader();
        var ids = new List<TmEntryId>();
        while (reader.Read())
        {
            var source = needsSource ? ReadContent((byte[])reader[1]) : null;
            var target = needsTarget ? ReadContent((byte[])reader[2]) : null;
            if (Maintenance.MatchesContent(filter, source, target, shared))
                ids.Add(new TmEntryId(reader.GetInt64(0)));
        }

        return ids;
    }

    // The conditions SQLite checks: dates on the plain changed column, and document and user on their stored bytes
    // (a string is always stored as the same bytes, so no row has to be decoded).
    private static (string Sql, (string, object?)[] Parameters) Conditions(EntryFilter filter)
    {
        var conditions = new List<string> { "1" };
        var parameters = new List<(string, object?)>();
        if (filter.Document is { } document)
        {
            conditions.Add("document = $document");
            parameters.Add(("$document", Text(document)));
        }

        if (filter.User is { } user)
        {
            conditions.Add("changed_by = $user");
            parameters.Add(("$user", Text(user)));
        }

        if (filter.ChangedFrom is { } from)
        {
            conditions.Add("changed >= $from");
            parameters.Add(("$from", from.UtcTicks));
        }

        if (filter.ChangedBefore is { } before)
        {
            conditions.Add("changed < $before");
            parameters.Add(("$before", before.UtcTicks));
        }

        return (string.Join(" AND ", conditions), [.. parameters]);
    }

    private IEnumerable<InlineContent> ReadColumn(string sql)
    {
        using var command = Command(sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return ReadContent((byte[])reader[0]);
    }

    // Equal strings are stored as equal bytes, so SQLite can find the distinct ones before they're decoded.
    private IEnumerable<string> DistinctTexts(string column) =>
        [.. ReadColumn($"SELECT DISTINCT {column} FROM entries WHERE {column} IS NOT NULL").Select(c => c.ToPlainText())];

    private static InlineContent ReadContent(byte[] blob)
    {
        try
        {
            return TmContentFormat.Read(blob);
        }
        catch (InvalidCastException ex)
        {
            throw new TmFormatException("A stored entry is damaged.", ex);
        }
    }

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

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private int ExecuteCount(string sql)
    {
        using var command = Command(sql);
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
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
