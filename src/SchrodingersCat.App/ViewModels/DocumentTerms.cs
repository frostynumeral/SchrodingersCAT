using SchrodingersCat.App.Services;
using SchrodingersCat.Core.Checks;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// The termbase as one open document sees it (step 3's decision in <c>docs/design/termbase.md</c>): the snapshot its
/// marks and terms list read, and the term source its checks use. One per document, so the check's records go with
/// the document, never kept for the whole session; after each new snapshot of the same open termbase the source is
/// made from the previous one, which keeps its records.
/// </summary>
public sealed class DocumentTerms
{
    // The open termbase Index is a snapshot of; null when Index is.
    private OpenTermbase? _termbase;

    /// <summary>The termbase's entries; null without a termbase, while it loads, or if it doesn't serve the document.</summary>
    public TermIndex? Index { get; private set; }

    /// <summary>The termbase's name, for the tooltips; null when <see cref="Index"/> is.</summary>
    public string? Name { get; private set; }

    /// <summary>The terminology check's term source over <see cref="Index"/>; null when it is.</summary>
    public ITermSource? Source { get; private set; }

    /// <summary>
    /// Uses a new snapshot of <paramref name="termbase"/> (null for none). The source takes over the previous one's
    /// records only if that was a snapshot of the same open termbase: another termbase, even one with the same name
    /// (or the same file opened again), never finds them, so they would only pile up until the document goes.
    /// </summary>
    internal void Use(TermIndex? index, OpenTermbase? termbase)
    {
        if (index is not null)
            ArgumentNullException.ThrowIfNull(termbase);
        else
            termbase = null;
        if (ReferenceEquals(index, Index) && ReferenceEquals(termbase, _termbase))
            return;

        Source = index is null ? null : index.ToTermSource(termbase!.Name, ReferenceEquals(termbase, _termbase) ? Source : null);
        Index = index;
        Name = termbase?.Name;
        _termbase = termbase;
    }
}
