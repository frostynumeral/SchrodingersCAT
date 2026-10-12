using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
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
/// Each source term is filed under the key of the place it starts at (<see cref="TermPattern.Key"/>), and is looked
/// for only at the places of the source with that key, so a common first word under which thousands of terms are
/// filed costs a comparison per term there, not a search of the text per term. A term with words is filed under its
/// first word run (<c>fájl</c>) and tried at each of the source's words, under the beginnings of whose run it is
/// filed. The others are tried at places of the source's folded text: a term in a script without spaces, or with no
/// words, filed under its first folded character, probed at every place; a term with punctuation under its
/// characters up to its first space (<c>c#</c>, <c>.net</c>, <c>wi-fi</c>; apostrophes and hyphens as one), probed
/// with the beginnings of the characters at a place up to whitespace, code marks skipped as the rule skips them (so
/// <c>Wi-Fi</c> is found in <c>W⟨ph/⟩i-Fi</c> too), where a word begins. Each candidate is then confirmed at that place
/// by the rule itself.
/// </para>
/// <para>
/// Keys are 64-bit hashes, so probing makes no strings, and a bit per hash rules out most of
/// a text's beginnings, which are no key at all, without a lookup in the dictionary.
/// </para>
/// </remarks>
public sealed class TermIndex
{
    private static readonly Comparer<TermEntryId> ById = Comparer<TermEntryId>.Create((a, b) => a.Value.CompareTo(b.Value));

    // Occurrences by start, the longer first, then by term, so the first of each range is under its first term.
    private static readonly Comparison<TermOccurrence> ByPlace = (a, b) =>
        a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Length != b.Length ? b.Length.CompareTo(a.Length) : a.Term.CompareTo(b.Term);

    private readonly ImmutableSortedDictionary<TermEntryId, Indexed> _entries;

    // Each key's source terms, of every entry: those found by their words, tried at the source's words, and the others,
    // tried at places of its folded text.
    private readonly Buckets.Frozen _byWord;
    private readonly Buckets.Frozen _byPlace;

    // The longest key ever filed, so a lookup probes no longer beginnings; removing an entry leaves it, which only
    // costs a few probes.
    private readonly int _maxKeyLength;

    private TermIndex(ImmutableSortedDictionary<TermEntryId, Indexed> entries, Buckets.Frozen byWord, Buckets.Frozen byPlace, int maxKeyLength)
    {
        _entries = entries;
        _byWord = byWord;
        _byPlace = byPlace;
        _maxKeyLength = maxKeyLength;
    }

    public static TermIndex Empty { get; } =
        new(ImmutableSortedDictionary.Create<TermEntryId, Indexed>(ById), Buckets.Frozen.Empty, Buckets.Frozen.Empty, 0);

    /// <summary>An index of these entries; a later entry with the same id replaces an earlier one.</summary>
    public static TermIndex Of(IEnumerable<StoredTermEntry> entries) => Empty.Build(entries) ?? Empty;

    public int Count => _entries.Count;

    public TermEntry? Find(TermEntryId id) => _entries.TryGetValue(id, out var indexed) ? indexed.Entry : null;

    /// <summary>The entries in the order of their ids.</summary>
    public IEnumerable<StoredTermEntry> Entries() => _entries.Select(e => new StoredTermEntry(e.Key, e.Value.Entry));

    /// <summary>The index with this entry added, or replacing the one with its id.</summary>
    public TermIndex With(TermEntryId id, TermEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var builder = new Builder(this);
        builder.Set(id, Indexed.Of(entry));
        return builder.ToIndex();
    }

    /// <summary>
    /// The index with these entries added, or replacing those with their ids (a later one with the same id replacing an
    /// earlier one); this same index if there are none. One new index for them all, as for an import.
    /// </summary>
    public TermIndex WithRange(IEnumerable<StoredTermEntry> entries) => Build(entries) ?? this;

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
        return removed ? builder.ToIndex() : this;
    }

    /// <summary>
    /// The entries with a source term in this text, by the term rule: every entry once, every place its terms are,
    /// those that start or end in a code left out. Hits are in the order of their first place (by start, the longer
    /// first), then of their ids, so <c>file manager</c> comes before <c>file</c>, and every overlapping hit is kept.
    /// </summary>
    public IReadOnlyList<TermHit> Find(QaText source) => [.. FindIndexed(source).Select(h => h.ToHit())];

    /// <summary>
    /// This index as the terminology check's term source (section 5): each entry found, as a record from the named
    /// termbase. It stays with this index; a termbase that changes gives the check a new one. It keeps the record of
    /// each entry it found (about 800 bytes each), so keep a source only as long as what it checks, such as one
    /// document, not for as long as the termbase is open.
    /// </summary>
    /// <param name="termbaseName">The termbase's name, for the check's messages.</param>
    /// <param name="previous">
    /// The source the same check used before the termbase changed, if any: the new source keeps using its records (an
    /// entry changed since gets a new one), so re-checking after a write doesn't make them all again.
    /// </param>
    public ITermSource ToTermSource(string termbaseName, ITermSource? previous = null)
    {
        var origin = TermOrigin.OfTermbase(termbaseName);
        return new TermSource(this, origin, previous is TermSource { Origin: var same } kept && same.Equals(origin) ? kept.Records : new(ReferenceEqualityComparer.Instance));
    }

    /// <summary>What <see cref="Find"/> finds, by trying every term of every entry: what the index must agree with.</summary>
    internal IReadOnlyList<TermHit> Scan(QaText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var found = new Dictionary<TermEntryId, Found>();
        foreach (var (id, indexed) in _entries)
        {
            for (var term = 0; term < indexed.Patterns.Length; term++)
            {
                foreach (var range in source.FindAll(indexed.Patterns[term]))
                    Add(found, source, new Candidate(id, term, indexed), range);
            }
        }

        return [.. Hits(found).Select(h => h.ToHit())];
    }

    // The hits, each with its entry as indexed, which keeps the entry's record for the check.
    private List<IndexedHit> FindIndexed(QaText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if ((_byWord.IsEmpty && _byPlace.IsEmpty) || source.Text.Length == 0)
            return [];

        // A term has one key, and the keys probed at one word or place are all different, so no term is tried twice
        // at one place (but for two keys with the same hash, and a range found twice is kept once).
        var found = new Dictionary<TermEntryId, Found>();
        var prepared = source.Prepared;
        var limit = Math.Min(Words.MaxKeyLength, _maxKeyLength);
        Span<long> keys = stackalloc long[Words.MaxKeyLength + 1];
        if (!_byWord.IsEmpty)
        {
            var words = prepared.Words;
            for (var i = 0; i < words.Count; i++)
            {
                if (words[i].Folded.Length == 0)
                    continue;

                foreach (var key in keys[..WordKeys(words[i].Folded, limit, keys)])
                {
                    if (!_byWord.TryGetValue(key, out var bucket))
                        continue;

                    foreach (var candidate in bucket)
                    {
                        // A phrase's second word's first character rules most phrases out before the whole is compared.
                        if (candidate.Second >= 0 && (i + 1 >= words.Count || words[i + 1].Folded is not { Length: > 0 } next || next[0] != candidate.Second))
                            continue;
                        if (!Words.PhraseAt(words, candidate.Pattern.Phrase, i))
                            continue;

                        var last = words[i + candidate.Pattern.Phrase.Count - 1];
                        Add(found, source, candidate, (words[i].Start, last.Start + last.Length - words[i].Start));
                    }
                }
            }
        }

        if (!_byPlace.IsEmpty)
        {
            var text = prepared.Text;
            var folded = prepared.Folded;
            var origin = prepared.Origin;
            for (var at = 0; at < folded.Length; at++)
            {
                // Every rule starts a term only where a character of the text starts.
                if (at > 0 && origin[at] == origin[at - 1])
                    continue;

                var count = PlaceKeys(folded, at, limit, keys);
                for (var k = 0; k < count; k++)
                {
                    if (!_byPlace.TryGetValue(keys[k], out var bucket))
                        continue;

                    // The keys after the first are a term with punctuation's, which starts only where a word may begin
                    // (Words.PunctuatedAt checks it too), told only when a key is there.
                    if (k > 0 && !Words.IsBoundary(text, origin[at], origin[at] - 1))
                        break;

                    foreach (var candidate in bucket)
                    {
                        var pattern = candidate.Pattern;
                        var range = pattern.Rule == TermRule.Folded
                            ? Words.FoldedAt(prepared, pattern.Needle, at)
                            : Words.PunctuatedAt(prepared, pattern.Needle, pattern.EndsInPunctuation, at);
                        if (range is { } r)
                            Add(found, source, candidate, r);
                    }
                }
            }
        }

        return Hits(found);
    }

    // A new index with these entries set; null if there are none. Classifying the entries is the slow part of loading a
    // termbase, so chunks of them are classified on other threads while the next is read (by one thread at a time, as
    // the entries may come from a database reader), then filed in their order on this thread.
    private TermIndex? Build(IEnumerable<StoredTermEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var chunks = new SortedList<int, (StoredTermEntry[] Entries, Indexed[] Indexed)>();
        try
        {
            Parallel.ForEach(Partitioner.Create(Chunks(entries), EnumerablePartitionerOptions.NoBuffering), chunk =>
            {
                var indexed = Array.ConvertAll(chunk.Entries, e => Indexed.Of(e.Entry));
                lock (chunks)
                    chunks.Add(chunk.Number, (chunk.Entries, indexed));
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            // What reading the entries threw (a damaged termbase, cancelling), as if they had been read here.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        if (chunks.Count == 0)
            return null;

        var builder = new Builder(this);
        foreach (var (stored, indexed) in chunks.Values)
        {
            for (var i = 0; i < stored.Length; i++)
                builder.Set(stored[i].Id, indexed[i]);
        }

        return builder.ToIndex();
    }

    // How many entries are classified together.
    private const int ChunkSize = 4_096;

    private static IEnumerable<(int Number, StoredTermEntry[] Entries)> Chunks(IEnumerable<StoredTermEntry> entries)
    {
        var number = 0;
        foreach (var chunk in entries.Chunk(ChunkSize))
        {
            if (Array.IndexOf(chunk, null) >= 0)
                throw new ArgumentNullException(nameof(entries));
            yield return (number++, chunk);
        }
    }

    // The keys a word is probed with, as hashes, from the shortest: the beginnings of its first run of key characters
    // (Words.WordRun), as long as the longest key; or its first character if that isn't one. Returns how many.
    private static int WordKeys(string folded, int limit, Span<long> keys)
    {
        if (!Words.IsKeyChar(folded[0]))
        {
            keys[0] = Step(Offset, folded[0]);
            return 1;
        }

        var count = 0;
        var hash = Offset;
        for (var i = 0; i < folded.Length && count < limit; i++)
        {
            if (Words.IsKeyChar(folded[i]))
                keys[count++] = hash = Step(hash, folded[i]);
            else if (folded[i] != QaText.CodeMark)
                break;
        }

        return count;
    }

    // The keys a place of the folded text is probed with, as hashes: first its character (for a term without spaces or
    // without words), then, from the shortest, the beginnings of its characters as a term with punctuation has them
    // (Words.Variant: apostrophes and hyphens as one) up to whitespace, code marks after the first skipped, as the rule
    // skips them, as long as the longest key. Returns how many.
    private static int PlaceKeys(string folded, int at, int limit, Span<long> keys)
    {
        keys[0] = Step(Offset, folded[at]);
        var count = 1;
        var hash = Punctuated;
        for (var i = at; i < folded.Length && count <= limit; i++)
        {
            if (i > at && folded[i] == QaText.CodeMark)
                continue;
            if (char.IsWhiteSpace(folded[i]))
                break;

            keys[count++] = hash = Step(hash, Words.Variant(folded[i]));
        }

        return count;
    }

    // The key a term is filed under, as a hash of TermPattern.Key from the seed of its kind, so a place it starts at
    // has it among its probed keys (WordKeys, PlaceKeys).
    private static (long Hash, int Length) KeyOf(TermPattern pattern)
    {
        var hash = pattern.Rule == TermRule.Punctuated ? Punctuated : Offset;
        foreach (var c in pattern.Key)
            hash = Step(hash, c);
        return (hash, pattern.Key.Length);
    }

    // FNV-1a, 64 bits, so a key is looked up by its hash, without a string made for it. Two keys with the same hash
    // would only make more candidates, each confirmed by the rule.
    private const long Offset = unchecked((long)14695981039346656037UL);

    // The seed of the keys of terms with punctuation, so they differ from the characters' keys in the same dictionary.
    private const long Punctuated = unchecked((long)0x9E3779B97F4A7C15UL);

    private static long Step(long hash, char c) => unchecked((hash ^ c) * 1099511628211L);

    // An occurrence, unless it starts or ends in a code (QaText's rule for the term search).
    private static void Add(Dictionary<TermEntryId, Found> found, QaText source, Candidate candidate, (int Start, int Length) range)
    {
        if (source.IsInCode(range.Start) || source.IsInCode(range.Start + range.Length - 1))
            return;

        if (!found.TryGetValue(candidate.Id, out var entry))
            found[candidate.Id] = entry = new Found(candidate.Indexed);
        entry.Occurrences.Add(new TermOccurrence(candidate.Term, range.Start, range.Length));
    }

    // The occurrences found, by entry, in hit order.
    private static List<IndexedHit> Hits(Dictionary<TermEntryId, Found> found)
    {
        var hits = new List<IndexedHit>(found.Count);
        foreach (var (id, (indexed, occurrences)) in found)
        {
            int[] matched;
            if (occurrences.Count == 1)
            {
                matched = [occurrences[0].Term];
            }
            else
            {
                // Every term found, then one occurrence per range, under the first term found there.
                matched = [.. occurrences.Select(o => o.Term).Distinct().Order()];
                occurrences.Sort(ByPlace);
                var kept = 1;
                for (var i = 1; i < occurrences.Count; i++)
                {
                    if (occurrences[i].Start != occurrences[kept - 1].Start || occurrences[i].Length != occurrences[kept - 1].Length)
                        occurrences[kept++] = occurrences[i];
                }

                occurrences.RemoveRange(kept, occurrences.Count - kept);
            }

            hits.Add(new IndexedHit(id, indexed, new EquatableList<int>(matched), new EquatableList<TermOccurrence>(occurrences)));
        }

        hits.Sort((a, b) =>
            a.Occurrences[0].Start != b.Occurrences[0].Start ? a.Occurrences[0].Start.CompareTo(b.Occurrences[0].Start)
            : a.Occurrences[0].Length != b.Occurrences[0].Length ? b.Occurrences[0].Length.CompareTo(a.Occurrences[0].Length)
            : a.Id.Value.CompareTo(b.Id.Value));
        return hits;
    }

    // An entry found, and its occurrences so far.
    private sealed record Found(Indexed Indexed)
    {
        public List<TermOccurrence> Occurrences { get; } = [];

        public void Deconstruct(out Indexed indexed, out List<TermOccurrence> occurrences) => (indexed, occurrences) = (Indexed, Occurrences);
    }

    private sealed record IndexedHit(TermEntryId Id, Indexed Indexed, IReadOnlyList<int> MatchedTerms, IReadOnlyList<TermOccurrence> Occurrences)
    {
        public TermHit ToHit() => new(Id, Indexed.Entry, MatchedTerms, Occurrences);
    }

    // An entry, and its source terms classified once, in the entry's order.
    private sealed class Indexed(TermEntry entry, ImmutableArray<TermPattern> patterns)
    {
        public TermEntry Entry { get; } = entry;

        public ImmutableArray<TermPattern> Patterns { get; } = patterns;

        public static Indexed Of(TermEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return new(entry, [.. entry.Source.Select(t => TermPattern.Of(t.Text))]);
        }
    }

    // A source term (by its index in the entry) to confirm in a text; for a phrase, also its second word's first
    // character (-1 for anything else).
    private readonly record struct Candidate(TermEntryId Id, int Term, Indexed Indexed)
    {
        public TermPattern Pattern { get; } = Indexed.Patterns[Term];

        public int Second { get; } = Indexed.Patterns[Term] is { Rule: TermRule.Words, Phrase: { Count: > 1 } phrase } ? phrase[1][0] : -1;
    }

    // Which keys a dictionary of buckets may have: a bit per key, by the top bits of its hash, set when a key is filed
    // and never cleared (a key whose terms were all removed only costs a lookup), so most probes need no lookup. A
    // filter never changes once an index has it; a builder copies it the first time it sets a bit.
    private sealed class KeyFilter
    {
        private const int Bits = 20;

        private readonly ulong[] _bits;

        private KeyFilter(ulong[] bits) => _bits = bits;

        public static KeyFilter Empty { get; } = new(new ulong[1 << (Bits - 6)]);

        public bool MayHave(long key)
        {
            var bit = (ulong)key >> (64 - Bits);
            return (_bits[bit >> 6] & (1UL << (int)(bit & 63))) != 0;
        }

        // A filter with the key's bit set: this one if it is set already or this one is the builder's own (own), else a
        // copy, which then is.
        public KeyFilter With(long key, ref bool own)
        {
            if (MayHave(key))
                return this;

            var filter = own ? this : new KeyFilter((ulong[])_bits.Clone());
            own = true;
            var bit = (ulong)key >> (64 - Bits);
            filter._bits[bit >> 6] |= 1UL << (int)(bit & 63);
            return filter;
        }
    }

    // The index's collections changed in place, then frozen into a new index; what isn't changed stays shared.
    private sealed class Builder(TermIndex from)
    {
        private readonly ImmutableSortedDictionary<TermEntryId, Indexed>.Builder _entries = from._entries.ToBuilder();
        private readonly Buckets _byWord = new(from._byWord);
        private readonly Buckets _byPlace = new(from._byPlace);
        private int _maxKeyLength = from._maxKeyLength;

        public void Set(TermEntryId id, Indexed indexed)
        {
            Remove(id);
            _entries[id] = indexed;
            for (var term = 0; term < indexed.Patterns.Length; term++)
            {
                var pattern = indexed.Patterns[term];
                if (pattern.Rule == TermRule.None)
                    continue;

                var (key, length) = KeyOf(pattern);
                BucketsOf(pattern).Add(key, new Candidate(id, term, indexed));
                _maxKeyLength = Math.Max(_maxKeyLength, length);
            }
        }

        public bool Remove(TermEntryId id)
        {
            if (!_entries.TryGetValue(id, out var indexed))
                return false;

            _entries.Remove(id);
            foreach (var pattern in indexed.Patterns.Where(p => p.Rule != TermRule.None))
                BucketsOf(pattern).Remove(KeyOf(pattern).Hash, id);
            return true;
        }

        public TermIndex ToIndex() => new(_entries.ToImmutable(), _byWord.ToFrozen(), _byPlace.ToFrozen(), _maxKeyLength);

        private Buckets BucketsOf(TermPattern pattern) => pattern.Rule == TermRule.Words ? _byWord : _byPlace;
    }

    // One dictionary of buckets being changed. A key's bucket is copied into a list the first time it is touched and
    // frozen once, and removals wait for that, so filing many terms under one key (a common first word) costs no copy
    // of its bucket per term.
    private sealed class Buckets(Buckets.Frozen from)
    {
        private readonly ImmutableDictionary<long, ImmutableArray<Candidate>>.Builder _byKey = from.ByKey.ToBuilder();
        private readonly Dictionary<long, List<Candidate>> _lists = [];
        private readonly Dictionary<long, HashSet<TermEntryId>> _removed = [];
        private KeyFilter _filter = from.Filter;
        private bool _ownFilter;

        public void Add(long key, Candidate candidate)
        {
            // An id set again in one batch (an update) has its waiting removal done now, before it's filed anew.
            var list = ListOf(key);
            if (_removed.TryGetValue(key, out var removed) && removed.Remove(candidate.Id))
                list.RemoveAll(c => c.Id == candidate.Id);
            list.Add(candidate);
            _filter = _filter.With(key, ref _ownFilter);
        }

        public void Remove(long key, TermEntryId id)
        {
            ListOf(key);
            if (!_removed.TryGetValue(key, out var removed))
                _removed[key] = removed = [];
            removed.Add(id);
        }

        public Frozen ToFrozen()
        {
            foreach (var (key, list) in _lists)
            {
                if (_removed.TryGetValue(key, out var removed) && removed.Count > 0)
                    list.RemoveAll(c => removed.Contains(c.Id));
                if (list.Count == 0)
                    _byKey.Remove(key);
                else
                    _byKey[key] = [.. list];
            }

            _lists.Clear();
            _removed.Clear();
            _ownFilter = false;
            return new Frozen(_byKey.ToImmutable(), _filter);
        }

        // The key's bucket as a list to change, copied the first time.
        private List<Candidate> ListOf(long key)
        {
            if (!_lists.TryGetValue(key, out var list))
                _lists[key] = list = _byKey.TryGetValue(key, out var bucket) ? [.. bucket] : [];
            return list;
        }

        // An index's buckets, and which keys they may have.
        public sealed record Frozen(ImmutableDictionary<long, ImmutableArray<Candidate>> ByKey, KeyFilter Filter)
        {
            public static Frozen Empty { get; } = new(ImmutableDictionary.Create<long, ImmutableArray<Candidate>>(), KeyFilter.Empty);

            public bool IsEmpty => ByKey.IsEmpty;

            public bool TryGetValue(long key, out ImmutableArray<Candidate> bucket)
            {
                bucket = default;
                return Filter.MayHave(key) && ByKey.TryGetValue(key, out bucket);
            }
        }
    }

    // The check's term source over one index. It keeps the record of each entry it found: the check works out what it
    // needs from a record once (TermRecord.Checked), so a record made anew for every segment would cost that every
    // time. A record costs about 800 bytes, nearly as much as its entry in the index, so the records stay with the
    // source, not the index, and go with it (step 3 of docs/design/termbase.md). Any thread may look up; two may both
    // make a record, and one is kept.
    private sealed class TermSource(TermIndex index, TermOrigin origin, ConcurrentDictionary<Indexed, TermRecord> records) : ITermSource
    {
        public TermOrigin Origin => origin;

        // By the entry's place in the index, which a changed entry doesn't keep, so it never gets its old record (which
        // stays until the sources sharing it go). Shared with the sources that follow this one (ToTermSource's previous).
        public ConcurrentDictionary<Indexed, TermRecord> Records => records;

        public IReadOnlyList<TermRecord> Find(QaText source) =>
            [.. index.FindIndexed(source).Select(h => records.GetOrAdd(h.Indexed, static (indexed, origin) => indexed.Entry.ToRecord(origin), origin))];
    }
}
