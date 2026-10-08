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

    // ---------------------------------------------------------------- maintenance (docs/design/tm-maintenance.md)

    /// <summary>The entry with this id; null if the TM has none (any more).</summary>
    StoredEntry? Find(TmEntryId id);

    /// <summary>
    /// Gives an entry a new translation, changed at <paramref name="changed"/> by <paramref name="changedBy"/>; its source,
    /// document and context stay. If the new pair equals another entry's (ignoring code ids), that entry is removed,
    /// as storing the pair would have replaced it (decision 4 of the TM design).
    /// </summary>
    UpdateResult Update(TmEntryId id, InlineContent target, DateTimeOffset changed, string? changedBy);

    /// <summary>Removes the entries, in one change; ids it doesn't have are skipped. Returns how many it removed.</summary>
    int Delete(IReadOnlyCollection<TmEntryId> ids);

    /// <summary>
    /// The entries the filter finds, newest first (the order stored, backwards), as a page of at most
    /// <paramref name="pageSize"/> after <paramref name="after"/> (the last id of the previous page), and how many it
    /// finds in all. Paging by id, not by position, so deleting entries between pages doesn't skip or repeat any.
    /// </summary>
    BrowseResult Browse(EntryFilter filter, TmEntryId? after = null, int pageSize = DefaultPageSize);

    /// <summary>Removes every entry the filter finds, in one change. Returns how many.</summary>
    int DeleteMatching(EntryFilter filter);

    /// <summary>The documents entries were made in, sorted.</summary>
    IReadOnlyList<string> Documents();

    /// <summary>The users who made or changed entries, sorted.</summary>
    IReadOnlyList<string> Users();

    /// <summary>
    /// Gives back the space deleted entries left, if it's worth it (for a file, more than a quarter of it). Returns
    /// whether it did; slow for a large TM, so run it after a bulk delete, not after each one.
    /// </summary>
    bool Compact();

    /// <summary>The page size of <see cref="Browse"/> by default.</summary>
    const int DefaultPageSize = 200;

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
