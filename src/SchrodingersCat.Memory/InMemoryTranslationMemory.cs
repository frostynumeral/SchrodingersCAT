using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>
/// A translation memory held in memory, scoring every entry on lookup (design section 2). The SQLite TM (step 2)
/// adds an index on top of the same scoring.
/// </summary>
public sealed class InMemoryTranslationMemory
{
    /// <summary>Matches below this score aren't returned.</summary>
    public const int DefaultThreshold = 70;

    /// <summary>At most this many matches are returned.</summary>
    public const int DefaultMaxMatches = 5;

    private readonly List<(TmEntry Entry, MatchScore.Normalized Source, string TargetKey)> _entries = [];

    public IReadOnlyList<TmEntry> Entries => [.. _entries.Select(e => e.Entry)];

    /// <summary>
    /// Stores a translation. Storing a pair that's already there (the same source and target, ignoring code ids)
    /// replaces it, so its date and context are the newest; another translation of the same source is kept beside the
    /// first (decision 4).
    /// </summary>
    public void Add(TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var source = MatchScore.Normalized.Of(entry.Source);
        var targetKey = MatchScore.Normalized.Of(entry.Target).Key;
        _entries.RemoveAll(e => e.Source.Key == source.Key && e.TargetKey == targetKey);
        _entries.Add((entry, source, targetKey));
    }

    /// <summary>
    /// The best matches for a source, best first, newest first among equal scores. An exact match between the same
    /// neighbours scores 101; that needs at least one neighbour, so a lone segment's exact match stays 100.
    /// </summary>
    public IReadOnlyList<TmMatch> Lookup(
        InlineContent source,
        string? previousSource = null,
        string? nextSource = null,
        int threshold = DefaultThreshold,
        int maxMatches = DefaultMaxMatches)
    {
        ArgumentNullException.ThrowIfNull(source);

        var query = MatchScore.Normalized.Of(source);
        var hasContext = !string.IsNullOrWhiteSpace(previousSource) || !string.IsNullOrWhiteSpace(nextSource);
        return [.. _entries
            .Select(e =>
            {
                var score = MatchScore.Compare(query, e.Source);
                if (score == MatchScore.Exact && hasContext
                    && MatchScore.SameContext(previousSource, e.Entry.PreviousSource) && MatchScore.SameContext(nextSource, e.Entry.NextSource))
                    score = MatchScore.Context;
                return new TmMatch(e.Entry, score);
            })
            .Where(m => m.Score >= threshold)
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Entry.Changed)
            .Take(maxMatches)];
    }
}
