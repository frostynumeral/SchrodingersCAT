namespace SchrodingersCat.Memory;

/// <summary>
/// A termbase (<c>docs/design/termbase.md</c>): the term entries of one language pair, and the index that finds them.
/// Everything is read from <see cref="Index"/>, an immutable snapshot, so reading needs no lock and may go on from
/// any thread while a write runs; the writes run one at a time.
/// </summary>
/// <remarks>
/// Every write either takes effect, and then publishes a new snapshot, or throws and leaves both the stored entries
/// and <see cref="Index"/> as they were. <see cref="TermEntry.Changed"/> is stored as UTC.
/// </remarks>
public interface ITermbase : IDisposable
{
    string SourceLanguage { get; }

    string TargetLanguage { get; }

    /// <summary>The entries as they are now; replaced by a new snapshot after each write.</summary>
    TermIndex Index { get; }

    /// <summary>
    /// Stores a new entry and returns its id: higher than any given before, deleted ones included. Nothing is
    /// merged: an entry is a concept, so equal entries (homonyms) stay apart.
    /// </summary>
    TermEntryId Add(TermEntry entry);

    /// <summary>Stores several new entries (an import) in one change, with one new snapshot. Their ids ascend in their order.</summary>
    IReadOnlyList<TermEntryId> AddRange(IEnumerable<TermEntry> entries);

    /// <summary>Replaces the whole entry with this id, which it keeps. False if there's none (any more); nothing changes then.</summary>
    bool Update(TermEntryId id, TermEntry entry);

    /// <summary>Removes the entries, in one change; ids it doesn't have, and repeated ones, are skipped. Returns how many it removed.</summary>
    int Delete(IReadOnlyCollection<TermEntryId> ids);

    /// <summary>
    /// Gives back the space deleted entries left, if it's worth it (for a file, more than a quarter of it). Returns
    /// whether it did; ids stay.
    /// </summary>
    bool Compact();
}
