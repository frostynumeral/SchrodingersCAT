namespace SchrodingersCat.Memory;

/// <summary>
/// A termbase held in memory only: what the SQLite termbase (<see cref="SqliteTermbase"/>) must agree with, and a
/// termbase for tests. Ids follow the same rule: the next is one more than the highest given or deleted.
/// </summary>
public sealed class InMemoryTermbase : ITermbase
{
    private volatile TermIndex _index = TermIndex.Empty;
    private long _lastId;

    public InMemoryTermbase(string sourceLanguage, string targetLanguage)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceLanguage);
        ArgumentException.ThrowIfNullOrEmpty(targetLanguage);
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
    }

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    public TermIndex Index => _index;

    public TermEntryId Add(TermEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AddRange([entry])[0];
    }

    public IReadOnlyList<TermEntryId> AddRange(IEnumerable<TermEntry> entries)
    {
        var list = SqliteTermbase.Normalized(entries);
        if (list.Count == 0)
            return [];

        // Checked, as the SQLite termbase's ids are: an id past long.MaxValue would wrap to a negative one.
        var last = checked(_lastId + list.Count);
        var stored = list.Select((entry, i) => new StoredTermEntry(new TermEntryId(_lastId + 1 + i), entry)).ToList();
        _lastId = last;
        _index = _index.WithRange(stored);
        return [.. stored.Select(s => s.Id)];
    }

    public bool Update(TermEntryId id, TermEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_index.Find(id) is null)
            return false;

        _index = _index.With(id, SqliteTermbase.Normalized(entry));
        return true;
    }

    public int Delete(IReadOnlyCollection<TermEntryId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var removed = ids.Distinct().Where(id => _index.Find(id) is not null).ToList();
        if (removed.Count == 0)
            return 0;

        _lastId = Math.Max(_lastId, removed.Max(id => id.Value));
        _index = _index.WithoutRange(removed);
        return removed.Count;
    }

    /// <summary>Nothing to give back in memory.</summary>
    public bool Compact() => false;

    public void Dispose()
    {
    }
}
