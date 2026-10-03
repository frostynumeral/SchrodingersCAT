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
