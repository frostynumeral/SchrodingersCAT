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

    private readonly List<(TmEntry Entry, MatchScore.Normalized Source)> _entries = [];

    // Where each pair is in _entries, so storing it again replaces it in place without a scan.
    private readonly Dictionary<(string SourceKey, string TargetKey), int> _positions = [];

    public IReadOnlyList<TmEntry> Entries => [.. _entries.Select(e => e.Entry)];

    public int Count => _entries.Count;

    public void Add(TmEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var source = MatchScore.Normalized.Of(entry.Source);
        var pair = (source.Key, Ranking.TargetKey(entry));
        if (_positions.TryGetValue(pair, out var position))
        {
            _entries[position] = (entry, source);
        }
        else
        {
            _positions[pair] = _entries.Count;
            _entries.Add((entry, source));
        }
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
}
