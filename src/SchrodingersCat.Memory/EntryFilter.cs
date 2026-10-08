namespace SchrodingersCat.Memory;

/// <summary>
/// Which entries to browse or delete (<c>docs/design/tm-maintenance.md</c>, section 2). Every condition is optional, and
/// an entry has to meet all that are set; <see cref="All"/> sets none.
/// </summary>
public sealed record EntryFilter
{
    /// <summary>Every entry.</summary>
    public static EntryFilter All { get; } = new();

    /// <summary>Words the entry has to contain, with the concordance's rules; null or empty for any text.</summary>
    public ConcordanceQuery? Text { get; init; }

    /// <summary>Where <see cref="Text"/> is looked for.</summary>
    public ConcordanceSide TextSide { get; init; }

    /// <summary>The file the entry was made in, exactly as stored (<see cref="TmEntry.Document"/>).</summary>
    public string? Document { get; init; }

    /// <summary>Who made or last changed the entry, exactly as stored (<see cref="TmEntry.ChangedBy"/>).</summary>
    public string? User { get; init; }

    /// <summary>Changed at or after this time.</summary>
    public DateTimeOffset? ChangedFrom { get; init; }

    /// <summary>Changed before this time.</summary>
    public DateTimeOffset? ChangedBefore { get; init; }

    /// <summary>Only entries whose source has more than one translation (sources compared as for an exact match).</summary>
    public bool SeveralTranslations { get; init; }

    internal bool HasText => Text is { IsEmpty: false };
}

/// <summary>One page of the entries a filter found, newest first, and how many it found in all.</summary>
public sealed record BrowseResult(IReadOnlyList<StoredEntry> Entries, int Total);

/// <summary>What <see cref="ITranslationMemory.Update"/> did.</summary>
public enum UpdateResult
{
    /// <summary>The TM has no entry with that id (any more).</summary>
    NotFound,

    /// <summary>The entry has the new translation.</summary>
    Updated,

    /// <summary>
    /// The entry has the new translation, and another entry with the same pair (ignoring code ids) was removed, as
    /// storing the pair would have replaced it.
    /// </summary>
    Merged,
}
