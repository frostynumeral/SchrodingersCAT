using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Memory;

/// <summary>
/// One translated segment in a translation memory (design section 1). Content keeps its codes, so a match brings them
/// along. The TM knows the language pair; entries don't repeat it.
/// </summary>
public sealed record TmEntry(InlineContent Source, InlineContent Target)
{
    /// <summary>The plain text of the segment before this one, for context matches (101%).</summary>
    public string? PreviousSource { get; init; }

    /// <summary>The plain text of the segment after this one, for context matches (101%).</summary>
    public string? NextSource { get; init; }

    /// <summary>The file the translation was made in.</summary>
    public string? Document { get; init; }

    public DateTimeOffset Changed { get; init; }

    public string? ChangedBy { get; init; }
}

/// <summary>A TM entry found for a source, with its score: 0–99 fuzzy, 100 exact, 101 exact in the same context.</summary>
public sealed record TmMatch(TmEntry Entry, int Score)
{
    public bool IsExact => Score >= MatchScore.Exact;

    /// <summary>Where the match comes from, for display: the TM's name, or the XLIFF file's own origin; null if unknown.</summary>
    public string? Origin { get; init; }
}
