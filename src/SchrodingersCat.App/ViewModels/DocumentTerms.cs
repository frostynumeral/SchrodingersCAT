using SchrodingersCat.Core.Checks;
using SchrodingersCat.Memory;

namespace SchrodingersCat.App.ViewModels;

/// <summary>
/// The termbase as one open document sees it (step 3's decision in <c>docs/design/termbase.md</c>): the snapshot its
/// marks and terms list read, and the term source its checks use. One per document, so the check's records go with
/// the document, never kept for the whole session; after each new snapshot the source is made from the previous one,
/// which keeps its records.
/// </summary>
public sealed class DocumentTerms
{
    /// <summary>The termbase's entries; null without a termbase, while it loads, or if it doesn't serve the document.</summary>
    public TermIndex? Index { get; private set; }

    /// <summary>The termbase's name, for the tooltips; null when <see cref="Index"/> is.</summary>
    public string? Name { get; private set; }

    /// <summary>The terminology check's term source over <see cref="Index"/>; null when it is.</summary>
    public ITermSource? Source { get; private set; }

    /// <summary>Uses a new snapshot (null for none). The source takes over the previous one's records.</summary>
    internal void Use(TermIndex? index, string? name)
    {
        if (ReferenceEquals(index, Index) && name == Name)
            return;

        Source = index is null ? null : index.ToTermSource(name ?? throw new ArgumentNullException(nameof(name)), Source);
        Index = index;
        Name = index is null ? null : name;
    }
}
