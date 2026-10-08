using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>A translation memory: stores translations and finds matches for a source (design sections 1 and 2).</summary>
public interface ITranslationMemory
{
    /// <summary>The number of entries.</summary>
    int Count { get; }

    /// <summary>
    /// Stores a translation. Storing a pair that's already there (the same source and target, ignoring code ids)
    /// replaces it, so its date and context are the newest; another translation of the same source is kept beside the
    /// first (decision 4).
    /// </summary>
    void Add(TmEntry entry);

    /// <summary>Stores several translations (an import), with the rules of <see cref="Add"/>.</summary>
    void AddRange(IEnumerable<TmEntry> entries);

    /// <summary>
    /// Removes the stored pair with the entry's source and target, ignoring code ids (the pair <see cref="Add"/> would
    /// replace): what re-confirming an edited segment does with its old translation. Other translations of the same
    /// source stay. False if the TM doesn't have the pair.
    /// </summary>
    bool Remove(TmEntry entry);

    /// <summary>
    /// Stores <paramref name="entry"/> in place of <paramref name="previous"/>, as one change: what re-confirming a
    /// corrected segment does. The previous pair is removed only if it was stored from the same document (its
    /// <see cref="TmEntry.Document"/>), so the same translation imported or stored from another file stays.
    /// </summary>
    void Replace(TmEntry previous, TmEntry entry);

    /// <summary>Every entry, oldest stored first (for export).</summary>
    IEnumerable<TmEntry> Entries();

    /// <summary>
    /// The best matches for a source, best first, newest first among equal scores. An exact match between the same
    /// neighbours scores 101; that needs at least one neighbour, so a lone segment's exact match stays 100.
    /// </summary>
    IReadOnlyList<TmMatch> Lookup(
        InlineContent source,
        string? previousSource = null,
        string? nextSource = null,
        int threshold = Ranking.DefaultThreshold,
        int maxMatches = Ranking.DefaultMaxMatches);
}
