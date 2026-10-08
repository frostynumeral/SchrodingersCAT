using System.Globalization;
using System.Text;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>Which side of the TM's entries a concordance search looks in.</summary>
public enum ConcordanceSide
{
    Source,
    Target,
}

/// <summary>A piece of an entry's content in a concordance result: an element, or part of a text, and whether it's part of a hit.</summary>
public sealed record HighlightPart(InlineElement Element, bool IsHit);

/// <summary>An entry with a hit; the searched side split at the hits (the other side has no hits).</summary>
public sealed record ConcordanceHit(TmEntry Entry, IReadOnlyList<HighlightPart> Source, IReadOnlyList<HighlightPart> Target)
{
    /// <summary>The entry's id in the TM, for editing or deleting it; null from a TM that has no ids.</summary>
    public TmEntryId? EntryId { get; init; }
}

/// <summary>The best hits, best first, and how many entries had a hit at all.</summary>
public sealed record ConcordanceResult(IReadOnlyList<ConcordanceHit> Hits, int Total);

/// <summary>What a concordance search looks for: the words of the text the translator typed, folded.</summary>
public sealed class ConcordanceQuery
{
    private ConcordanceQuery(string text, IReadOnlyList<string> words)
    {
        Text = text;
        Words = words;
    }

    /// <summary>The text as typed.</summary>
    public string Text { get; }

    /// <summary>No words to look for (only punctuation, or nothing): don't search.</summary>
    public bool IsEmpty => Words.Count == 0;

    internal IReadOnlyList<string> Words { get; }

    public static ConcordanceQuery Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ConcordanceQuery(text, [.. Concordance.Words(text).Select(w => w.Folded)]);
    }
}

/// <summary>
/// Concordance search (a follow-up to <c>docs/design/translation-memory.md</c>): how a word or phrase was translated
/// before, in the TM's sources or translations.
/// </summary>
/// <remarks>
/// <para><b>Words:</b> the ones <see cref="MatchScore"/> counts, without punctuation, folded: lower case, accents
/// removed, and a few letters accents don't explain (ł, ø, đ, æ, œ, ß). A hit is a run of words, with nothing but
/// codes, annotations, punctuation and whitespace between them, each starting with the query's word at that place:
/// <c>fájl</c> finds <c>fájlt</c>, <c>press ok</c> finds <c>Press &lt;b&gt;OK&lt;/b&gt;.</c>.</para>
/// <para><b>Order:</b> entries with a hit of whole words first, then the shorter text, then the newest, then the
/// order stored. Every entry is read: a TM has no index of single words (the step 2 index holds word pairs of
/// sources), so the search scans, as decided after measuring it (see the design doc).</para>
/// </remarks>
public static class Concordance
{
    /// <summary>At most this many hits are returned.</summary>
    public const int DefaultMaxHits = 100;

    /// <summary>How often a scan checks for cancellation, in entries.</summary>
    internal const int CheckEvery = 1_000;

    public static ConcordanceResult Search(
        ITranslationMemory memory,
        ConcordanceQuery query,
        ConcordanceSide side,
        int maxHits = DefaultMaxHits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHits);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.IsEmpty)
            return new ConcordanceResult([], 0);

        // The two TMs key their rows by entry id; any other is keyed by position and has no ids.
        if (memory is SqliteTranslationMemory sqlite)
            return Rank(sqlite.ScanSide(side), sqlite.Load, keysAreIds: true, query, side, maxHits, cancellationToken);
        if (memory is InMemoryTranslationMemory inMemory)
            return Rank(inMemory.ScanSide(side), inMemory.Load, keysAreIds: true, query, side, maxHits, cancellationToken);

        var entries = memory.Entries().ToList();
        return Rank(
            entries.Select((e, i) => ((long)i, e.Changed, side == ConcordanceSide.Source ? e.Source : e.Target)),
            key => entries[(int)key],
            keysAreIds: false, query, side, maxHits, cancellationToken);
    }

    // ---------------------------------------------------------------- ranking

    private readonly record struct Candidate(long Key, bool Whole, int Length, long ChangedTicks) : IComparable<Candidate>
    {
        public int CompareTo(Candidate other)
        {
            var order = other.Whole.CompareTo(Whole); // whole words first
            if (order == 0)
                order = Length.CompareTo(other.Length);
            if (order == 0)
                order = other.ChangedTicks.CompareTo(ChangedTicks); // newest first
            return order != 0 ? order : Key.CompareTo(other.Key);
        }
    }

    // Keeps the best maxHits in a sorted list (a total order, so equal entries are all kept), then loads and
    // highlights only those.
    private static ConcordanceResult Rank(
        IEnumerable<(long Key, DateTimeOffset Changed, InlineContent Side)> rows,
        Func<long, TmEntry> load,
        bool keysAreIds,
        ConcordanceQuery query,
        ConcordanceSide side,
        int maxHits,
        CancellationToken cancellationToken)
    {
        var best = new List<Candidate>();
        var total = 0;
        var read = 0;
        foreach (var (key, changed, content) in rows)
        {
            if (++read % CheckEvery == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var text = content.ToPlainText();
            if (!MayContain(text, query) || Find(content, query) is not { Count: > 0 } hits)
                continue;

            total++;
            var candidate = new Candidate(key, hits.Any(h => h.Whole), text.Length, changed.UtcTicks);
            if (best.Count == maxHits && candidate.CompareTo(best[^1]) > 0)
                continue;
            var at = best.BinarySearch(candidate);
            best.Insert(at < 0 ? ~at : at, candidate);
            if (best.Count > maxHits)
                best.RemoveAt(best.Count - 1);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ConcordanceResult(
            [.. best.Select(c => Highlight(load(c.Key), query, side) with { EntryId = keysAreIds ? new TmEntryId(c.Key) : null })], total);
    }

    /// <summary>
    /// A stored entry split at its hits for the query, as a search would return it: for browsing the TM with a text
    /// filter (<see cref="EntryFilter.Text"/>). Without a query, or one without words, nothing is a hit.
    /// </summary>
    public static ConcordanceHit Highlight(StoredEntry stored, ConcordanceQuery? query, ConcordanceSide side)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var hit = query is { IsEmpty: false }
            ? Highlight(stored.Entry, query, side)
            : new ConcordanceHit(stored.Entry, Plain(stored.Entry.Source), Plain(stored.Entry.Target));
        return hit with { EntryId = stored.Id };
    }

    private static ConcordanceHit Highlight(TmEntry entry, ConcordanceQuery query, ConcordanceSide side) =>
        side == ConcordanceSide.Source
            ? new ConcordanceHit(entry, Parts(entry.Source, Find(entry.Source, query)), Plain(entry.Target))
            : new ConcordanceHit(entry, Plain(entry.Source), Parts(entry.Target, Find(entry.Target, query)));

    // ---------------------------------------------------------------- matching

    // A word of an entry: where it is (element index, span in that element's text) and its folded form.
    internal readonly record struct Word(int Element, int Start, int Length, string Folded);

    // One occurrence of the query: its first word and how many, and whether every word was whole.
    private readonly record struct Hit(int First, int Count, bool Whole);

    // Whether the content has a hit (a filter's text condition).
    internal static bool Contains(InlineContent content, ConcordanceQuery query) =>
        MayContain(content.ToPlainText(), query) && Find(content, query).Count > 0;

    // Cheap: the whole text, folded, must contain every query word, or there's no hit.
    private static bool MayContain(string text, ConcordanceQuery query)
    {
        var folded = Fold(text);
        return query.Words.All(w => folded.Contains(w, StringComparison.Ordinal));
    }

    private static List<Hit> Find(InlineContent content, ConcordanceQuery query)
    {
        var words = Words(content);
        var hits = new List<Hit>();
        var count = query.Words.Count;
        for (var i = 0; i + count <= words.Count; i++)
        {
            var whole = true;
            var all = true;
            for (var j = 0; j < count && all; j++)
            {
                all = words[i + j].Folded.StartsWith(query.Words[j], StringComparison.Ordinal);
                whole &= words[i + j].Folded.Length == query.Words[j].Length;
            }

            if (all)
                hits.Add(new Hit(i, count, whole));
        }

        return hits;
    }

    private static List<Word> Words(InlineContent content)
    {
        var words = new List<Word>();
        var index = 0;
        foreach (var element in content)
        {
            if (element is InlineText { Text: var text })
                words.AddRange(Words(text).Select(w => w with { Element = index }));
            index++;
        }

        return words;
    }

    internal static IEnumerable<Word> Words(string text)
    {
        foreach (System.Text.RegularExpressions.Match token in MatchScore.Tokens().Matches(text))
        {
            if (IsPunctuation(token.Value))
                continue;

            var folded = Fold(token.Value);
            if (folded.Length > 0)
                yield return new Word(0, token.Index, token.Length, folded);
        }
    }

    // The tokenizer's third kind: one character that isn't a letter, mark, number or underscore.
    private static bool IsPunctuation(string token) =>
        token.Length == 1 && token[0] != '_' && CharUnicodeInfo.GetUnicodeCategory(token[0]) is not (
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber);

    // Each UTF-16 unit folded on its own (filled in as met): the same as folding the whole text, as decomposing,
    // lower-casing and dropping marks work per character, but without normalizing every text of a scan.
    private static readonly string?[] FoldedChars = new string?[char.MaxValue + 1];

    // Lower case, accents removed, and letters that don't decompose spelled out. Never fails: a lone surrogate or
    // U+FFFF (from <cp>), which Normalize rejects, stays as it is.
    internal static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 0x80)
                folded.Append(char.ToLowerInvariant(c));
            else
                folded.Append(FoldedChars[c] ??= FoldChar(c));
        }

        return folded.ToString();
    }

    private static string FoldChar(char c)
    {
        if (char.IsSurrogate(c) || c == '\uFFFF' || c == '\uFFFE')
            return c.ToString();

        string decomposed;
        try
        {
            decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return c.ToString();
        }

        var folded = new StringBuilder();
        foreach (var d in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(d);
            _ = lower switch
            {
                'ł' => folded.Append('l'),
                'ø' => folded.Append('o'),
                'đ' => folded.Append('d'),
                'æ' => folded.Append("ae"),
                'œ' => folded.Append("oe"),
                'ß' => folded.Append("ss"),
                _ => folded.Append(lower),
            };
        }

        return folded.ToString();
    }

    // ---------------------------------------------------------------- highlighting

    // The content split at the hits: each hit's words in one element, from its first word to its last (so the space
    // between two words of a hit is lit too), as hit parts; everything else as it is.
    private static List<HighlightPart> Parts(InlineContent content, List<Hit> hits)
    {
        var words = Words(content);
        var lit = new Dictionary<int, List<(int Start, int End)>>();
        foreach (var hit in hits)
        {
            foreach (var group in words.GetRange(hit.First, hit.Count).GroupBy(w => w.Element))
            {
                if (!lit.TryGetValue(group.Key, out var spans))
                    lit[group.Key] = spans = [];
                spans.Add((group.Min(w => w.Start), group.Max(w => w.Start + w.Length)));
            }
        }

        var parts = new List<HighlightPart>();
        var index = 0;
        foreach (var element in content)
        {
            if (element is InlineText { Text: var text } && lit.TryGetValue(index, out var spans))
            {
                var position = 0;
                foreach (var (start, end) in Merge(spans))
                {
                    if (start > position)
                        parts.Add(new HighlightPart(new InlineText(text[position..start]), false));
                    parts.Add(new HighlightPart(new InlineText(text[start..end]), true));
                    position = end;
                }

                if (position < text.Length)
                    parts.Add(new HighlightPart(new InlineText(text[position..]), false));
            }
            else
            {
                parts.Add(new HighlightPart(element, false));
            }

            index++;
        }

        return parts;
    }

    // Overlapping or touching spans as one.
    private static List<(int Start, int End)> Merge(List<(int Start, int End)> spans)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var (start, end) in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }

        return merged;
    }

    private static List<HighlightPart> Plain(InlineContent content) => [.. content.Select(e => new HighlightPart(e, false))];
}
