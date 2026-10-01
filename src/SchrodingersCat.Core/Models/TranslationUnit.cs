namespace SchrodingersCat.Core.Models;

/// <summary>
/// A self-contained piece of translatable content (a string resource, a paragraph, …).
/// Corresponds to an XLIFF 2.0 <c>&lt;unit&gt;</c>. Its text is split into <see cref="Segments"/>;
/// comments live at unit level, because that is where they are anchored in XLIFF.
/// </summary>
public sealed class TranslationUnit
{
    /// <summary>Unit id, unique within its file. Required.</summary>
    public required string Id { get; set; }

    /// <summary>Optional human-readable name, e.g. the resource key.</summary>
    public string? Name { get; set; }

    public List<Segment> Segments { get; } = [];

    public List<Note> Notes { get; } = [];

    /// <summary>
    /// The native code behind each inline code, keyed by the id its <see cref="InlineCode.DataRef"/> points to
    /// (XLIFF <c>&lt;originalData&gt;</c>). Characters that are invalid in XML are stored decoded.
    /// </summary>
    public Dictionary<string, string> OriginalData { get; } = new(StringComparer.Ordinal);

    /// <summary>Full source text of the unit (all segments, including ignorables).</summary>
    public InlineContent Source => new(Segments.SelectMany(s => s.Source));

    /// <summary>Full target text; untranslated segments fall back to their source text, as in a preview.</summary>
    public InlineContent Target => new(Segments.SelectMany(s => s.Target ?? s.Source));

    /// <summary>The segments the translator actually edits (ignorables excluded).</summary>
    public IEnumerable<Segment> TranslatableSegments => Segments.Where(s => !s.IsIgnorable);
}
