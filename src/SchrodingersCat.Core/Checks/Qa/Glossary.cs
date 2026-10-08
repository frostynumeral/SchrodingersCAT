using System.Xml.Linq;
using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Checks;

/// <summary>
/// One entry of a unit's glossary (the XLIFF 2 glossary module): a term and its translations. If the entry points to
/// a term annotation of the unit (<c>ref="#m1"</c>), <see cref="MarkerId"/> is that annotation's id.
/// </summary>
public sealed record GlossaryEntry(string Term, IReadOnlyList<string> Translations)
{
    public string? MarkerId { get; init; }
}

/// <summary>
/// Reads the glossary module (<c>&lt;gls:glossary&gt;</c>) from a unit's <see cref="TranslationItem.ExtensionElements"/>
/// (section 3 of <c>docs/design/qa-checks.md</c>). An entry without a term is left out; nothing is ever written back.
/// </summary>
public static class GlossaryReader
{
    public const string Namespace = "urn:oasis:names:tc:xliff:glossary:2.0";

    private static readonly XName Glossary = XName.Get("glossary", Namespace);
    private static readonly XName GlossEntry = XName.Get("glossEntry", Namespace);
    private static readonly XName TermName = XName.Get("term", Namespace);
    private static readonly XName TranslationName = XName.Get("translation", Namespace);

    public static IReadOnlyList<GlossaryEntry> Read(TranslationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var entries = new List<GlossaryEntry>();
        foreach (var entry in unit.ExtensionElements.Where(e => e.Name == Glossary).SelectMany(g => g.Elements(GlossEntry)))
        {
            // Exactly one term (the module's rule); text only.
            if (entry.Elements(TermName).ToList() is not [var termElement] || termElement.HasElements || termElement.Value.Trim() is not { Length: > 0 } term)
                continue;

            var translations = entry.Elements(TranslationName)
                .Where(t => !t.HasElements)
                .Select(t => t.Value.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // "#m1" points to a source annotation; "#t=m1" (a target one) or anything else isn't the term in the source.
            var reference = (string?)entry.Attribute("ref");
            var markerId = reference is ['#', .. var id] && !id.Contains('=', StringComparison.Ordinal) && id.Length > 0 ? id : null;
            entries.Add(new GlossaryEntry(term, translations) { MarkerId = markerId });
        }

        return entries;
    }
}
