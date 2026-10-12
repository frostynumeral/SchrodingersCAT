namespace SchrodingersCat.Core.Checks;

/// <summary>Where a <see cref="TermRecord"/> comes from: the file's glossary, or a termbase, by its name.</summary>
public sealed record TermOrigin
{
    private TermOrigin(string? termbase) => Termbase = termbase;

    /// <summary>The unit's glossary module (<see cref="GlossaryReader"/>).</summary>
    public static TermOrigin FileGlossary { get; } = new((string?)null);

    /// <summary>The termbase's name, as messages show it; null for <see cref="FileGlossary"/>.</summary>
    public string? Termbase { get; }

    public static TermOrigin OfTermbase(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new TermOrigin(name);
    }
}

/// <summary>
/// What the terminology check compares (section 5 of <c>docs/design/termbase.md</c>): one concept's source terms
/// (synonyms), the translations it allows, those it forbids, and where it comes from. A forbidden translation never
/// satisfies the check, even if <see cref="Translations"/> lists it too. If the record is for a term annotation of the
/// unit (<c>ref="#m1"</c>), <see cref="MarkerId"/> is that annotation's id, and the annotation's text is the term.
/// The lists are copied, so a record can't change after the check has read it.
/// </summary>
public sealed class TermRecord
{
    public TermRecord(IReadOnlyList<string> terms, IReadOnlyList<string> translations, TermOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(origin);

        Terms = [.. terms];
        Translations = [.. translations];
        Origin = origin;
    }

    /// <summary>The source terms: the term and its synonyms, the preferred one first.</summary>
    public IReadOnlyList<string> Terms { get; }

    public IReadOnlyList<string> Translations { get; }

    public TermOrigin Origin { get; }

    public IReadOnlyList<string> ForbiddenTranslations
    {
        get;
        init => field = value is null ? throw new ArgumentNullException(nameof(value)) : [.. value];
    } = [];

    public string? MarkerId { get; init; }

    /// <summary>
    /// What the terminology check works out from the record, once however many segments it checks: a termbase's term
    /// source keeps the records it found, so a source with many terms doesn't classify them for every segment again.
    /// Two threads may both work it out, with the same result.
    /// </summary>
    internal TerminologyCheck.RecordTerms Checked => field ??= TerminologyCheck.RecordTerms.Of(this);

    /// <summary>A glossary entry as a record from the file.</summary>
    public static TermRecord Of(GlossaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new TermRecord([entry.Term], entry.Translations, TermOrigin.FileGlossary) { MarkerId = entry.MarkerId };
    }
}

/// <summary>
/// Term records from outside the file, such as a termbase: those whose source terms may be in a segment's source. A
/// source may return more than are there; the check finds each term itself, so a source can't make it disagree with
/// the pane and the marks.
/// </summary>
public interface ITermSource
{
    IReadOnlyList<TermRecord> Find(QaText source);
}
