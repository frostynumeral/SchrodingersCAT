using System.Collections.Immutable;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Text;

namespace SchrodingersCat.Memory;

/// <summary>One place an entry's source term is in a text: which term (an index into the entry's source terms), and its range of <see cref="QaText.Text"/>.</summary>
public readonly record struct TermOccurrence(int Term, int Start, int Length);

/// <summary>
/// An entry found in a text: which of its source terms are there (ascending), and every place one is (by start, the
/// longer first), one per range, under the first of the terms found there.
/// </summary>
public sealed record TermHit(TermEntryId Id, TermEntry Entry, IReadOnlyList<int> MatchedTerms, IReadOnlyList<TermOccurrence> Occurrences);

/// <summary>
/// The termbase's entries, indexed for finding them in a segment's source (section 2 of <c>docs/design/termbase.md</c>)
/// by the term rule the QA check uses (<see cref="QaText"/>'s term search), so the pane, the marks and the check agree.
/// </summary>
/// <remarks>
/// <para>
/// An index never changes: <see cref="With"/> and <see cref="Without"/> return a new one that shares everything
/// unchanged with the old, so a lookup needs no lock, and one going on while a term is added sees the termbase as it
/// was.
/// </para>
/// <para>
/// Each source term is filed under its key (<see cref="TermPattern.Key"/>): a term with words under its first word run
/// (<c>fájl</c>, <c>c</c> for <c>C#</c>, <c>net</c> for <c>.NET</c>), a term in a script without spaces, or with no
/// words, under its first folded character. A lookup probes, at each place of the source's folded text, its character
/// and the beginnings of the word run starting there; every text a term is in has its key among them, so the
/// candidates are all the terms that may be there, and each is then confirmed by the rule itself.
/// </para>
/// </remarks>
public sealed class TermIndex
{
    private static readonly Comparer<TermEntryId> ById = Comparer<TermEntryId>.Create((a, b) => a.Value.CompareTo(b.Value));

    private readonly ImmutableSortedDictionary<TermEntryId, Indexed> _entries;

    // Each key's source terms, of every entry.
    private readonly ImmutableDictionary<string, ImmutableArray<Candidate>> _byKey;

    // The longest key ever filed, so a lookup probes no longer beginnings; removing an entry leaves it, which only
    // costs a few probes.
    private readonly int _maxKeyLength;

    private TermIndex(ImmutableSortedDictionary<TermEntryId, Indexed> entries, ImmutableDictionary<string, ImmutableArray<Candidate>> byKey, int maxKeyLength)
    {
        _entries = entries;
        _byKey = byKey;
        _maxKeyLength = maxKeyLength;
    }

    public static TermIndex Empty { get; } = new(
        ImmutableSortedDictionary.Create<TermEntryId, Indexed>(ById),
        ImmutableDictionary.Create<string, ImmutableArray<Candidate>>(StringComparer.Ordinal),
        0);

    /// <summary>An index of these entries; a later entry with the same id replaces an earlier one.</summary>
    public static TermIndex Of(IEnumerable<StoredTermEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var builder = new Builder(Empty);
        foreach (var stored in entries)
        {
            ArgumentNullException.ThrowIfNull(stored, nameof(entries));
            builder.Set(stored.Id, stored.Entry);
        }

        return builder.ToIndex(recountKeys: true);
    }

    public int Count => _entries.Count;

    public TermEntry? Find(TermEntryId id) => _entries.TryGetValue(id, out var indexed) ? indexed.Entry : null;

    /// <summary>The entries in the order of their ids.</summary>
    public IEnumerable<StoredTermEntry> Entries() => _entries.Select(e => new StoredTermEntry(e.Key, e.Value.Entry));

    /// <summary>The index with this entry added, or replacing the one with its id.</summary>
    public TermIndex With(TermEntryId id, TermEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var builder = new Builder(this);
        builder.Set(id, entry);
        return builder.ToIndex(recountKeys: false);
    }

    /// <summary>The index without the entry with this id; this same index if there's none.</summary>
    public TermIndex Without(TermEntryId id) => WithoutRange([id]);

    /// <summary>The index without the entries with these ids (those it has); this same index if it has none of them.</summary>
    public TermIndex WithoutRange(IEnumerable<TermEntryId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var builder = new Builder(this);
        var removed = false;
        foreach (var id in ids)
            removed |= builder.Remove(id);
        return removed ? builder.ToIndex(recountKeys: false) : this;
    }

    /// <summary>
    /// The entries with a source term in this text, by the term rule: every entry once, every place its terms are,
    /// those that start or end in a code left out. Hits are in the order of their first place (by start, the longer
    /// first), then of their ids, so <c>file manager</c> comes before <c>file</c>, and every overlapping hit is kept.
    /// </summary>
    public IReadOnlyList<TermHit> Find(QaText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_byKey.IsEmpty || source.Text.Length == 0)
            return [];

        // Each candidate is in one bucket, and each bucket is visited once, so none is confirmed twice.
        var candidates = new List<Candidate>();
        foreach (var key in ProbeKeys(source.Prepared.Folded))
        {
            if (_byKey.TryGetValue(key, out var bucket))
                candidates.AddRange(bucket);
        }

        return Collect(source, candidates, _entries);
    }

    /// <summary>
    /// This index as the terminology check's term source (section 5): each entry found, as a record from the named
    /// termbase. It stays with this index; a termbase that changes gives the check a new one.
    /// </summary>
    public ITermSource ToTermSource(string termbaseName) => new TermSource(this, TermOrigin.OfTermbase(termbaseName));

    /// <summary>What <see cref="Find"/> finds, by trying every term of every entry: what the index must agree with.</summary>
    internal IReadOnlyList<TermHit> Scan(QaText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var candidates = _entries.SelectMany(e => e.Value.Patterns.Select((pattern, term) => new Candidate(e.Key, term, pattern)))
            .Where(c => c.Pattern.Rule != TermRule.None);
        return Collect(source, candidates, _entries);
    }

    // The keys a text is probed with: each folded character, and the beginnings of the word run from each place (as
    // long as the longest key), so every key of a term the rule may find there is among them.
    private HashSet<string> ProbeKeys(string folded)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var p = 0; p < folded.Length; p++)
        {
            keys.Add(folded[p].ToString());
            if (!Words.IsKeyChar(folded[p]))
                continue;

            var run = Words.WordRun(folded, p);
            for (var length = 2; length <= Math.Min(run.Length, _maxKeyLength); length++)
                keys.Add(run[..length]);
        }

        return keys;
    }

    // The candidates confirmed in the text, by entry, in hit order.
    private static List<TermHit> Collect(QaText source, IEnumerable<Candidate> candidates, ImmutableSortedDictionary<TermEntryId, Indexed> entries)
    {
        var found = new Dictionary<TermEntryId, List<TermOccurrence>>();
        foreach (var candidate in candidates)
        {
            foreach (var (start, length) in source.FindAll(candidate.Pattern))
            {
                if (!found.TryGetValue(candidate.Id, out var occurrences))
                    found[candidate.Id] = occurrences = [];
                occurrences.Add(new TermOccurrence(candidate.Term, start, length));
            }
        }

        var hits = new List<TermHit>(found.Count);
        foreach (var (id, occurrences) in found)
        {
            var matched = occurrences.Select(o => o.Term).Distinct().Order().ToList();
            var places = occurrences
                .OrderBy(o => o.Start).ThenByDescending(o => o.Length).ThenBy(o => o.Term)
                .DistinctBy(o => (o.Start, o.Length))
                .ToList();
            hits.Add(new TermHit(id, entries[id].Entry, new EquatableList<int>(matched), new EquatableList<TermOccurrence>(places)));
        }

        hits.Sort((a, b) =>
            a.Occurrences[0].Start != b.Occurrences[0].Start ? a.Occurrences[0].Start.CompareTo(b.Occurrences[0].Start)
            : a.Occurrences[0].Length != b.Occurrences[0].Length ? b.Occurrences[0].Length.CompareTo(a.Occurrences[0].Length)
            : a.Id.Value.CompareTo(b.Id.Value));
        return hits;
    }

    // An entry, and its source terms classified once, in the entry's order.
    private sealed record Indexed(TermEntry Entry, ImmutableArray<TermPattern> Patterns)
    {
        public static Indexed Of(TermEntry entry) => new(entry, [.. entry.Source.Select(t => TermPattern.Of(t.Text))]);
    }

    // A source term (by its index in the entry) to confirm in a text.
    private readonly record struct Candidate(TermEntryId Id, int Term, TermPattern Pattern);

    // The index's collections changed in place, then frozen into a new index; what isn't changed stays shared.
    private sealed class Builder(TermIndex from)
    {
        private readonly ImmutableSortedDictionary<TermEntryId, Indexed>.Builder _entries = from._entries.ToBuilder();
        private readonly ImmutableDictionary<string, ImmutableArray<Candidate>>.Builder _byKey = from._byKey.ToBuilder();
        private int _maxKeyLength = from._maxKeyLength;

        public void Set(TermEntryId id, TermEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            Remove(id);
            var indexed = Indexed.Of(entry);
            _entries[id] = indexed;
            for (var term = 0; term < indexed.Patterns.Length; term++)
            {
                var pattern = indexed.Patterns[term];
                if (pattern.Rule == TermRule.None)
                    continue;

                var bucket = _byKey.TryGetValue(pattern.Key, out var existing) ? existing : [];
                _byKey[pattern.Key] = bucket.Add(new Candidate(id, term, pattern));
                _maxKeyLength = Math.Max(_maxKeyLength, pattern.Key.Length);
            }
        }

        public bool Remove(TermEntryId id)
        {
            if (!_entries.TryGetValue(id, out var indexed))
                return false;

            _entries.Remove(id);
            foreach (var key in indexed.Patterns.Where(p => p.Rule != TermRule.None).Select(p => p.Key).Distinct(StringComparer.Ordinal))
            {
                var bucket = _byKey[key].RemoveAll(c => c.Id == id);
                if (bucket.IsEmpty)
                    _byKey.Remove(key);
                else
                    _byKey[key] = bucket;
            }

            return true;
        }

        public TermIndex ToIndex(bool recountKeys)
        {
            var maxKeyLength = recountKeys ? _byKey.Keys.Select(k => k.Length).DefaultIfEmpty(0).Max() : _maxKeyLength;
            return new TermIndex(_entries.ToImmutable(), _byKey.ToImmutable(), maxKeyLength);
        }
    }

    // The check's term source over one index.
    private sealed class TermSource(TermIndex index, TermOrigin origin) : ITermSource
    {
        public IReadOnlyList<TermRecord> Find(QaText source) => [.. index.Find(source).Select(h => h.Entry.ToRecord(origin))];
    }
}
