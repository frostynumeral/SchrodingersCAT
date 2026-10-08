using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>The browsing rules every TM shares (<c>docs/design/tm-maintenance.md</c>), so they can't drift apart.</summary>
internal static class Maintenance
{
    // The normalised sources more than one entry has, for EntryFilter.SeveralTranslations.
    internal static HashSet<string> SharedSources(IEnumerable<InlineContent> sources)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var key = MatchScore.Normalized.KeyOf(source);
            if (!seen.Add(key))
                shared.Add(key);
        }

        return shared;
    }

    // Whether an entry meets the filter; shared is SharedSources of every entry, when the filter asks for it.
    internal static bool Matches(EntryFilter filter, TmEntry entry, HashSet<string>? shared)
    {
        if (filter.ChangedFrom is { } from && entry.Changed < from)
            return false;
        if (filter.ChangedBefore is { } before && entry.Changed >= before)
            return false;
        if (filter.Document is not null && !string.Equals(entry.Document, filter.Document, StringComparison.Ordinal))
            return false;
        if (filter.User is not null && !string.Equals(entry.ChangedBy, filter.User, StringComparison.Ordinal))
            return false;
        return MatchesContent(filter, entry.Source, entry.Target, shared);
    }

    // The conditions on content: the text, and several translations. (The SQLite TM checks the others in SQL.)
    internal static bool MatchesContent(EntryFilter filter, InlineContent? source, InlineContent? target, HashSet<string>? shared)
    {
        if (filter.HasText && !Concordance.Contains(filter.TextSide == ConcordanceSide.Source ? source! : target!, filter.Text!))
            return false;
        return !filter.SeveralTranslations || shared!.Contains(MatchScore.Normalized.KeyOf(source!));
    }

    // A page of the matching entries (given newest first) after the given id, and how many match in all.
    internal static BrowseResult Page(IEnumerable<StoredEntry> matching, TmEntryId? after, int pageSize)
    {
        var page = new List<StoredEntry>();
        var total = 0;
        foreach (var stored in matching)
        {
            total++;
            if (page.Count < pageSize && (after is not { } last || stored.Id.Value < last.Value))
                page.Add(stored);
        }

        return new BrowseResult(page, total);
    }

    // The distinct non-null values, sorted for a list: ignoring case, then exactly.
    internal static IReadOnlyList<string> Sorted(IEnumerable<string?> values) =>
        [.. values.OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ThenBy(v => v, StringComparer.Ordinal)];
}
