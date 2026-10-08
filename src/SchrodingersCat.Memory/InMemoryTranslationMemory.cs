using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>
/// A translation memory held in memory, scoring every entry on lookup (design section 2). The SQLite TM (step 2)
/// adds an index on top of the same scoring.
/// </summary>
public sealed class InMemoryTranslationMemory : ITranslationMemory
{
    /// <summary>Matches below this score aren't returned.</summary>
    public const int DefaultThreshold = Ranking.DefaultThreshold;

    /// <summary>At most this many matches are returned.</summary>
    public const int DefaultMaxMatches = Ranking.DefaultMaxMatches;

    // In the order stored, which breaks ties in Ranking; ids increase with it.
    private readonly List<(TmEntryId Id, TmEntry Entry, MatchScore.Normalized Source)> _entries = [];

    // Where each pair is in _entries, so storing it again replaces it in place without a scan.
    private readonly Dictionary<(string SourceKey, string TargetKey), int> _positions = [];

    private long _lastId;

    public IEnumerable<TmEntry> Entries() => [.. _entries.Select(e => e.Entry)];

    public int Count => _entries.Count;

    public void Add(TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var source = MatchScore.Normalized.Of(entry.Source);
        var pair = (source.Key, Ranking.TargetKey(entry));
        if (_positions.TryGetValue(pair, out var position))
        {
            _entries[position] = (_entries[position].Id, entry, source);
        }
        else
        {
            _positions[pair] = _entries.Count;
            _entries.Add((new TmEntryId(++_lastId), entry, source));
        }
    }

    public void AddRange(IEnumerable<TmEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
            Add(entry);
    }

    public bool Remove(TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_positions.TryGetValue((MatchScore.Normalized.Of(entry.Source).Key, Ranking.TargetKey(entry)), out var position))
            return false;

        RemoveAt([position]);
        return true;
    }

    public void Replace(TmEntry previous, TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(entry);

        if (_positions.TryGetValue((MatchScore.Normalized.Of(previous.Source).Key, Ranking.TargetKey(previous)), out var position)
            && _entries[position].Entry.Document == previous.Document)
            Remove(previous);
        Add(entry);
    }

    public IReadOnlyList<TmMatch> Lookup(
        InlineContent source,
        string? previousSource = null,
        string? nextSource = null,
        int threshold = DefaultThreshold,
        int maxMatches = DefaultMaxMatches)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Ranking.Rank(MatchScore.Normalized.Of(source), previousSource, nextSource, _entries, threshold, maxMatches);
    }

    // ---------------------------------------------------------------- maintenance

    public StoredEntry? Find(TmEntryId id) => IndexOf(id) is var i and >= 0 ? new StoredEntry(id, _entries[i].Entry) : null;

    public UpdateResult Update(TmEntryId id, InlineContent target, DateTimeOffset changed, string? changedBy)
    {
        ArgumentNullException.ThrowIfNull(target);

        var position = IndexOf(id);
        if (position < 0)
            return UpdateResult.NotFound;

        var (_, stored, source) = _entries[position];
        var entry = stored with { Target = target, Changed = changed, ChangedBy = changedBy };
        var result = UpdateResult.Updated;
        if (_positions.TryGetValue((source.Key, Ranking.TargetKey(entry)), out var other) && other != position)
        {
            RemoveAt([other]);
            position = IndexOf(id);
            result = UpdateResult.Merged;
        }

        _positions.Remove((source.Key, Ranking.TargetKey(stored)));
        _positions[(source.Key, Ranking.TargetKey(entry))] = position;
        _entries[position] = (id, entry, source);
        return result;
    }

    public int Delete(IReadOnlyCollection<TmEntryId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var doomed = ids.ToHashSet();
        var positions = Enumerable.Range(0, _entries.Count).Where(i => doomed.Contains(_entries[i].Id)).ToList();
        RemoveAt(positions);
        return positions.Count;
    }

    public BrowseResult Browse(EntryFilter filter, TmEntryId? after = null, int pageSize = ITranslationMemory.DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        return Maintenance.Page(Enumerable.Reverse(Matching(filter)).Select(e => new StoredEntry(e.Id, e.Entry)), after, pageSize);
    }

    public int DeleteMatching(EntryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return Delete([.. Matching(filter).Select(e => e.Id)]);
    }

    public IReadOnlyList<string> Documents() => Maintenance.Sorted(_entries.Select(e => e.Entry.Document));

    public IReadOnlyList<string> Users() => Maintenance.Sorted(_entries.Select(e => e.Entry.ChangedBy));

    /// <summary>Nothing to give back: memory is freed as entries go.</summary>
    public bool Compact() => false;

    // ---------------------------------------------------------------- concordance

    // One side of every entry, keyed by id, for a concordance scan.
    internal IEnumerable<(long Key, DateTimeOffset Changed, InlineContent Side)> ScanSide(ConcordanceSide side) =>
        [.. _entries.Select(e => (e.Id.Value, e.Entry.Changed, side == ConcordanceSide.Source ? e.Entry.Source : e.Entry.Target))];

    internal TmEntry Load(long key) => _entries[IndexOf(new TmEntryId(key))].Entry;

    // ---------------------------------------------------------------- helpers

    private List<(TmEntryId Id, TmEntry Entry, MatchScore.Normalized Source)> Matching(EntryFilter filter)
    {
        var shared = filter.SeveralTranslations ? Maintenance.SharedSources(_entries.Select(e => e.Entry.Source)) : null;
        return [.. _entries.Where(e => Maintenance.Matches(filter, e.Entry, shared))];
    }

    // Ids increase in the order stored, so a binary search finds one.
    private int IndexOf(TmEntryId id)
    {
        int low = 0, high = _entries.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var value = _entries[middle].Id.Value;
            if (value == id.Value)
                return middle;
            if (value < id.Value)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return -1;
    }

    // Rebuilt rather than patched, so the entries keep the order they were stored in.
    private void RemoveAt(List<int> positions)
    {
        if (positions.Count == 0)
            return;

        var doomed = positions.ToHashSet();
        var kept = _entries.Where((_, i) => !doomed.Contains(i)).ToList();
        _entries.Clear();
        _entries.AddRange(kept);
        _positions.Clear();
        for (var i = 0; i < _entries.Count; i++)
            _positions[(_entries[i].Source.Key, Ranking.TargetKey(_entries[i].Entry))] = i;
    }
}
