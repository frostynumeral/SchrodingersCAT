using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>The lookup rules every TM shares, so they can't drift apart: scoring, context, threshold and order.</summary>
public static class Ranking
{
    /// <summary>Matches below this score aren't returned.</summary>
    public const int DefaultThreshold = 70;

    /// <summary>At most this many matches are returned.</summary>
    public const int DefaultMaxMatches = 5;

    internal static IReadOnlyList<TmMatch> Rank(
        MatchScore.Normalized query,
        string? previousSource,
        string? nextSource,
        IEnumerable<(TmEntryId Id, TmEntry Entry, MatchScore.Normalized Source)> candidates,
        int threshold,
        int maxMatches)
    {
        var hasContext = !string.IsNullOrWhiteSpace(previousSource) || !string.IsNullOrWhiteSpace(nextSource);
        return [.. candidates
            .Select(c =>
            {
                var score = MatchScore.Compare(query, c.Source);
                if (score == MatchScore.Exact && hasContext
                    && MatchScore.SameContext(previousSource, c.Entry.PreviousSource) && MatchScore.SameContext(nextSource, c.Entry.NextSource))
                    score = MatchScore.Context;
                return new TmMatch(c.Entry, score) { EntryId = c.Id };
            })
            .Where(m => m.Score >= threshold)
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Entry.Changed)
            .Take(maxMatches)];
    }

    /// <summary>What makes two targets the same translation: equal ignoring code ids (and annotations).</summary>
    internal static string TargetKey(TmEntry entry) => MatchScore.Normalized.Of(entry.Target).Key;
}
