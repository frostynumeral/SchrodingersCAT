using SchrodingersCat.Core.Checks;
using SchrodingersCat.Core.Models;
using SchrodingersCat.Core.Text;

namespace SchrodingersCat.Memory;

/// <summary>
/// A term's status, as TBX marks it (section 1 of <c>docs/design/termbase.md</c>). <see cref="Unspecified"/> is a
/// status of its own, so a term the client gave no status isn't exported as preferred.
/// </summary>
public enum TermStatus
{
    Unspecified,
    Preferred,
    Admitted,

    /// <summary>A translation never inserted and never satisfying the check; a source synonym still found.</summary>
    Forbidden,
}

/// <summary>One term of an entry, in plain text. A blank term is allowed, and never found.</summary>
public sealed record Term(string Text, TermStatus Status = TermStatus.Unspecified)
{
    public string Text { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } =
        Text ?? throw new ArgumentNullException(nameof(Text));

    public TermStatus Status { get; init => field = Defined(value); } = Defined(Status);

    /// <summary>About this term: usage, register.</summary>
    public string? Note { get; init; }

    private static TermStatus Defined(TermStatus status) =>
        Enum.IsDefined(status) ? status : throw new ArgumentOutOfRangeException(nameof(status), status, "Not a term status.");
}

/// <summary>
/// A termbase entry (section 1 of <c>docs/design/termbase.md</c>): one concept, with its source terms (the term and
/// its synonyms, at least one) and its translations (perhaps none yet). The termbase knows the language pair; entries
/// don't repeat it. The lists are copied, so an entry can't change behind the index's back, and compared by value.
/// </summary>
/// <param name="Note">About the concept: definition, context, the client's instruction.</param>
public sealed record TermEntry(IReadOnlyList<Term> Source, IReadOnlyList<Term> Target, string? Note, DateTimeOffset Changed, string? ChangedBy)
{
    /// <summary>The term, and its synonyms.</summary>
    public IReadOnlyList<Term> Source { get; init => field = Checked(value, allowEmpty: false); } = Checked(Source, allowEmpty: false);

    /// <summary>The term's translations.</summary>
    public IReadOnlyList<Term> Target { get; init => field = Checked(value, allowEmpty: true); } = Checked(Target, allowEmpty: true);

    /// <summary>
    /// The entry as the terminology check compares it (section 5): every source term, forbidden synonyms too (they
    /// still name the concept), the preferred ones first; the translations it allows; and those it forbids.
    /// </summary>
    public TermRecord ToRecord(TermOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        return new TermRecord(
            [.. Source.OrderBy(t => t.Status == TermStatus.Preferred ? 0 : 1).Select(t => t.Text)],
            [.. Target.Where(t => t.Status != TermStatus.Forbidden).Select(t => t.Text)],
            origin)
        {
            ForbiddenTranslations = [.. Target.Where(t => t.Status == TermStatus.Forbidden).Select(t => t.Text)],
        };
    }

    /// <summary>
    /// Whether two terms are the same term, as the terminology check counts them: folded (case and accents ignored),
    /// whitespace collapsed. Blank terms are the same as each other.
    /// </summary>
    public static bool SameTerm(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Words.SameTermKey(a) == Words.SameTermKey(b);
    }

    private static EquatableList<Term> Checked(IReadOnlyList<Term> terms, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var list = new EquatableList<Term>(terms);
        if (!allowEmpty && list.Count == 0)
            throw new ArgumentException("An entry needs at least one source term.", nameof(terms));
        if (list.Any(t => t is null))
            throw new ArgumentException("Terms cannot be null.", nameof(terms));
        return list;
    }
}

/// <summary>
/// An entry's id in its termbase: stable while the entry is stored, and, as for the TM's ids (<see cref="TmEntryId"/>),
/// never reused for another entry of the same termbase, so the editor can point at an entry.
/// </summary>
public readonly record struct TermEntryId(long Value);

/// <summary>An entry with its id.</summary>
public sealed record StoredTermEntry(TermEntryId Id, TermEntry Entry);
