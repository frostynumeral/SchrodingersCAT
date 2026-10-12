using SchrodingersCat.Core.Checks;

namespace SchrodingersCat.Memory;

/// <summary>
/// One entry's source terms, classified once, to try on many texts by the term rule (<see cref="TermIndex.Find"/>'s):
/// after a term is added or changed, which segments have it (step 3 of <c>docs/design/termbase.md</c>), without making
/// a one-entry index for it.
/// </summary>
public sealed class TermProbe
{
    private readonly TermIndex.Indexed _indexed;

    private TermProbe(TermIndex.Indexed indexed) => _indexed = indexed;

    public static TermProbe Of(TermEntry entry) => new(TermIndex.Indexed.Of(entry));

    public TermEntry Entry => _indexed.Entry;

    /// <summary>Whether one of the entry's source terms is in the text (what <see cref="Find"/> finds, stopping at the first place).</summary>
    public bool IsIn(QaText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        foreach (var pattern in _indexed.Patterns)
        {
            foreach (var _ in source.FindAll(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The entry's hit in the text, as if it were stored with <paramref name="id"/>: what an index of this entry alone
    /// finds (<see cref="TermIndex.Find"/>); null if none of its terms is there.
    /// </summary>
    public TermHit? Find(QaText source, TermEntryId id) => TermIndex.HitOf(source, id, _indexed);
}
